using System.Data.Common;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Xunit;

namespace Pasto.IntegrationTests;

// 僅供測試注入同步點／故障；不在產品程式加入測試開關。
internal static class TransactionTestHooks
{
    internal static async Task WaitForBlockedBackendAsync(PostgresTestDatabase database, int pid)
    {
        await using var context = database.CreateContext();
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(1.5))
        {
            if (await context.Database.SqlQuery<bool>($"SELECT cardinality(pg_blocking_pids({pid})) > 0 AS \"Value\"").SingleAsync())
                return;
            // 有界輪詢實際 DB 狀態；等待時間不作為「已發生競爭」的證據。
            await Task.Delay(10);
        }
        Assert.Fail("Second connection did not enter a PostgreSQL lock wait.");
    }

    internal sealed class InjectedFailureException : Exception;

    internal sealed class HoldAfterSaveInterceptor : SaveChangesInterceptor
    {
        public TaskCompletionSource Saved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            Saved.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken);
            return result;
        }
    }

    internal sealed class LockAttemptInterceptor : DbCommandInterceptor
    {
        public int Attempts { get; private set; }
        public TaskCompletionSource<int> Attempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FOR UPDATE", StringComparison.Ordinal))
            {
                Attempts++;
                Attempted.TrySetResult(((NpgsqlConnection)command.Connection!).ProcessID);
            }
            return ValueTask.FromResult(result);
        }
    }

    internal sealed class FailLockQueryInterceptor(Exception failure) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FOR UPDATE", StringComparison.Ordinal))
                throw failure;
            return ValueTask.FromResult(result);
        }
    }

    internal sealed class BeforeLockBarrierInterceptor(int participants) : DbCommandInterceptor
    {
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int arrivals;
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FOR UPDATE", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref arrivals) == participants) ready.TrySetResult();
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken);
            }
            return result;
        }
    }

    internal sealed class FailAfterSaveInterceptor(CancellationTokenSource? cancellation) : SaveChangesInterceptor
    {
        public int Saves { get; private set; }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            Saves++;
            if (cancellation is not null)
            {
                cancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }
            throw new InjectedFailureException();
        }
    }

    internal sealed class FailBeforeCommitInterceptor : DbTransactionInterceptor
    {
        public bool ReachedCommit { get; private set; }
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default)
        {
            ReachedCommit = true;
            throw new InjectedFailureException();
        }
    }

    internal sealed class FailAfterCommitInterceptor(CancellationTokenSource? cancellation) : DbTransactionInterceptor
    {
        public int Commits { get; private set; }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            Commits++;
            // 明確已提交後注入，不把此測試宣稱為真實 COMMIT／cancel 網路競爭。
            cancellation?.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InjectedFailureException();
        }
    }
    internal sealed class FailOrCancelAfterSaveInterceptor(CancellationTokenSource? cancellation) : SaveChangesInterceptor
    {
        public int Saves { get; private set; }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            Saves++;
            cancellation?.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InjectedFailureException();
        }
    }
    internal sealed class HttpCommitFailureInterceptor(string stage) : DbTransactionInterceptor
    {
        public int Attempts { get; private set; }
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            Attempts++;
            if (stage == "before") throw new OperationCanceledException();
            return ValueTask.FromResult(result);
        }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (stage == "afterCancel") throw new OperationCanceledException();
            if (stage == "afterDatabase") throw new NpgsqlException("Simulated response loss after join commit.");
            return Task.CompletedTask;
        }
    }
}
