using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Adminbot.Services.Telemetry;

/// <summary>Measures synchronous and asynchronous SQLite command execution without verbose EF logging.</summary>
/// <remarks>Register the same singleton interceptor through DbContextOptionsBuilder.AddInterceptors. Reader execution ends when EF receives the reader, not after row materialization. No SQL, parameters, connection strings, or context entities are recorded.</remarks>
public sealed class LatencySqliteCommandInterceptor : DbCommandInterceptor
{
    /// <summary>Bounded command ownership and sanitized completion recorder.</summary>
    private readonly LatencySqliteObserver _observer;

    /// <summary>Creates a command observer for all SQLite contexts, including background workers.</summary>
    /// <param name="telemetry">Application singleton nonblocking telemetry writer; null disables background events.</param>
    /// <remarks>Instrumentation is isolated from database results and never suppresses or retries execution.</remarks>
    /// <example><code>options.AddInterceptors(new LatencySqliteCommandInterceptor(telemetry));</code></example>
    public LatencySqliteCommandInterceptor(LatencyTelemetryService telemetry) => _observer = new(telemetry);

    /// <summary>Classifies commands from a small keyword allowlist without retaining SQL text.</summary>
    /// <param name="command">Provider command inspected only for its leading statement keyword.</param>
    /// <returns>Read for SELECT/EXPLAIN; write conservatively for all other statements, including potentially mutating PRAGMA/WITH/RETURNING commands.</returns>
    /// <remarks>Names, SQL fragments, parameter values, and connection metadata are never emitted.</remarks>
    private static TelegramUpdateStage Stage(DbCommand command)
    {
        var text = command.CommandText.AsSpan().TrimStart();
        foreach (var keyword in ReadKeywords)
            if (text.StartsWith(keyword, StringComparison.OrdinalIgnoreCase)
                && (text.Length == keyword.Length || char.IsWhiteSpace(text[keyword.Length])))
                return TelegramUpdateStage.SqliteRead;
        return TelegramUpdateStage.SqliteWrite;
    }

    /// <summary>Compile-time read keyword allowlist, never populated from user values.</summary>
    private static readonly string[] ReadKeywords = ["SELECT", "EXPLAIN"];

    /// <summary>Starts payload-free command accounting while leaving the interception result unchanged.</summary>
    /// <param name="command">Live provider command, never retained.</param>
    /// <param name="eventData">EF ownership metadata, never serialized.</param>
    private void Start(DbCommand command, CommandEventData eventData)
    {
        try { var stage = Stage(command); _observer.Start(eventData.CommandId, command.Connection, stage, TelegramUpdateLatencyScope.StageName(stage)); }
        catch { /* Provider execution is never changed by diagnostics. */ }
    }

    /// <summary>Completes payload-free command accounting with the original outcome.</summary>
    /// <param name="command">Provider command used only for connection type and keyword classification.</param>
    /// <param name="eventData">EF completion metadata containing duration and ownership id.</param>
    /// <param name="outcome">Compile-time terminal category.</param>
    /// <param name="exception">Optional original failure, inspected only for safe classification.</param>
    private void Complete(DbCommand command, CommandEndEventData eventData, string outcome, Exception exception = null)
    {
        try { var stage = Stage(command); _observer.Complete(eventData.CommandId, command.Connection, eventData.Duration, "sqlite_operation_completed", TelegramUpdateLatencyScope.StageName(stage), stage, outcome, exception); }
        catch { /* Provider execution is never changed by diagnostics. */ }
    }

    /// <summary>Measures the synchronous reader boundary without suppressing execution.</summary>
    /// <param name="command">Provider command; SQL and parameters are never recorded.</param>
    /// <param name="eventData">EF-generated operation metadata, not customer data.</param>
    /// <param name="result">Existing interception result returned unchanged.</param>
    /// <returns>The original interception result without changing provider behavior.</returns>
    /// <remarks>Exceptions from diagnostic accounting are isolated; provider exceptions remain unchanged.</remarks>
    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Start(command, eventData);
        return result;
    }

    /// <summary>Records the synchronous reader completion and preserves the result.</summary>
    /// <param name="command">Completed provider command, never serialized or retained.</param>
    /// <param name="eventData">Provider completion metadata and elapsed execution duration.</param>
    /// <param name="result">Original provider result returned unchanged.</param>
    /// <returns>The same provider result; reader lifetime and disposal ownership are unchanged.</returns>
    /// <remarks>No financial, transaction, cancellation, or retry boundary changes.</remarks>
    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        Complete(command, eventData, "completed");
        return result;
    }

    /// <summary>Measures the asynchronous reader boundary without suppressing execution.</summary>
    /// <param name="command">Provider command; SQL and parameters are never recorded.</param>
    /// <param name="eventData">EF-generated operation metadata, not customer data.</param>
    /// <param name="result">Existing interception result returned unchanged.</param>
    /// <param name="cancellationToken">Provider cancellation, unchanged by diagnostics.</param>
    /// <returns>The original interception result without changing provider behavior.</returns>
    /// <remarks>Exceptions from diagnostic accounting are isolated; provider exceptions remain unchanged.</remarks>
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Start(command, eventData);
        return ValueTask.FromResult(result);
    }

    /// <summary>Records the asynchronous reader completion and preserves the result.</summary>
    /// <param name="command">Completed provider command, never serialized or retained.</param>
    /// <param name="eventData">Provider completion metadata and elapsed execution duration.</param>
    /// <param name="result">Original provider result returned unchanged.</param>
    /// <param name="cancellationToken">Provider cancellation, unchanged by diagnostics.</param>
    /// <returns>The same provider result; reader lifetime and disposal ownership are unchanged.</returns>
    /// <remarks>No financial, transaction, cancellation, or retry boundary changes.</remarks>
    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    {
        Complete(command, eventData, "completed");
        return ValueTask.FromResult(result);
    }

    /// <summary>Measures the synchronous scalar boundary without suppressing execution.</summary>
    /// <param name="command">Provider command; SQL and parameters are never recorded.</param>
    /// <param name="eventData">EF-generated operation metadata, not customer data.</param>
    /// <param name="result">Existing interception result returned unchanged.</param>
    /// <returns>The original interception result without changing provider behavior.</returns>
    /// <remarks>Exceptions from diagnostic accounting are isolated; provider exceptions remain unchanged.</remarks>
    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Start(command, eventData);
        return result;
    }

    /// <summary>Records the synchronous scalar completion and preserves the result.</summary>
    /// <param name="command">Completed provider command, never serialized or retained.</param>
    /// <param name="eventData">Provider completion metadata and elapsed execution duration.</param>
    /// <param name="result">Original provider result returned unchanged.</param>
    /// <returns>The same provider result; reader lifetime and disposal ownership are unchanged.</returns>
    /// <remarks>No financial, transaction, cancellation, or retry boundary changes.</remarks>
    public override object ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object result)
    {
        Complete(command, eventData, "completed");
        return result;
    }

    /// <summary>Measures the asynchronous scalar boundary without suppressing execution.</summary>
    /// <param name="command">Provider command; SQL and parameters are never recorded.</param>
    /// <param name="eventData">EF-generated operation metadata, not customer data.</param>
    /// <param name="result">Existing interception result returned unchanged.</param>
    /// <param name="cancellationToken">Provider cancellation, unchanged by diagnostics.</param>
    /// <returns>The original interception result without changing provider behavior.</returns>
    /// <remarks>Exceptions from diagnostic accounting are isolated; provider exceptions remain unchanged.</remarks>
    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        Start(command, eventData);
        return ValueTask.FromResult(result);
    }

    /// <summary>Records the asynchronous scalar completion and preserves the result.</summary>
    /// <param name="command">Completed provider command, never serialized or retained.</param>
    /// <param name="eventData">Provider completion metadata and elapsed execution duration.</param>
    /// <param name="result">Original provider result returned unchanged.</param>
    /// <param name="cancellationToken">Provider cancellation, unchanged by diagnostics.</param>
    /// <returns>The same provider result; reader lifetime and disposal ownership are unchanged.</returns>
    /// <remarks>No financial, transaction, cancellation, or retry boundary changes.</remarks>
    public override ValueTask<object> ScalarExecutedAsync(DbCommand command, CommandExecutedEventData eventData, object result, CancellationToken cancellationToken = default)
    {
        Complete(command, eventData, "completed");
        return ValueTask.FromResult(result);
    }

    /// <summary>Measures the synchronous nonquery boundary without suppressing execution.</summary>
    /// <param name="command">Provider command; SQL and parameters are never recorded.</param>
    /// <param name="eventData">EF-generated operation metadata, not customer data.</param>
    /// <param name="result">Existing interception result returned unchanged.</param>
    /// <returns>The original interception result without changing provider behavior.</returns>
    /// <remarks>Exceptions from diagnostic accounting are isolated; provider exceptions remain unchanged.</remarks>
    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Start(command, eventData);
        return result;
    }

    /// <summary>Records the synchronous nonquery completion and preserves the result.</summary>
    /// <param name="command">Completed provider command, never serialized or retained.</param>
    /// <param name="eventData">Provider completion metadata and elapsed execution duration.</param>
    /// <param name="result">Original provider result returned unchanged.</param>
    /// <returns>The same provider result; reader lifetime and disposal ownership are unchanged.</returns>
    /// <remarks>No financial, transaction, cancellation, or retry boundary changes.</remarks>
    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        Complete(command, eventData, "completed");
        return result;
    }

    /// <summary>Measures the asynchronous nonquery boundary without suppressing execution.</summary>
    /// <param name="command">Provider command; SQL and parameters are never recorded.</param>
    /// <param name="eventData">EF-generated operation metadata, not customer data.</param>
    /// <param name="result">Existing interception result returned unchanged.</param>
    /// <param name="cancellationToken">Provider cancellation, unchanged by diagnostics.</param>
    /// <returns>The original interception result without changing provider behavior.</returns>
    /// <remarks>Exceptions from diagnostic accounting are isolated; provider exceptions remain unchanged.</remarks>
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Start(command, eventData);
        return ValueTask.FromResult(result);
    }

    /// <summary>Records the asynchronous nonquery completion and preserves the result.</summary>
    /// <param name="command">Completed provider command, never serialized or retained.</param>
    /// <param name="eventData">Provider completion metadata and elapsed execution duration.</param>
    /// <param name="result">Original provider result returned unchanged.</param>
    /// <param name="cancellationToken">Provider cancellation, unchanged by diagnostics.</param>
    /// <returns>The same provider result; reader lifetime and disposal ownership are unchanged.</returns>
    /// <remarks>No financial, transaction, cancellation, or retry boundary changes.</remarks>
    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Complete(command, eventData, "completed");
        return ValueTask.FromResult(result);
    }

    /// <summary>Records a cancelled SQLite attempt without suppressing its original outcome.</summary>
    /// <param name="command">Provider command, never retained.</param>
    /// <param name="eventData">EF completion metadata; exception text and SQL are never recorded.</param>
    /// <remarks>Terminal diagnostic failures are swallowed and never replace provider failures.</remarks>
    public override void CommandCanceled(DbCommand command, CommandEndEventData eventData)
    {
        Complete(command, eventData, "cancelled");
    }

    /// <summary>Records a cancelled SQLite attempt without suppressing its original outcome.</summary>
    /// <param name="command">Provider command, never retained.</param>
    /// <param name="eventData">EF completion metadata; exception text and SQL are never recorded.</param>
    /// <param name="cancellationToken">Original provider cancellation token.</param>
    /// <returns>A completed notification task; provider failure remains unchanged.</returns>
    /// <remarks>Terminal diagnostic failures are swallowed and never replace provider failures.</remarks>
    public override Task CommandCanceledAsync(DbCommand command, CommandEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Complete(command, eventData, "cancelled");
        return Task.CompletedTask;
    }

    /// <summary>Records a failed SQLite attempt without suppressing its original outcome.</summary>
    /// <param name="command">Provider command, never retained.</param>
    /// <param name="eventData">EF completion metadata; exception text and SQL are never recorded.</param>
    /// <remarks>Terminal diagnostic failures are swallowed and never replace provider failures.</remarks>
    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData)
    {
        Complete(command, eventData, "failed", eventData.Exception);
    }

    /// <summary>Records a failed SQLite attempt without suppressing its original outcome.</summary>
    /// <param name="command">Provider command, never retained.</param>
    /// <param name="eventData">EF completion metadata; exception text and SQL are never recorded.</param>
    /// <param name="cancellationToken">Original provider cancellation token.</param>
    /// <returns>A completed notification task; provider failure remains unchanged.</returns>
    /// <remarks>Terminal diagnostic failures are swallowed and never replace provider failures.</remarks>
    public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Complete(command, eventData, "failed", eventData.Exception);
        return Task.CompletedTask;
    }
}
