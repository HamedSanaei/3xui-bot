#nullable enable
using System.Collections.Concurrent;
using System.Globalization;
using System.Threading.Channels;
using Adminbot.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

/// <summary>Owns bounded, process-local public-channel compositions and one-shot background publication.</summary>
/// <remarks>All update-lane APIs perform only memory operations. The single worker owns network work and files;
/// per-draft locks protect admission, never database, filesystem or Telegram I/O. Restart never replays jobs.</remarks>
public sealed partial class PublicChannelPostManager : BackgroundService
{
    /// <summary>Maximum permitted bytes for each Telegram photo, including streaming downloads.</summary>
    private const long MaximumPhotoBytes = 10 * 1024 * 1024;
    /// <summary>Inactivity/armed-preview/completed-result retention interval.</summary>
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    /// <summary>Independent short-lived detached database context factory.</summary>
    private readonly UserDbContextFactory _users;
    /// <summary>Current configuration-owned bots and exact runtime transport availability.</summary>
    private readonly BotRegistry _bots;
    /// <summary>Exact identity-bound raw Telegram clients, never ambient defaults.</summary>
    private readonly BotClientProvider _clients;
    /// <summary>Current configured super-admin allow-list.</summary>
    private readonly IConfiguration _configuration;
    /// <summary>Per-request text/media wall-clock limits.</summary>
    private readonly TelegramForegroundDeliveryPolicy _delivery;
    /// <summary>Logger used only with closed sanitized reason codes, never exceptions or payloads.</summary>
    private readonly ILogger<PublicChannelPostManager> _logger;
    /// <summary>Bounded single-reader command queue; update handlers use nonwaiting TryWrite.</summary>
    private readonly Channel<Operation> _queue;
    /// <summary>Current composition per exact bot/actor/private-chat key.</summary>
    private readonly ConcurrentDictionary<PublicChannelPostKey, Draft> _current = new();
    /// <summary>Retained draft identities, including jobs replaced by a newer composition.</summary>
    private readonly ConcurrentDictionary<string, Draft> _retained = new(StringComparer.Ordinal);
    /// <summary>Serializes identity allocation and composition replacement; never protects I/O.</summary>
    private readonly object _indexGate = new();
    /// <summary>Cancellation for queued preparation when the host stops.</summary>
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    /// <summary>Stable token value captured before disposal so concurrent shutdown admissions cannot read a disposed source.</summary>
    private readonly CancellationToken _hostToken;
    /// <summary>Minimum delay in milliseconds between frozen destination attempts.</summary>
    private readonly int _delayMs;
    /// <summary>Retries allowed solely after explicit Telegram 429 with RetryAfter.</summary>
    private readonly int _maxRetryCount;
    /// <summary>Rejects admissions after shutdown begins.</summary>
    private volatile bool _stopping;
    /// <summary>Ensures repeated container/service disposal releases cancellation resources only once.</summary>
    private int _disposed;

    /// <summary>Creates an inactive manager; DI hosts this same singleton instance.</summary>
    /// <param name="users">Factory for the isolated/current users.db, never a scoped context.</param>
    /// <param name="bots">Shared exact runtime registry; Owned configuration remains authoritative.</param>
    /// <param name="clients">Shared identity-bound transport provider.</param>
    /// <param name="configuration">Application settings including admin allow-list and queue/delay/retry values.</param>
    /// <param name="delivery">Existing eight-second text and twenty-four-second media delivery policy.</param>
    /// <param name="logger">Operational logger; raw errors, tokens and user content are never recorded.</param>
    /// <remarks>No network or database work occurs until a worker operation executes.</remarks>
    /// <exception cref="ArgumentNullException">Any required constructor dependency is null.</exception>
    /// <example><code>var manager = provider.GetRequiredService&lt;PublicChannelPostManager&gt;();</code></example>
    public PublicChannelPostManager(UserDbContextFactory users, BotRegistry bots, BotClientProvider clients,
        IConfiguration configuration, TelegramForegroundDeliveryPolicy delivery, ILogger<PublicChannelPostManager> logger)
    {
        _users = users ?? throw new ArgumentNullException(nameof(users));
        _bots = bots ?? throw new ArgumentNullException(nameof(bots));
        _clients = clients ?? throw new ArgumentNullException(nameof(clients));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _delivery = delivery ?? throw new ArgumentNullException(nameof(delivery));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _hostToken = _lifetimeCancellation.Token;
        var settings = configuration.Get<AppConfig>() ?? new AppConfig();
        _delayMs = Math.Max(50, settings.BroadcastDelayMs);
        _maxRetryCount = Math.Max(0, settings.BroadcastMaxRetryCount);
        _queue = Channel.CreateBounded<Operation>(new BoundedChannelOptions(Math.Max(100, settings.BroadcastQueueCapacity))
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    }

    /// <summary>Starts/replaces an authorized private composition, retaining any already admitted older job.</summary>
    /// <param name="key">Exact incoming Owned bot id, configured sender and equal private chat id.</param>
    /// <returns>A detached editing snapshot, or null when source/actor authority is unavailable.</returns>
    /// <remarks>Only unpublished prior drafts are cancelled; no admitted publication is recalled.</remarks>
    /// <example><code>var draft = manager.StartDraft(new(sourceBotId, message.From.Id, message.Chat.Id));</code></example>
    public PublicChannelPostDraftSnapshot? StartDraft(PublicChannelPostKey key)
    {
        if (!IsAuthorized(key, out var identity) || _stopping) return null;
        Operation? cancelled = null;
        PublicChannelPostDraftSnapshot result;
        lock (_indexGate)
        {
            if (_current.TryGetValue(key, out var previous))
            {
                lock (previous.Sync)
                {
                    if (IsUnpublished(previous.Phase))
                    {
                        previous.Phase = PublicChannelPostPhase.Cancelled;
                        cancelled = previous.Pending;
                        previous.Pending = null;
                    }
                }
            }
            string id;
            do { id = Guid.NewGuid().ToString("N")[..10]; } while (_retained.ContainsKey(id));
            var draft = new Draft(key, id, identity);
            _retained[id] = draft;
            _current[key] = draft;
            result = Snapshot(draft);
        }
        CancelOperation(cancelled);
        return result;
    }

    /// <summary>Reads the current composition without ever creating one or granting preview permission.</summary>
    /// <param name="key">Exact incoming source/actor/private-chat key.</param>
    /// <returns>A detached snapshot, or null when absent, unauthorized, cancelled or expired.</returns>
    /// <remarks>Expiry disarms pending preparation; asset removal remains worker-owned.</remarks>
    /// <example><code>var current = manager.GetDraft(new(sourceBotId, message.From.Id, message.Chat.Id));</code></example>
    public PublicChannelPostDraftSnapshot? GetDraft(PublicChannelPostKey key)
    {
        if (!IsAuthorized(key, out var identity) || !_current.TryGetValue(key, out var draft) || draft.SourceIdentity != identity) return null;
        Operation? cancelled = null;
        PublicChannelPostDraftSnapshot? result;
        lock (draft.Sync)
        {
            cancelled = ExpireLocked(draft);
            result = draft.Phase is PublicChannelPostPhase.Expired or PublicChannelPostPhase.Cancelled ? null : Snapshot(draft);
        }
        CancelOperation(cancelled);
        return result;
    }

    /// <summary>Collects text or one photo update, preserving original UTF-16 formatting and album order.</summary>
    /// <param name="key">Authorized exact Owned source/actor/private-chat key.</param>
    /// <param name="message">Original sender message; its source message id deduplicates intake, not its file id.</param>
    /// <returns>Atomic acceptance or a closed rejection code; rejected content/admission is unchanged.</returns>
    /// <remarks>A separate text message replaces the common caption and resolves conflicts. Accepted content
    /// immediately disarms/cancels preview work, but never changes admitted publication.</remarks>
    /// <example><code>var result = manager.AddMessage(new(sourceBotId, message.From.Id, message.Chat.Id), message);</code></example>
    public PublicChannelPostCommandResult AddMessage(PublicChannelPostKey key, Message message)
    {
        if (!IsAuthorized(key, out var identity)) return Reject("unauthorized");
        if (!_current.TryGetValue(key, out var draft)) return Reject("missing_draft");
        Operation? cancelled = null;
        PublicChannelPostCommandResult result;
        lock (draft.Sync)
        {
            cancelled = ExpireLocked(draft);
            if (draft.SourceIdentity != identity) result = Reject("source_identity_changed", draft);
            else if (!IsUnpublished(draft.Phase)) result = Reject(IsAdmitted(draft.Phase) ? "publication_already_queued" : "expired", draft);
            else if (message.From?.Id != key.TelegramUserId || message.Chat.Id != key.ChatId || message.Chat.Type != ChatType.Private)
                result = Reject("unauthorized");
            else if (draft.Messages.Contains(message.Id)) result = Reject("duplicate_message", draft);
            else
            {
                var photo = message.Type == MessageType.Photo
                    ? message.Photo?.Where(x => !string.IsNullOrWhiteSpace(x.FileId))
                        .OrderByDescending(x => x.FileSize ?? 0).FirstOrDefault()
                    : null;
                if (photo == null && message.Type != MessageType.Text) result = Reject("unsupported_message", draft);
                else if (photo != null && draft.Photos.Count == 10) result = Reject("too_many_photos", draft);
                else if (photo != null && photo.FileSize > MaximumPhotoBytes) result = Reject("photo_too_large", draft);
                else if (photo != null && !string.IsNullOrEmpty(message.MediaGroupId) &&
                    draft.Photos.Any(x => x.GroupId != null && x.GroupId != message.MediaGroupId))
                    result = Reject("multiple_albums", draft);
                else
                {
                    if (photo == null)
                    {
                        draft.Text = message.Text ?? string.Empty;
                        draft.Entities = CloneEntities(message.Entities);
                        draft.CaptionConflict = false;
                    }
                    else
                    {
                        draft.Photos.Add(new Photo(message.Id, message.MediaGroupId, photo.FileId, photo.FileSize));
                        if (!string.IsNullOrEmpty(message.Caption))
                        {
                            if (draft.Text.Length == 0)
                            {
                                draft.Text = message.Caption;
                                draft.Entities = CloneEntities(message.CaptionEntities);
                            }
                            else if (!string.Equals(draft.Text, message.Caption, StringComparison.Ordinal)) draft.CaptionConflict = true;
                        }
                    }
                    draft.Messages.Add(message.Id);
                    cancelled = draft.Pending ?? cancelled;
                    draft.Pending = null;
                    draft.Prepared = null;
                    draft.Revision++;
                    draft.ContentRevision++;
                    draft.Phase = PublicChannelPostPhase.Editing;
                    draft.Expires = DateTimeOffset.UtcNow + Lifetime;
                    result = Accept(draft);
                }
            }
        }
        CancelOperation(cancelled);
        return result;
    }

    /// <summary>Binds a successfully rendered composer control to its exact current callback revision.</summary>
    /// <param name="key">Original source/actor/private-chat key.</param>
    /// <param name="draftId">Current ten-character draft id.</param>
    /// <param name="revision">Revision of the rendered composer keyboard.</param>
    /// <param name="messageId">Positive Telegram private control message id returned by Telegram.</param>
    /// <remarks>Stale binds are ignored. Replacing the control cancels any preview and clears confirmation.</remarks>
    /// <example><code>manager.BindControlMessage(key, draft.Id, draft.Revision, control.Id);</code></example>
    public void BindControlMessage(PublicChannelPostKey key, string draftId, long revision, int messageId)
    {
        if (messageId <= 0 || !IsAuthorized(key, out var identity) || !_current.TryGetValue(key, out var draft) ||
            draft.SourceIdentity != identity) return;
        Operation? cancelled = null;
        lock (draft.Sync)
        {
            if (draft.Id != draftId || draft.Revision != revision || !IsUnpublished(draft.Phase) || draft.Expires <= DateTimeOffset.UtcNow) return;
            if (draft.ControlMessageId != messageId && draft.Phase != PublicChannelPostPhase.Editing)
            {
                cancelled = draft.Pending;
                draft.Pending = null;
                draft.Phase = PublicChannelPostPhase.Editing;
                draft.Prepared = null;
            }
            draft.ControlMessageId = messageId;
        }
        CancelOperation(cancelled);
    }

    /// <summary>Admits background preparation and a real preview without waiting for network or queue space.</summary>
    /// <param name="key">Exact authorized source/actor/private-chat key.</param>
    /// <param name="draftId">Current draft identity from the callback.</param>
    /// <param name="revision">Exact revision from the callback, before this operation increments it.</param>
    /// <param name="controlMessageId">Exact bound positive private control-message id.</param>
    /// <param name="destinationIndex">Zero-based selected destination; zero starts/refetches an editing inventory.</param>
    /// <returns>Accepted preparing snapshot, or rejection leaving content and confirmation unchanged.</returns>
    /// <remarks>Preview navigation retains prepared inventory and downloaded assets; editing re-prepares inventory.</remarks>
    /// <example><code>var queued = manager.QueuePreview(key, draft.Id, draft.Revision, draft.ControlMessageId, 0);</code></example>
    public PublicChannelPostCommandResult QueuePreview(PublicChannelPostKey key, string draftId, long revision, int controlMessageId, int destinationIndex)
    {
        if (!IsAuthorized(key, out var identity)) return Reject("unauthorized");
        if (!_current.TryGetValue(key, out var draft)) return Reject("expired");
        Operation? cancelled = null;
        PublicChannelPostCommandResult result;
        lock (draft.Sync)
        {
            cancelled = ExpireLocked(draft);
            if (!Matches(draft, draftId, revision, controlMessageId) || !IsUnpublished(draft.Phase)) result = Reject("stale_preview", draft);
            else if (draft.SourceIdentity != identity) result = Reject("source_identity_changed", draft);
            else if (draft.CaptionConflict) result = Reject("caption_conflict", draft);
            else if (draft.Text.Length == 0 && draft.Photos.Count == 0) result = Reject("empty_content", draft);
            else if (destinationIndex < 0 || (draft.Prepared != null && destinationIndex >= draft.Prepared.Targets.Length) ||
                (draft.Prepared == null && destinationIndex != 0)) result = Reject("stale_preview", draft);
            else
            {
                var operation = new Operation(draft, false, draft.Revision + 1, controlMessageId,
                    CaptureContent(draft), draft.Prepared, destinationIndex, _hostToken);
                // Reader also takes draft.Sync before execution: queue visibility cannot race this commit.
                if (_stopping || !_queue.Writer.TryWrite(operation))
                {
                    operation.Cancellation.Dispose();
                    result = Reject("queue_full", draft);
                }
                else
                {
                    cancelled = draft.Pending ?? cancelled;
                    draft.Pending = operation;
                    draft.Operations++;
                    draft.Revision++;
                    draft.Phase = PublicChannelPostPhase.PreparingPreview;
                    draft.Expires = DateTimeOffset.UtcNow + Lifetime;
                    result = Accept(draft);
                }
            }
        }
        CancelOperation(cancelled);
        return result;
    }

    /// <summary>Atomically consumes exactly one visible preview revision and queues immutable fan-out.</summary>
    /// <param name="key">Exact authorized source/actor/private-chat key.</param>
    /// <param name="draftId">Identity of the current unpublished draft.</param>
    /// <param name="revision">Revision whose actual private preview and keyboard both succeeded.</param>
    /// <param name="controlMessageId">Exact armed control message; also retained for job progress.</param>
    /// <returns>One accepted queued snapshot, or a rejection; full queue preserves confirmation.</returns>
    /// <remarks>Confirmation is consumed only after TryWrite succeeds. Repeated confirmations can never queue twice,
    /// even if all subsequent status edits fail. Navigation cannot cancel admitted publication.</remarks>
    /// <example><code>var admitted = manager.QueuePublish(key, draft.Id, draft.Revision, draft.ControlMessageId);</code></example>
    public PublicChannelPostCommandResult QueuePublish(PublicChannelPostKey key, string draftId, long revision, int controlMessageId)
    {
        if (!IsAuthorized(key, out var identity)) return Reject("unauthorized");
        if (!_current.TryGetValue(key, out var draft)) return Reject("expired");
        Operation? cancelled = null;
        PublicChannelPostCommandResult result;
        lock (draft.Sync)
        {
            cancelled = ExpireLocked(draft);
            if (!Matches(draft, draftId, revision, controlMessageId) || draft.Phase != PublicChannelPostPhase.PreviewReady ||
                draft.Prepared == null || draft.Prepared.Targets.Length == 0) result = Reject("stale_preview", draft);
            else if (draft.SourceIdentity != identity) result = Reject("source_identity_changed", draft);
            else
            {
                var operation = new Operation(draft, true, revision, controlMessageId,
                    CaptureContent(draft), draft.Prepared, 0, _hostToken);
                if (_stopping || !_queue.Writer.TryWrite(operation))
                {
                    operation.Cancellation.Dispose();
                    result = Reject("queue_full", draft);
                }
                else
                {
                    draft.Operations++;
                    draft.Phase = PublicChannelPostPhase.PublishQueued;
                    draft.Pending = null;
                    draft.Progress = new Progress(draft.Prepared);
                    result = Accept(draft);
                }
            }
        }
        CancelOperation(cancelled);
        return result;
    }

    /// <summary>Returns the exact unpublished composition to editing, preserving its text and photo assets.</summary>
    /// <param name="key">Exact authorized source/actor/private-chat key.</param>
    /// <param name="draftId">Current draft identity.</param>
    /// <param name="revision">Current callback revision.</param>
    /// <param name="controlMessageId">Exact current private control-message id.</param>
    /// <returns>Incremented editing snapshot, or a rejection with no admission/content change.</returns>
    /// <remarks>Outstanding preparation is cancelled, confirmation and inventory cleared; photo downloads remain reusable.</remarks>
    /// <example><code>var editing = manager.ResumeEditing(key, draft.Id, draft.Revision, draft.ControlMessageId);</code></example>
    public PublicChannelPostCommandResult ResumeEditing(PublicChannelPostKey key, string draftId, long revision, int controlMessageId)
    {
        if (!IsAuthorized(key, out var identity)) return Reject("unauthorized");
        if (!_current.TryGetValue(key, out var draft)) return Reject("expired");
        Operation? cancelled;
        PublicChannelPostCommandResult result;
        lock (draft.Sync)
        {
            cancelled = ExpireLocked(draft);
            if (!Matches(draft, draftId, revision, controlMessageId) || !IsUnpublished(draft.Phase)) result = Reject("stale_preview", draft);
            else if (draft.SourceIdentity != identity) result = Reject("source_identity_changed", draft);
            else
            {
                cancelled = draft.Pending ?? cancelled;
                draft.Pending = null;
                draft.Prepared = null;
                draft.Revision++;
                draft.Phase = PublicChannelPostPhase.Editing;
                draft.Expires = DateTimeOffset.UtcNow + Lifetime;
                result = Accept(draft);
            }
        }
        CancelOperation(cancelled);
        return result;
    }

    /// <summary>Safely abandons only this source/user's unpublished drafts during any user's navigation.</summary>
    /// <param name="sourceBotId">Exact incoming internal source bot id; never an ambient/default fallback.</param>
    /// <param name="telegramUserId">Incoming positive Telegram sender id, not a callback payload grant.</param>
    /// <remarks>No authority is required or granted. Absent drafts and admitted publications are untouched.</remarks>
    /// <example><code>manager.CancelDraft(currentSourceBotId, message.From.Id);</code></example>
    public void CancelDraft(string sourceBotId, long telegramUserId)
    {
        foreach (var pair in _current)
        {
            if (pair.Key.SourceBotId != sourceBotId || pair.Key.TelegramUserId != telegramUserId) continue;
            Operation? cancelled = null;
            lock (pair.Value.Sync)
            {
                if (IsUnpublished(pair.Value.Phase))
                {
                    pair.Value.Phase = PublicChannelPostPhase.Cancelled;
                    cancelled = pair.Value.Pending;
                    pair.Value.Pending = null;
                }
            }
            CancelOperation(cancelled);
        }
    }

    /// <summary>Validates a retained job's exact refresh binding without requiring it to be the current composition.</summary>
    /// <param name="key">Incoming exact source/actor/private-chat key.</param>
    /// <param name="draftId">Retained admitted job identity.</param>
    /// <param name="revision">Frozen admitted callback revision.</param>
    /// <param name="controlMessageId">Original retained positive progress-control message id.</param>
    /// <returns>True only for authorized, unexpired, exactly bound admitted/completed publication.</returns>
    /// <remarks>This is validation only; it never starts a draft, changes content or sends.</remarks>
    internal bool IsStatusBindingCurrent(PublicChannelPostKey key, string draftId, long revision, int controlMessageId)
    {
        if (!IsAuthorized(key, out var identity) || !_retained.TryGetValue(draftId, out var draft)) return false;
        lock (draft.Sync) return draft.Key == key && draft.SourceIdentity == identity && IsAdmitted(draft.Phase) &&
            draft.Revision == revision && draft.ControlMessageId == controlMessageId && controlMessageId > 0 &&
            (draft.Phase != PublicChannelPostPhase.Completed || draft.Expires > DateTimeOffset.UtcNow);
    }

    /// <summary>Checks source authorization without fallback resolution or network I/O.</summary>
    /// <param name="key">Required exact private composition key.</param>
    /// <param name="identity">Positive BotFather identity from the current source token on success.</param>
    /// <returns>True only for an active exact Owned source and configured super-admin in their private chat.</returns>
    /// <remarks>Configured admins, not colleague/database roles, are authoritative.</remarks>
    private bool IsAuthorized(PublicChannelPostKey key, out long identity)
    {
        identity = 0;
        if (key == null || key.TelegramUserId <= 0 || key.ChatId != key.TelegramUserId ||
            _configuration.GetSection(nameof(AppConfig.AdminsUserIds)).Get<List<long>>()?.Contains(key.TelegramUserId) != true) return false;
        var bot = _bots.Bots.FirstOrDefault(x => string.Equals(x.Id, key.SourceBotId, StringComparison.Ordinal));
        var numeric = TelegramBotTokenIdentity.ExtractBotId(bot?.Token);
        if (bot == null || !bot.Enabled || bot.Type != BotInstanceTypes.Owned || bot.IsSalesAssistant || numeric is not > 0) return false;
        identity = numeric.Value;
        return true;
    }

    /// <summary>Creates detached original-content entities with every supported optional field preserved.</summary>
    /// <param name="entities">Original Telegram text/caption entities, possibly null.</param>
    /// <returns>A new array with unchanged UTF-16 offsets; empty when absent.</returns>
    /// <remarks>These explicit entities are sent without interpreting raw text as HTML or Markdown.</remarks>
    private static MessageEntity[] CloneEntities(IEnumerable<MessageEntity>? entities) => entities?.Select(x => new MessageEntity
    { Type = x.Type, Offset = x.Offset, Length = x.Length, Url = x.Url, User = x.User, Language = x.Language, CustomEmojiId = x.CustomEmojiId }).ToArray() ?? [];

    /// <summary>Copies bounded metadata and orders each album's slots by source message id.</summary>
    /// <param name="draft">Locked draft whose content is frozen for one operation.</param>
    /// <returns>Immutable operation metadata; photo bytes are never copied.</returns>
    /// <remarks>Separate photos keep intake slots; occurrences within the same album fill their slots in message-id order.</remarks>
    private static Content CaptureContent(Draft draft)
    {
        var photos = draft.Photos.ToArray();
        foreach (var group in photos.Where(x => x.GroupId != null).GroupBy(x => x.GroupId))
        {
            var ordered = group.OrderBy(x => x.MessageId).ToArray();
            var cursor = 0;
            for (var i = 0; i < photos.Length; i++) if (photos[i].GroupId == group.Key) photos[i] = ordered[cursor++];
        }
        return new Content(draft.ContentRevision, draft.Text, CloneEntities(draft.Entities), photos);
    }

    /// <summary>Builds a detached handler snapshot while the draft is locked.</summary>
    /// <param name="draft">Locked owning draft.</param>
    /// <returns>Immutable content-free lifecycle view.</returns>
    private static PublicChannelPostDraftSnapshot Snapshot(Draft draft) => new(draft.Key, draft.Id, draft.Revision,
        draft.Phase, draft.Photos.Count, draft.Text.Length != 0, draft.CaptionConflict, draft.ControlMessageId, draft.Expires);

    /// <summary>Builds a successful detached result for a committed in-memory change.</summary>
    /// <param name="draft">Locked committed draft.</param>
    /// <returns>Accepted result with an immutable snapshot.</returns>
    private static PublicChannelPostCommandResult Accept(Draft draft) => new(true, string.Empty, Snapshot(draft));

    /// <summary>Builds a closed rejection without mutating draft content or queue admission.</summary>
    /// <param name="reason">Sanitized machine-readable reason code.</param>
    /// <param name="draft">Optional locked current draft.</param>
    /// <returns>Rejected result, omitting expired/cancelled drafts.</returns>
    private static PublicChannelPostCommandResult Reject(string reason, Draft? draft = null) => new(false, reason,
        draft == null || draft.Phase is PublicChannelPostPhase.Expired or PublicChannelPostPhase.Cancelled ? null : Snapshot(draft));

    /// <summary>Checks the exact callback/control binding of a current, live composition.</summary>
    /// <param name="draft">Locked owning draft.</param>
    /// <param name="id">Callback draft id.</param>
    /// <param name="revision">Callback revision.</param>
    /// <param name="control">Incoming positive private control-message id.</param>
    /// <returns>True only for an exact live current composition.</returns>
    private bool Matches(Draft draft, string id, long revision, int control) => draft.Id == id && draft.Revision == revision &&
        control > 0 && draft.ControlMessageId == control && draft.Expires > DateTimeOffset.UtcNow &&
        _current.TryGetValue(draft.Key, out var current) && ReferenceEquals(current, draft);

    /// <summary>Recognizes phases that navigation/editing may abandon.</summary>
    /// <param name="phase">Current phase.</param>
    /// <returns>True only before one-shot publication admission.</returns>
    private static bool IsUnpublished(PublicChannelPostPhase phase) => phase is PublicChannelPostPhase.Editing or
        PublicChannelPostPhase.PreparingPreview or PublicChannelPostPhase.PreviewReady;

    /// <summary>Recognizes retained jobs, which can only be refreshed and never edited/repeated.</summary>
    /// <param name="phase">Current phase.</param>
    /// <returns>True for queued, running or completed publication.</returns>
    private static bool IsAdmitted(PublicChannelPostPhase phase) => phase is PublicChannelPostPhase.PublishQueued or
        PublicChannelPostPhase.Publishing or PublicChannelPostPhase.Completed;

    /// <summary>Expires inactive unpublished content or completed results, returning cancellation outside the lock.</summary>
    /// <param name="draft">Locked draft whose UTC retention deadline is checked.</param>
    /// <returns>Superseded preparation, or null; queued/running jobs never expire mid-send.</returns>
    private static Operation? ExpireLocked(Draft draft)
    {
        if ((!IsUnpublished(draft.Phase) && draft.Phase != PublicChannelPostPhase.Completed) ||
            draft.Expires > DateTimeOffset.UtcNow) return null;
        draft.Phase = PublicChannelPostPhase.Expired;
        var pending = draft.Pending;
        draft.Pending = null;
        return pending;
    }

    /// <summary>Cancels pre-publication network work outside all state locks.</summary>
    /// <param name="operation">Superseded preparation, possibly null or already disposed by the worker.</param>
    /// <remarks>Cancellation never removes files; the worker awaits stream disposal before cleanup.</remarks>
    private static void CancelOperation(Operation? operation)
    {
        if (operation == null) return;
        try { operation.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>Worker-owned draft and retained progress; Sync protects only mutable admission/status fields.</summary>
    private sealed class Draft
    {
        /// <summary>Per-draft in-memory state lock, never held during I/O.</summary>
        internal readonly object Sync = new();
        /// <summary>Immutable exact source/actor/private-chat ownership.</summary>
        internal readonly PublicChannelPostKey Key;
        /// <summary>Unique callback identity retained independently from current composition.</summary>
        internal readonly string Id;
        /// <summary>Numeric source identity to which all original file ids belong.</summary>
        internal readonly long SourceIdentity;
        /// <summary>GUID-owned OS-temp directory; only the worker creates/deletes it.</summary>
        internal readonly string DirectoryPath = Path.Combine(Path.GetTempPath(), "AdminbotPublicChannelPost-" + Guid.NewGuid().ToString("N"));
        /// <summary>Worker-only cache of successfully completed original-source downloads.</summary>
        internal readonly Dictionary<string, string> Assets = new(StringComparer.Ordinal);
        /// <summary>Ordered editable photo occurrences, including intentionally repeated file ids.</summary>
        internal readonly List<Photo> Photos = [];
        /// <summary>Accepted source message ids for intake idempotency.</summary>
        internal readonly HashSet<int> Messages = [];
        /// <summary>Current explicit common text/caption, never normalized or parsed.</summary>
        internal string Text = string.Empty;
        /// <summary>Detached original explicit formatting entities.</summary>
        internal MessageEntity[] Entities = [];
        /// <summary>Unresolved differing nonempty photo captions.</summary>
        internal bool CaptionConflict;
        /// <summary>Callback revision, incremented by content, editing and preview selection.</summary>
        internal long Revision = 1;
        /// <summary>Content-only revision independent from preview navigation.</summary>
        internal long ContentRevision;
        /// <summary>Closed lifecycle, including retained admitted jobs.</summary>
        internal PublicChannelPostPhase Phase = PublicChannelPostPhase.Editing;
        /// <summary>Exact private control binding, initially zero.</summary>
        internal int ControlMessageId;
        /// <summary>UTC inactivity/preview/completion retention deadline.</summary>
        internal DateTimeOffset Expires = DateTimeOffset.UtcNow + Lifetime;
        /// <summary>Current preparation cancellation handle; never an admitted publication.</summary>
        internal Operation? Pending;
        /// <summary>Frozen validated target inventory available for navigation/confirmation.</summary>
        internal Prepared? Prepared;
        /// <summary>Number of queued/running operations retaining this draft's asset directory.</summary>
        internal int Operations;
        /// <summary>Admitted job accounting retained after asset release.</summary>
        internal Progress? Progress;
        /// <summary>Constructs an isolated composition without filesystem or network activity.</summary>
        /// <param name="key">Exact source/actor/private-chat ownership.</param>
        /// <param name="id">Unique ten-character callback identity.</param>
        /// <param name="identity">Positive captured source BotFather identity.</param>
        internal Draft(PublicChannelPostKey key, string id, long identity) { Key = key; Id = id; SourceIdentity = identity; }
    }

    /// <summary>Detached photo occurrence metadata, not file bytes or a transport.</summary>
    /// <param name="MessageId">Original Telegram source message id used for ordering/dedup.</param>
    /// <param name="GroupId">Optional single incoming album identity.</param>
    /// <param name="FileId">Largest source-bot-scoped photo file id, never sent through a destination bot.</param>
    /// <param name="Size">Telegram-reported bytes, possibly absent.</param>
    private sealed record Photo(int MessageId, string? GroupId, string FileId, long? Size);

    /// <summary>Immutable bounded metadata captured at queue admission.</summary>
    /// <param name="Revision">Content-only revision.</param>
    /// <param name="Text">Unmodified original text/caption.</param>
    /// <param name="Entities">Detached original entities, never mutated after capture.</param>
    /// <param name="Photos">Ordered immutable photo occurrences.</param>
    private sealed record Content(long Revision, string Text, MessageEntity[] Entities, Photo[] Photos);

    /// <summary>Queue item retaining immutable content plus a draft-owned asset lease.</summary>
    private sealed class Operation
    {
        /// <summary>Owning composition or retained publication job.</summary>
        internal readonly Draft Draft;
        /// <summary>True for one-way admitted publication; false for cancellable preview.</summary>
        internal readonly bool Publish;
        /// <summary>Callback revision owned by this command.</summary>
        internal readonly long Revision;
        /// <summary>Exact original private control binding.</summary>
        internal readonly int Control;
        /// <summary>Frozen original content metadata.</summary>
        internal readonly Content Content;
        /// <summary>Optional already prepared immutable destination inventory.</summary>
        internal readonly Prepared? Prepared;
        /// <summary>Zero-based real-preview selection.</summary>
        internal readonly int DestinationIndex;
        /// <summary>Host-linked cancellable preparation/publication request lifetime.</summary>
        internal readonly CancellationTokenSource Cancellation;
        /// <summary>Captures an immutable command before nonwaiting queue admission.</summary>
        /// <param name="draft">Owning draft retaining shared cached files.</param>
        /// <param name="publish">Whether the command is one-shot publication.</param>
        /// <param name="revision">Captured callback revision.</param>
        /// <param name="control">Positive bound private control id.</param>
        /// <param name="content">Frozen original content metadata.</param>
        /// <param name="prepared">Already prepared targets for navigation/publication, or null.</param>
        /// <param name="destinationIndex">Zero-based preview selection.</param>
        /// <param name="hostToken">Host lifetime cancellation; never an inbox update token.</param>
        internal Operation(Draft draft, bool publish, long revision, int control, Content content, Prepared? prepared,
            int destinationIndex, CancellationToken hostToken)
        {
            Draft = draft; Publish = publish; Revision = revision; Control = control; Content = content;
            Prepared = prepared; DestinationIndex = destinationIndex;
            Cancellation = CancellationTokenSource.CreateLinkedTokenSource(hostToken);
        }
    }
}
