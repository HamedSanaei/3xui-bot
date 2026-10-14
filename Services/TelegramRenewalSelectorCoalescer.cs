using System.Globalization;
using Telegram.Bot.Types;

/// <summary>Recognizes queued repeat clicks on an already completed account-renewal selector.</summary>
/// <remarks>
/// Only x3:aren and x3:asren are eligible; neither admits money nor mutates XUI. Each update still receives its
/// normal durable FIFO claim and terminal receipt. This bounded process-local optimization is lost on restart,
/// which merely permits a fresh read. Different users, bots, messages, targets, navigation and financial callbacks
/// never share suppression. Only clicks accepted during the original execution are coalesced.
/// </remarks>
internal sealed class TelegramRenewalSelectorCoalescer
{
    /// <summary>Protects the bounded metadata map across independent bot/user lanes.</summary>
    private readonly object _gate = new();
    /// <summary>Last successful selector per bot/user; no UUID, email, token or customer text is retained.</summary>
    private readonly Dictionary<TelegramUpdateExecutionKey, CompletedSelector> _completed = new();
    /// <summary>Maximum retained lane entries; eviction only permits another harmless selector read.</summary>
    private const int Capacity = 1024;
    /// <summary>Short retention from completion, preventing old buttons from being suppressed indefinitely.</summary>
    private static readonly TimeSpan Retention = TimeSpan.FromSeconds(30);
    /// <summary>Clock for retention checks; production uses the system clock.</summary>
    private readonly TimeProvider _clock;

    /// <summary>Creates an isolated bounded selector tracker.</summary>
    /// <param name="clock">Optional test clock; null uses the system UTC clock.</param>
    /// <remarks>The scheduler owns one instance. No database, network, wallet, or financial authorization is accessed.</remarks>
    public TelegramRenewalSelectorCoalescer(TimeProvider clock = null) => _clock = clock ?? TimeProvider.System;

    /// <summary>Checks a claimed update and invalidates old selector metadata on a different or later action.</summary>
    /// <param name="item">Claimed durable update containing its exact bot/user lane and UTC acceptance time.</param>
    /// <returns>True only for the same selector accepted before its preceding successful execution finished.</returns>
    /// <remarks>The caller acknowledges true results without entering account/state handlers. A false result must run normally.</remarks>
    /// <example><code>if (coalescer.ShouldCoalesce(item)) await executor.AcknowledgeRenewalSelectorAsync(item, token);</code></example>
    public bool ShouldCoalesce(TelegramUpdateWorkItem item)
    {
        lock (_gate)
        {
            if (TrySelector(item, out var selector) && _completed.TryGetValue(item.Key, out var previous) &&
                selector == previous.Selector && item.Sequence > previous.Sequence &&
                item.AcceptedAtUtc >= previous.AcceptedAtUtc && item.AcceptedAtUtc <= previous.CompletedAtUtc &&
                _clock.GetUtcNow().UtcDateTime - previous.CompletedAtUtc < Retention)
                return true;

            // An intervening navigation/confirmation invalidates suppression before its business handler runs.
            _completed.Remove(item.Key);
            return false;
        }
    }

    /// <summary>Remembers a successful selector immediately before releasing its durable FIFO claim.</summary>
    /// <param name="item">Successfully executed original update, scoped by bot id and Telegram sender id.</param>
    /// <remarks>
    /// Failures and coalesced acknowledgements must not be recorded. Publish before terminal persistence wakes the
    /// next lane head; if persistence fails the running head still blocks that lane. No authorization is granted here.
    /// </remarks>
    /// <example><code>if (failure == null) coalescer.RecordSuccess(item);</code></example>
    public void RecordSuccess(TelegramUpdateWorkItem item)
    {
        if (!TrySelector(item, out var selector)) return;
        lock (_gate)
        {
            if (_completed.Count >= Capacity && !_completed.ContainsKey(item.Key))
                _completed.Remove(_completed.MinBy(pair => pair.Value.CompletedAtUtc).Key);
            _completed[item.Key] = new(selector, item.Sequence, item.AcceptedAtUtc, _clock.GetUtcNow().UtcDateTime);
        }
    }

    /// <summary>Parses only the two non-financial numeric account-card renewal selectors.</summary>
    /// <param name="item">Private claimed update; callback data is inspected in memory and never logged.</param>
    /// <param name="selector">Scoped message and numeric target identity on success; default otherwise.</param>
    /// <returns>True for an exact supported callback from the claimed lane's sender and a real chat message.</returns>
    /// <remarks>Malformed, inline, external-proof, warning-confirmation, plan and payment callbacks are excluded.</remarks>
    private static bool TrySelector(TelegramUpdateWorkItem item, out Selector selector)
    {
        selector = default;
        var callback = item.Update.CallbackQuery;
        var message = callback?.Message;
        if (callback?.From?.Id != item.Key.TelegramUserId || message?.Chat == null || message.Id <= 0)
            return false;
        var parts = callback.Data?.Split(':');
        if (parts?.Length != 4 || parts[0] != "x3" || parts[1] is not ("aren" or "asren") ||
            !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var clientId) || clientId <= 0 ||
            !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var page) || page < 0)
            return false;
        selector = new(message.Chat.Id, message.Id, clientId, page, parts[1]);
        return true;
    }

    /// <summary>Numeric routing identity and list/search origin of one account-card selector.</summary>
    /// <param name="ChatId">Telegram chat containing the account card; prevents cross-chat suppression.</param>
    /// <param name="MessageId">Positive Telegram message id within that chat.</param>
    /// <param name="ClientId">Positive numeric XUI account id; routing only, never an ownership proof.</param>
    /// <param name="Page">Nonnegative list/search page carried by the callback.</param>
    /// <param name="Origin">Exact allow-listed aren or asren action, never arbitrary callback text.</param>
    private readonly record struct Selector(long ChatId, int MessageId, int ClientId, int Page, string Origin);
    /// <summary>Successful original selector's durable sequence and acceptance/completion window.</summary>
    /// <param name="Selector">Scoped numeric account-card routing identity.</param>
    /// <param name="Sequence">Original users.db inbox sequence; only later receipts can be suppressed.</param>
    /// <param name="AcceptedAtUtc">Original durable acceptance timestamp.</param>
    /// <param name="CompletedAtUtc">UTC success timestamp before terminal persistence releases the lane.</param>
    private sealed record CompletedSelector(Selector Selector, long Sequence, DateTime AcceptedAtUtc, DateTime CompletedAtUtc);
}
