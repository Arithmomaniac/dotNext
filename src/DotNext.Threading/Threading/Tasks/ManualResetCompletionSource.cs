using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ValueTaskSourceOnCompletedFlags = System.Threading.Tasks.Sources.ValueTaskSourceOnCompletedFlags;

namespace DotNext.Threading.Tasks;

/// <summary>
/// Represents base class for producer of value task.
/// </summary>
[SuppressMessage("Usage", "CA1001", Justification = "CTS is disposed automatically when passing through lifecycle of the completion source")]
public abstract class ManualResetCompletionSource : IThreadPoolWorkItem
{
    private readonly Action<object?, CancellationToken> cancellationCallback;
    private readonly bool runContinuationsAsynchronously, isConsumptionCallbackProvided;
    private CancellationTokenRegistration tokenTracker, timeoutTracker;
    private CancellationTokenSource? timeoutSource;

    // task management
    private Continuation continuation;
    private object? completionData;
    private ExecutionContext? context;
    private protected short version;
    private volatile ManualResetCompletionSourceStatus status;

    private protected ManualResetCompletionSource(bool runContinuationsAsynchronously)
    {
        this.runContinuationsAsynchronously = runContinuationsAsynchronously;
        version = short.MinValue;
        isConsumptionCallbackProvided = new Action(AfterConsumed).Method.DeclaringType != typeof(ManualResetCompletionSource);

        // cached callback to avoid further allocations
        cancellationCallback = CancellationRequested;
    }

    private protected object SyncRoot => cancellationCallback;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void CancellationRequested(object? expectedVersion, CancellationToken token)
    {
        Debug.Assert(expectedVersion is short);

        // due to concurrency, this method can be called after Reset or twice
        // that's why we need to skip the call if token doesn't match (call after Reset)
        // or completed flag is set (call twice with the same token)
        if (status is ManualResetCompletionSourceStatus.Activated)
        {
            lock (SyncRoot)
            {
                if (status is ManualResetCompletionSourceStatus.Activated && (short)expectedVersion == version)
                {
                    if (timeoutSource?.Token == token)
                        CompleteAsTimedOut();
                    else
                        CompleteAsCanceled(token);
                }
            }
        }
    }

    private protected void StartTrackingCancellation(TimeSpan timeout, CancellationToken token)
    {
        // box current token once and only if needed
        var tokenHolder = default(IEquatable<short>);
        if (timeout > TimeSpan.Zero)
        {
            timeoutSource ??= new();
            timeoutTracker = timeoutSource.Token.UnsafeRegister(cancellationCallback, tokenHolder = version);
            timeoutSource.CancelAfter(timeout);
        }

        if (token.CanBeCanceled)
        {
            tokenTracker = token.UnsafeRegister(cancellationCallback, tokenHolder ?? version);
        }
    }

    private protected abstract void CompleteAsTimedOut();

    private protected abstract void CompleteAsCanceled(CancellationToken token);

    private protected void StopTrackingCancellation()
    {
        Debug.Assert(Monitor.IsEntered(SyncRoot));

        // Dispose() cannot be used here because it's a blocking call that could lead to deadlock
        // because of concurrency with CancellationRequested
        tokenTracker.Unregister();
        tokenTracker = default;

        timeoutTracker.Unregister();
        timeoutTracker = default;

        if (timeoutSource is not null && !timeoutSource.TryReset())
        {
            timeoutSource.Dispose();
            timeoutSource = null;
        }
    }

    private protected void InvokeContinuation()
    {
        Debug.Assert(Monitor.IsEntered(SyncRoot));

        if (continuation)
        {
            if (context is null)
            {
                continuation.Invoke(runContinuationsAsynchronously, flowExecutionContext: false);
            }
            else
            {
                ExecutionContext.Run(
                    context,
                    static arg =>
                    {
                        Debug.Assert(arg is ManualResetCompletionSource);

                        var source = Unsafe.As<ManualResetCompletionSource>(arg);
                        source.continuation.Invoke(source.runContinuationsAsynchronously, flowExecutionContext: true);
                    },
                    this);

                context = null;
            }

            continuation = default;
        }
    }

    private protected virtual void ResetCore()
    {
        Debug.Assert(Monitor.IsEntered(SyncRoot));

        version += 1;
        status = ManualResetCompletionSourceStatus.WaitForActivation;
        context = null;
        completionData = null;
        continuation = default;
    }

    /// <summary>
    /// Resets the state of the source.
    /// </summary>
    /// <remarks>
    /// This methods acts as a barrier for completion.
    /// It means that calling of this method guarantees that the task
    /// cannot be completed by the previously linked timeout or cancellation token.
    /// </remarks>
    /// <returns>The version of the incompleted task.</returns>
    public short Reset()
    {
        short result;

        lock (SyncRoot)
        {
            StopTrackingCancellation();
            ResetCore();
            result = version;
        }

        return result;
    }

    /// <summary>
    /// Attempts to reset the state of this source.
    /// </summary>
    /// <param name="token">The version of the incompleted task.</param>
    /// <returns><see langword="true"/> if the state was reset successfully; otherwise, <see langword="false"/>.</returns>
    public bool TryReset(out short token)
    {
        bool result;
        if (result = Monitor.TryEnter(SyncRoot))
        {
            try
            {
                StopTrackingCancellation();
                ResetCore();
                token = version;
            }
            finally
            {
                Monitor.Exit(SyncRoot);
            }
        }
        else
        {
            token = default;
        }

        return result;
    }

    /// <summary>
    /// Invokes when this source is ready to reuse.
    /// </summary>
    /// <seealso cref="CompletionData"/>
    protected virtual void AfterConsumed()
    {
    }

    /// <summary>
    /// Gets a value passed to the manual completion method.
    /// </summary>
    protected object? CompletionData => completionData;

    /// <inheritdoc />
    void IThreadPoolWorkItem.Execute() => AfterConsumed();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private protected void OnConsumed()
    {
        status = ManualResetCompletionSourceStatus.Consumed;

        if (isConsumptionCallbackProvided)
            ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: true);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private protected void OnCompleted(object? completionData)
    {
        this.completionData = completionData;
        status = ManualResetCompletionSourceStatus.WaitForConsumption;
    }

    private void OnCompleted(in Continuation continuation, short token, bool flowExecutionContext)
    {
        string errorMessage;

        // fast path - monitor lock is not needed
        if (token != version)
        {
            errorMessage = ExceptionMessages.InvalidSourceToken;
            goto invalid_state;
        }

        switch (status)
        {
            default:
                errorMessage = ExceptionMessages.InvalidSourceState;
                goto invalid_state;
            case ManualResetCompletionSourceStatus.WaitForConsumption:
                goto execute_inplace;
            case ManualResetCompletionSourceStatus.Activated:
                break;
        }

        lock (SyncRoot)
        {
            // avoid running continuation inside of the lock
            if (token != version)
            {
                errorMessage = ExceptionMessages.InvalidSourceToken;
                goto invalid_state;
            }

            switch (status)
            {
                default:
                    errorMessage = ExceptionMessages.InvalidSourceState;
                    goto invalid_state;
                case ManualResetCompletionSourceStatus.WaitForConsumption:
                    goto execute_inplace;
                case ManualResetCompletionSourceStatus.Activated:
                    break;
            }

            this.continuation = continuation;
            context = flowExecutionContext ? ExecutionContext.Capture() : null;
            goto exit;
        }

    execute_inplace:
        continuation.Invoke(runContinuationsAsynchronously, flowExecutionContext);

    exit:
        return;
    invalid_state:
        throw new InvalidOperationException(errorMessage);
    }

    private protected void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
    {
        OnCompleted(
            new Continuation(continuation, state, (flags & ValueTaskSourceOnCompletedFlags.UseSchedulingContext) is not 0),
            token,
            (flags & ValueTaskSourceOnCompletedFlags.FlowExecutionContext) is not 0);
    }

    /// <summary>
    /// Attempts to complete the task unsuccessfully.
    /// </summary>
    /// <param name="e">The exception to be returned to the consumer.</param>
    /// <returns><see langword="true"/> if the result is completed successfully; <see langword="false"/> if the task has been canceled or timed out.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TrySetException(Exception e) => TrySetException(null, e);

    /// <summary>
    /// Attempts to complete the task unsuccessfully.
    /// </summary>
    /// <param name="completionData">The data to be saved in <see cref="CompletionData"/> property that can be accessed from within <see cref="AfterConsumed"/> method.</param>
    /// <param name="e">The exception to be returned to the consumer.</param>
    /// <returns><see langword="true"/> if the result is completed successfully; <see langword="false"/> if the task has been canceled or timed out.</returns>
    public abstract bool TrySetException(object? completionData, Exception e);

    /// <summary>
    /// Attempts to complete the task unsuccessfully.
    /// </summary>
    /// <param name="token">The canceled token.</param>
    /// <returns><see langword="true"/> if the result is completed successfully; <see langword="false"/> if the task has been canceled or timed out.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TrySetCanceled(CancellationToken token) => TrySetCanceled(null, token);

    /// <summary>
    /// Attempts to complete the task unsuccessfully.
    /// </summary>
    /// <param name="completionData">The data to be saved in <see cref="CompletionData"/> property that can be accessed from within <see cref="AfterConsumed"/> method.</param>
    /// <param name="token">The canceled token.</param>
    /// <returns><see langword="true"/> if the result is completed successfully; <see langword="false"/> if the task has been canceled or timed out.</returns>
    public abstract bool TrySetCanceled(object? completionData, CancellationToken token);

    /// <summary>
    /// Gets the status of this source.
    /// </summary>
    public ManualResetCompletionSourceStatus Status => status;

    private protected bool CanBeCompleted => status is ManualResetCompletionSourceStatus.WaitForActivation or ManualResetCompletionSourceStatus.Activated;

    /// <summary>
    /// Gets a value indicating that this source is in signaled (completed) state.
    /// </summary>
    /// <remarks>
    /// This property returns <see langword="true"/> if <see cref="Status"/> is <see cref="ManualResetCompletionSourceStatus.WaitForConsumption"/>
    /// or <see cref="ManualResetCompletionSourceStatus.Consumed"/>.
    /// </remarks>
    public bool IsCompleted => status >= ManualResetCompletionSourceStatus.WaitForConsumption;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void PrepareTaskCore(TimeSpan timeout, CancellationToken token)
    {
        Debug.Assert(Monitor.IsEntered(SyncRoot));

        if (timeout == default)
        {
            CompleteAsTimedOut();
        }
        else if (token.IsCancellationRequested)
        {
            CompleteAsCanceled(token);
        }
        else
        {
            status = ManualResetCompletionSourceStatus.Activated;
            StartTrackingCancellation(timeout, token);
        }
    }

    private protected bool PrepareTask(TimeSpan timeout, CancellationToken token)
    {
        if (timeout.Ticks is < 0L and not Timeout.InfiniteTicks)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        bool result;

        // The task can be created for the completed source. This workaround is needed for AsyncBridge methods
        lock (SyncRoot)
        {
            switch (status)
            {
                case ManualResetCompletionSourceStatus.WaitForActivation:
                    PrepareTaskCore(timeout, token);
                    goto case ManualResetCompletionSourceStatus.WaitForConsumption;
                case ManualResetCompletionSourceStatus.WaitForConsumption:
                    result = true;
                    break;
                default:
                    result = false;
                    break;
            }
        }

        return result;
    }

    [DoesNotReturn]
    [StackTraceHidden]
    private protected static void InvalidSourceStateDetected()
        => throw new InvalidOperationException(ExceptionMessages.InvalidSourceState);

    // encapsulates continuation and its execution logic
    [StructLayout(LayoutKind.Auto)]
    private readonly struct Continuation
    {
        private readonly Action<object?> action;
        private readonly object? state, schedulingContext;

        internal Continuation(Action<object?> action, object? state, bool useSchedulingContext)
        {
            Debug.Assert(action is not null);

            this.action = action;
            this.state = state;

            // capture scheduling context
            schedulingContext = useSchedulingContext ? CaptureContext() : null;

            static object? CaptureContext()
            {
                object? schedulingContext = SynchronizationContext.Current;
                if (schedulingContext is null || schedulingContext.GetType() == typeof(SynchronizationContext))
                {
                    var scheduler = TaskScheduler.Current;
                    schedulingContext = ReferenceEquals(scheduler, TaskScheduler.Default) ? null : scheduler;
                }

                return schedulingContext;
            }
        }

        internal void Invoke(bool runAsynchronously, bool flowExecutionContext)
        {
            switch (schedulingContext)
            {
                case null when runAsynchronously:
                    if (flowExecutionContext)
                        ThreadPool.QueueUserWorkItem(action, state, preferLocal: true);
                    else
                        ThreadPool.UnsafeQueueUserWorkItem(action, state, preferLocal: true);
                    break;
                case SynchronizationContext context:
                    context.Post(action.Invoke, state);
                    break;
                case TaskScheduler scheduler:
                    Task.Factory.StartNew(action, state, CancellationToken.None, TaskCreationOptions.DenyChildAttach, scheduler);
                    break;
                default:
                    action(state);
                    break;
            }
        }

        public static bool operator true(in Continuation continuation) => continuation.action is not null;

        public static bool operator false(in Continuation continuation) => continuation.action is null;
    }
}