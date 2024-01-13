using System.Runtime.CompilerServices;

namespace DotNext.Runtime.CompilerServices;

public sealed class SpawningAsyncTaskMethodBuilderTests : Test
{
    [Fact]
    public static async Task ForkAsyncMethodWithResult()
    {
        Equal(42, await Sum(40, 2, Thread.CurrentThread.ManagedThreadId));

        [AsyncMethodBuilder(typeof(SpawningAsyncTaskMethodBuilder<>))]
        static async Task<int> Sum(int x, int y, int callerThreadId)
        {
            NotEqual(callerThreadId, Thread.CurrentThread.ManagedThreadId);

            await Task.Yield();
            return x + y;
        }
    }

    [Fact]
    public static async Task ForkAsyncMethodWithoutResult()
    {
        await CheckThreadId(Thread.CurrentThread.ManagedThreadId);

        [AsyncMethodBuilder(typeof(SpawningAsyncTaskMethodBuilder))]
        static async Task CheckThreadId(int callerThreadId)
        {
            NotEqual(callerThreadId, Thread.CurrentThread.ManagedThreadId);

            await Task.Yield();
        }
    }

    [Fact]
    public static async Task CancellationOfSpawnedMethod()
    {
        var task = CheckThreadId(Thread.CurrentThread.ManagedThreadId, new(true));
        await Task.WhenAny(task);
        True(task.IsCanceled);

        [AsyncMethodBuilder(typeof(SpawningAsyncTaskMethodBuilder))]
        static async Task CheckThreadId(int callerThreadId, CancellationToken token)
        {
            NotEqual(callerThreadId, Thread.CurrentThread.ManagedThreadId);

            await Task.Delay(DefaultTimeout, token);
        }
    }
}