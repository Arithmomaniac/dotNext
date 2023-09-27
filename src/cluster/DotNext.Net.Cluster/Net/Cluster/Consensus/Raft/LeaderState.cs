﻿using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace DotNext.Net.Cluster.Consensus.Raft;

using Diagnostics;
using IO.Log;
using Membership;
using Runtime.CompilerServices;
using Threading.Tasks;
using static Threading.LinkedTokenSourceFactory;
using GCLatencyModeScope = Runtime.GCLatencyModeScope;

internal sealed partial class LeaderState<TMember> : RaftState<TMember>
    where TMember : class, IRaftClusterMember
{
    private const int MaxTermCacheSize = 100;
    private readonly long currentTerm;
    private readonly CancellationTokenSource timerCancellation;
    internal readonly CancellationToken LeadershipToken; // cached to avoid ObjectDisposedException

    private Task? heartbeatTask;

    internal LeaderState(IRaftStateMachine<TMember> stateMachine, long term, TimeSpan maxLease)
        : base(stateMachine)
    {
        currentTerm = term;
        timerCancellation = new();
        LeadershipToken = timerCancellation.Token;
        this.maxLease = maxLease;
        lease = ExpiredLease.Instance;
        replicationEvent = new(initialState: false) { MeasurementTags = stateMachine.MeasurementTags };
        replicationQueue = new() { MeasurementTags = stateMachine.MeasurementTags };
        context = new();
        replicatorFactory = CreateReplicator;
    }

    internal ILeaderStateMetrics? Metrics
    {
        private get;
        init;
    }

    // no need to allocate state machine for every round of heartbeats
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<bool> DoHeartbeats(Timestamp startTime, TaskCompletionPipe<Task<Result<bool>>> responsePipe, IAuditTrail<IRaftLogEntry> auditTrail, IClusterConfigurationStorage configurationStorage, IEnumerator<TMember> members, CancellationToken token)
    {
        long commitIndex = auditTrail.LastCommittedEntryIndex,
            currentIndex = auditTrail.LastEntryIndex,
            term = currentTerm,
            minPrecedingIndex = 0L;

        var activeConfig = configurationStorage.ActiveConfiguration;
        var proposedConfig = configurationStorage.ProposedConfiguration;

        var leaseRenewalThreshold = 0;

        // send heartbeat in parallel
        while (members.MoveNext())
        {
            leaseRenewalThreshold++;

            if (members.Current is { IsRemote: true } member)
            {
                var precedingIndex = member.State.PrecedingIndex;
                minPrecedingIndex = Math.Min(minPrecedingIndex, precedingIndex);

                // try to get term from the cache to avoid touching audit trail for each member
                if (!precedingTermCache.TryGet(precedingIndex, out var precedingTerm))
                    precedingTermCache.Add(precedingIndex, precedingTerm = await auditTrail.GetTermAsync(precedingIndex, token).ConfigureAwait(false));

                // fork replication procedure
                var replicator = context.GetOrCreate(member, replicatorFactory);
                replicator.Initialize(activeConfig, proposedConfig, commitIndex, term, precedingIndex, precedingTerm);
                responsePipe.Add(SpawnReplicationAsync(replicator, auditTrail, currentIndex, token), replicator);
            }
        }

        responsePipe.Complete();

        // Clear cache:
        // 1. Best case: remove all entries from the cache up to the minimal observed index (those entries will never be requested)
        // 2. Worst case: cleanup the entire cache because one of the members too far behind of the leader (perhaps, it's unavailable)
        if (precedingTermCache.ApproximatedCount < MaxTermCacheSize)
            precedingTermCache.RemovePriorTo(minPrecedingIndex);
        else
            precedingTermCache.Clear();

        // update lease if the cluster contains only one local node
        if (leaseRenewalThreshold is 1)
            RenewLease(startTime.Elapsed);
        else
            leaseRenewalThreshold = (leaseRenewalThreshold >> 1) + 1;

        int quorum = 1, commitQuorum = 1; // because we know that the entry is replicated in this node
        while (await responsePipe.WaitToReadAsync(token).ConfigureAwait(false))
        {
            while (responsePipe.TryRead(out var response, out var replicator))
            {
                Debug.Assert(replicator is Replicator);

                if (!ProcessMemberResponse(startTime, response, Unsafe.As<Replicator>(replicator), ref term, ref quorum, ref commitQuorum, ref leaseRenewalThreshold))
                    return false;
            }
        }

        var broadcastTime = startTime.ElapsedMilliseconds;
        Metrics?.ReportBroadcastTime(TimeSpan.FromMilliseconds(broadcastTime));
        LeaderState.BroadcastTimeMeter.Record(broadcastTime, MeasurementTags);

        if (term <= currentTerm && quorum > 0)
        {
            Debug.Assert(quorum >= commitQuorum);

            if (commitQuorum > 0)
            {
                // majority of nodes accept entries with at least one entry from the current term
                var count = await auditTrail.CommitAsync(currentIndex, token).ConfigureAwait(false); // commit all entries starting from the first uncommitted index to the end
                Logger.CommitSuccessful(currentIndex, count);
            }
            else
            {
                Logger.CommitFailed(quorum, commitIndex);
            }

            await configurationStorage.ApplyAsync(token).ConfigureAwait(false);
            UpdateLeaderStickiness();
            return true;
        }

        // it is partitioned network with absolute majority, not possible to have more than one leader
        MoveToFollowerState(randomizeTimeout: false, term);
        return false;
    }

    [SuppressMessage("StyleCop.CSharp.SpacingRules", "SA1013", Justification = "False positive")]
    private bool ProcessMemberResponse(Timestamp startTime, Task<Result<bool>> response, Replicator replicator, ref long term, ref int quorum, ref int commitQuorum, ref int leaseRenewalThreshold)
    {
        var detector = replicator.FailureDetector;

        try
        {
            var result = response.GetAwaiter().GetResult();
            detector?.ReportHeartbeat();
            term = Math.Max(term, result.Term);
            quorum++;

            if (result.Value)
            {
                if (--leaseRenewalThreshold is 0)
                    RenewLease(startTime.Elapsed);

                commitQuorum++;
            }
            else
            {
                commitQuorum--;
            }
        }
        catch (MemberUnavailableException)
        {
            quorum -= 1;
            commitQuorum -= 1;
        }
        catch (OperationCanceledException)
        {
            // leading was canceled
            var broadcastTime = startTime.ElapsedMilliseconds;
            Metrics?.ReportBroadcastTime(TimeSpan.FromMilliseconds(broadcastTime));
            LeaderState.BroadcastTimeMeter.Record(broadcastTime, MeasurementTags);

            return false;
        }
        catch (Exception e)
        {
            // treat any exception as faulty member
            quorum -= 1;
            commitQuorum -= 1;
            Logger.LogError(e, ExceptionMessages.UnexpectedError);
        }
        finally
        {
            response.Dispose();
            replicator.Reset();
        }

        // report unavailable cluster member
        switch (detector)
        {
            case { IsMonitoring: false }:
                Logger.UnknownHealthStatus(replicator.Member.EndPoint);
                break;
            case { IsHealthy: false }:
                UnavailableMemberDetected(replicator.Member, LeadershipToken);
                break;
        }

        return true;
    }

    [AsyncMethodBuilder(typeof(SpawningAsyncTaskMethodBuilder))]
    private async Task DoHeartbeats(TimeSpan period, IAuditTrail<IRaftLogEntry> auditTrail, IClusterConfigurationStorage configurationStorage, IReadOnlyCollection<TMember> members, CancellationToken token)
    {
        var cancellationSource = token.LinkTo(LeadershipToken);

        // cached enumerator allows to avoid memory allocation on every GetEnumerator call inside of the loop
        var enumerator = members.GetEnumerator();
        try
        {
            for (var responsePipe = new TaskCompletionPipe<Task<Result<bool>>>(); !token.IsCancellationRequested; responsePipe.Reset(), AdjustEnumerator(ref members, ref enumerator))
            {
                var startTime = new Timestamp();

                // do not resume suspended callers that came after the barrier, resume them in the next iteration
                replicationQueue.SwitchValve();

                // we want to minimize GC intrusion during replication process
                // (however, it is still allowed in case of system-wide memory pressure, e.g. due to container limits)
                using (GCLatencyModeScope.SustainedLowLatency)
                {
                    if (!await DoHeartbeats(startTime, responsePipe, auditTrail, configurationStorage, enumerator, token).ConfigureAwait(false))
                        break;
                }

                // resume all suspended callers added to the queue concurrently before SwitchValve()
                replicationQueue.Drain();

                // wait for heartbeat timeout or forced replication
                await WaitForReplicationAsync(startTime, period, token).ConfigureAwait(false);
            }
        }
        finally
        {
            cancellationSource?.Dispose();
            enumerator.Dispose();
        }
    }

    private void AdjustEnumerator(ref IReadOnlyCollection<TMember> currentList, ref IEnumerator<TMember> enumerator)
    {
        var freshList = Members;
        if (ReferenceEquals(currentList, freshList))
        {
            enumerator.Reset();
        }
        else
        {
            enumerator.Dispose();
            currentList = freshList;
            enumerator = freshList.GetEnumerator();
        }
    }

    /// <summary>
    /// Starts cluster synchronization.
    /// </summary>
    /// <param name="period">Time period of Heartbeats.</param>
    /// <param name="transactionLog">Transaction log.</param>
    /// <param name="configurationStorage">Cluster configuration storage.</param>
    /// <param name="token">The toke that can be used to cancel the operation.</param>
    internal void StartLeading(TimeSpan period, IAuditTrail<IRaftLogEntry> transactionLog, IClusterConfigurationStorage configurationStorage, CancellationToken token)
    {
        var members = Members;
        context = new(members.Count);
        var state = new IRaftClusterMember.ReplicationState
        {
            NextIndex = transactionLog.LastEntryIndex + 1L,
        };

        foreach (var member in members)
        {
            member.State = state;
        }

        heartbeatTask = DoHeartbeats(period, transactionLog, configurationStorage, members, token);
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        try
        {
            timerCancellation.Cancel(false);
            replicationEvent.CancelSuspendedCallers(LeadershipToken);
            await (heartbeatTask ?? Task.CompletedTask).ConfigureAwait(false); // may throw OperationCanceledException
        }
        catch (Exception e)
        {
            Logger.LeaderStateExitedWithError(e);
        }
        finally
        {
            Dispose(disposing: true);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            timerCancellation.Dispose();
            heartbeatTask = null;

            DestroyLease();

            // cancel replication queue
            replicationQueue.Dispose(new InvalidOperationException(ExceptionMessages.LocalNodeNotLeader));
            replicationEvent.Dispose();

            precedingTermCache.Clear();
            context.Dispose();
        }

        base.Dispose(disposing);
    }
}

internal static class LeaderState
{
    internal static readonly Histogram<double> BroadcastTimeMeter = Metrics.Instrumentation.ServerSide.CreateHistogram<double>("broadcast-time", unit: "ms", description: "Heartbeat Broadcasting Time");
    internal static readonly Counter<int> TransitionRateMeter = Metrics.Instrumentation.ServerSide.CreateCounter<int>("transitions-to-leader-count", description: "Number of Transitions of Leader State");
}