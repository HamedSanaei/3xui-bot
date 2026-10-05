#nullable enable
using System.Globalization;
using System.Text.Json;
using Adminbot.Domain;
using Adminbot.Domain.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

/// <summary>Background preparation, exact channel inventory and bounded draft-owned media storage.</summary>
public sealed partial class PublicChannelPostManager
{
    /// <summary>Runs the sole network/file worker and a cancellation-only thirty-second expiry scanner.</summary>
    /// <param name="stoppingToken">Hosted service shutdown token, never an inbox/update token.</param>
    /// <returns>Completion after outstanding requests/streams and queued asset leases are released.</returns>
    /// <remarks>Neither scanner nor foreground APIs delete assets. Only this awaited worker removes owned directories.</remarks>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _hostToken);
        var token = linked.Token;
        var scan = ScanExpiryAsync(token);
        try
        {
            while (!token.IsCancellationRequested)
            {
                using var readBudget = CancellationTokenSource.CreateLinkedTokenSource(token);
                readBudget.CancelAfter(TimeSpan.FromSeconds(30));
                try
                {
                    if (!await _queue.Reader.WaitToReadAsync(readBudget.Token)) break;
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    CleanupRetiredAssets();
                    continue;
                }
                while (_queue.Reader.TryRead(out var operation))
                {
                    try
                    {
                        if (operation.Publish) await PublishAsync(operation, token);
                        else await PreviewAsync(operation);
                    }
                    catch (OperationCanceledException) when (operation.Cancellation.IsCancellationRequested || token.IsCancellationRequested) { }
                    catch (Exception)
                    {
                        _logger.LogWarning("Public channel post operation failed. reason={ReasonCode}", "worker_operation_failed");
                        if (!operation.Publish) await FailPreviewAsync(operation, "preparation_failed", "آماده‌سازی پیش‌نمایش ناموفق بود؛ دوباره تلاش کنید.");
                    }
                    finally
                    {
                        lock (operation.Draft.Sync)
                        {
                            operation.Draft.Operations--;
                            if (ReferenceEquals(operation.Draft.Pending, operation)) operation.Draft.Pending = null;
                        }
                        operation.Cancellation.Dispose();
                        CleanupRetiredAssets();
                    }
                    if (token.IsCancellationRequested) break;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            _stopping = true;
            _queue.Writer.TryComplete();
            linked.Cancel();
            try { await scan; } catch (OperationCanceledException) { }
            while (_queue.Reader.TryRead(out var remaining))
            {
                lock (remaining.Draft.Sync) remaining.Draft.Operations--;
                remaining.Cancellation.Dispose();
            }
            foreach (var draft in _retained.Values)
            {
                lock (draft.Sync)
                {
                    if (IsUnpublished(draft.Phase)) draft.Phase = PublicChannelPostPhase.Cancelled;
                    if (draft.Phase == PublicChannelPostPhase.PublishQueued) draft.Phase = PublicChannelPostPhase.Completed;
                }
            }
            CleanupRetiredAssets(force: true);
        }
    }

    /// <summary>Prevents new admissions and requests shutdown without deleting still-open worker assets.</summary>
    /// <param name="cancellationToken">Host's maximum graceful-shutdown wait.</param>
    /// <returns>The background service's awaited stop task.</returns>
    /// <remarks>Worker finally, not this method, releases streams and deletes directories. Restart has no replay source.</remarks>
    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping = true;
        _queue.Writer.TryComplete();
        try { _lifetimeCancellation.Cancel(); } catch (ObjectDisposedException) { }
        return base.StopAsync(cancellationToken);
    }

    /// <summary>Releases cancellation resources; filesystem cleanup remains in the awaited worker.</summary>
    /// <remarks>An inactive test manager has never created assets; a running worker owns all stream disposal.</remarks>
    public override void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stopping = true;
        _queue.Writer.TryComplete();
        _lifetimeCancellation.Cancel();
        base.Dispose();
        _lifetimeCancellation.Dispose();
    }

    /// <summary>Marks inactive drafts/results expired every thirty seconds without touching files.</summary>
    /// <param name="token">Host-linked scanner lifetime.</param>
    /// <returns>Completion on host cancellation.</returns>
    /// <remarks>Cancellation happens outside state locks, including during a slow worker preparation.</remarks>
    private async Task ScanExpiryAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(token))
        {
            foreach (var draft in _retained.Values)
            {
                Operation? cancelled;
                lock (draft.Sync)
                {
                    cancelled = ExpireLocked(draft);
                }
                CancelOperation(cancelled);
            }
        }
    }

    /// <summary>Releases only validated directories whose draft has no queued/running asset lease.</summary>
    /// <param name="force">True only in the worker's shutdown finally after all requests have settled.</param>
    /// <remarks>Queued stale operations retain their lease until dequeued, so supersession never removes open/new/job files.
    /// Successful completed jobs retain progress, not bytes. Failed deletion is retried on the next worker sweep.</remarks>
    private void CleanupRetiredAssets(bool force = false)
    {
        foreach (var pair in _retained)
        {
            var draft = pair.Value;
            bool release;
            bool forget;
            lock (draft.Sync)
            {
                release = draft.Operations == 0 && (force || draft.Phase is PublicChannelPostPhase.Completed or
                    PublicChannelPostPhase.Cancelled or PublicChannelPostPhase.Expired);
                forget = release && (force || draft.Phase is PublicChannelPostPhase.Cancelled or PublicChannelPostPhase.Expired);
            }
            if (!release) continue;
            if (!DeleteOwnedDirectory(draft.DirectoryPath)) continue;
            draft.Assets.Clear();
            if (forget)
            {
                _retained.TryRemove(pair.Key, out _);
                ((ICollection<KeyValuePair<PublicChannelPostKey, Draft>>)_current).Remove(new(draft.Key, draft));
            }
        }
    }

    /// <summary>Deletes only this manager's immediate GUID-owned OS-temp directory, refusing links or foreign paths.</summary>
    /// <param name="path">Generated absolute draft-owned directory, never configuration or user input.</param>
    /// <returns>True when absent/deleted; false for unsafe paths or transient filesystem errors.</returns>
    /// <remarks>No repository/Data directories or directories created by another draft are removed.</remarks>
    private static bool DeleteOwnedDirectory(string path)
    {
        const string prefix = "AdminbotPublicChannelPost-";
        try
        {
            var directory = new DirectoryInfo(path);
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!string.Equals(directory.Parent?.FullName.TrimEnd(Path.DirectorySeparatorChar), temp, StringComparison.OrdinalIgnoreCase) ||
                !directory.Name.StartsWith(prefix, StringComparison.Ordinal) ||
                !Guid.TryParseExact(directory.Name[prefix.Length..], "N", out _)) return false;
            if (!directory.Exists) return true;
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0 ||
                directory.EnumerateFileSystemInfos().Any(x => (x.Attributes & FileAttributes.ReparsePoint) != 0 || x is DirectoryInfo)) return false;
            directory.Delete(recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    /// <summary>Checks whether one preparation still owns the current revision, control and unexpired authority.</summary>
    /// <param name="operation">Queued cancellable preview operation.</param>
    /// <returns>True only while it can safely render/arm its exact private control.</returns>
    private bool IsPreviewCurrent(Operation operation)
    {
        var draft = operation.Draft;
        if (operation.Cancellation.IsCancellationRequested || !IsAuthorized(draft.Key, out var identity) || identity != draft.SourceIdentity) return false;
        lock (draft.Sync) return draft.Phase == PublicChannelPostPhase.PreparingPreview && ReferenceEquals(draft.Pending, operation) &&
            Matches(draft, draft.Id, operation.Revision, operation.Control) && draft.ContentRevision == operation.Content.Revision;
    }

    /// <summary>Prepares all targets/assets, sends actual private content, then successfully edits controls before arming.</summary>
    /// <param name="operation">Immutable source content and exact retained control/revision.</param>
    /// <returns>Completion after preview and control requests settle; no channel sends occur.</returns>
    /// <remarks>Every await is followed by current-revision checks; stale work never arms confirmation.</remarks>
    private async Task PreviewAsync(Operation operation)
    {
        if (!IsPreviewCurrent(operation)) return;
        try
        {
            var token = operation.Cancellation.Token;
            var prepared = operation.Prepared ?? await PrepareDestinationsAsync(operation, token);
            if (!IsPreviewCurrent(operation)) return;
            if (prepared.Targets.Length == 0)
            {
                await FailPreviewAsync(operation, "no_eligible_targets", "هیچ کانال عمومی واجد شرایطی پیدا نشد.", prepared);
                return;
            }
            var client = _clients.GetClient(operation.Draft.Key.SourceBotId, operation.Draft.SourceIdentity);
            await EnsureAssetsAsync(operation, client, token);
            if (!IsPreviewCurrent(operation)) return;
            var selected = prepared.Targets[operation.DestinationIndex];
            await SendContentAsync(client, operation.Draft.Key.ChatId, operation.Content, selected, operation.Draft, preview: true, token);
            if (!IsPreviewCurrent(operation)) return;
            if (prepared.Skipped.Length != 0)
            {
                await SendDetailMessagesAsync(client, operation.Draft.Key.ChatId, prepared.Skipped, token);
                if (!IsPreviewCurrent(operation)) return;
            }
            await WithBudgetAsync(ct => client.EditMessageText(operation.Draft.Key.ChatId, operation.Control,
                PreviewControlText(prepared, operation.DestinationIndex), replyMarkup: PreviewKeyboard(operation, prepared),
                linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true }, cancellationToken: ct), _delivery.OverallBudget, token);
            if (!IsPreviewCurrent(operation)) return;
            lock (operation.Draft.Sync)
            {
                if (!ReferenceEquals(operation.Draft.Pending, operation) || operation.Cancellation.IsCancellationRequested ||
                    !Matches(operation.Draft, operation.Draft.Id, operation.Revision, operation.Control) ||
                    operation.Draft.Phase != PublicChannelPostPhase.PreparingPreview) return;
                operation.Draft.Prepared = prepared;
                operation.Draft.Phase = PublicChannelPostPhase.PreviewReady;
                operation.Draft.Expires = DateTimeOffset.UtcNow + Lifetime;
            }
        }
        catch (OperationCanceledException) when (operation.Cancellation.IsCancellationRequested) { }
        catch (PreparationException ex) { await FailPreviewAsync(operation, ex.Reason, ex.SafeMessage); }
        catch (Exception) { await FailPreviewAsync(operation, "preview_failed", "پیش‌نمایش ناموفق بود؛ محتوا حفظ شده است. دوباره پیش‌نمایش بگیرید."); }
    }

    /// <summary>Returns only the still-current failed preparation to editing, preserving all original content/assets.</summary>
    /// <param name="operation">Failed background preview operation.</param>
    /// <param name="reason">Closed sanitized failure code.</param>
    /// <param name="text">Application-generated safe Persian notice, never exception text.</param>
    /// <param name="prepared">Optional exclusion inventory for a zero-target report.</param>
    /// <returns>Completion after a best-effort bounded control update.</returns>
    /// <remarks>Failure cannot grant confirmation; a stale failure cannot overwrite a later control.</remarks>
    private async Task FailPreviewAsync(Operation operation, string reason, string text, Prepared? prepared = null)
    {
        if (!IsPreviewCurrent(operation)) return;
        lock (operation.Draft.Sync)
        {
            if (!ReferenceEquals(operation.Draft.Pending, operation)) return;
            operation.Draft.Phase = PublicChannelPostPhase.Editing;
            operation.Draft.Prepared = null;
            operation.Draft.Expires = DateTimeOffset.UtcNow + Lifetime;
        }
        _logger.LogWarning("Public channel post preview was not armed. reason={ReasonCode}", reason);
        try
        {
            if (!IsEditingOperationBindingCurrent(operation)) return;
            var client = _clients.GetClient(operation.Draft.Key.SourceBotId, operation.Draft.SourceIdentity);
            await WithBudgetAsync(ct => client.EditMessageText(operation.Draft.Key.ChatId, operation.Control, text,
                replyMarkup: EditingKeyboard(operation), cancellationToken: ct), _delivery.OverallBudget, operation.Cancellation.Token);
            if (prepared != null && IsEditingOperationBindingCurrent(operation))
                await SendDetailMessagesAsync(client, operation.Draft.Key.ChatId, prepared.Skipped, operation.Cancellation.Token);
        }
        catch (Exception) { _logger.LogWarning("Public channel post status update failed. reason={ReasonCode}", "status_update_failed"); }
    }

    /// <summary>Ensures a failure notice still belongs to the same editing revision and pending operation.</summary>
    /// <param name="operation">Current failed preparation.</param>
    /// <returns>True only before newer content/control/navigation supersedes this operation.</returns>
    private bool IsEditingOperationBindingCurrent(Operation operation)
    {
        if (operation.Cancellation.IsCancellationRequested || !IsAuthorized(operation.Draft.Key, out var identity) || identity != operation.Draft.SourceIdentity) return false;
        lock (operation.Draft.Sync) return operation.Draft.Phase == PublicChannelPostPhase.Editing &&
            ReferenceEquals(operation.Draft.Pending, operation) && Matches(operation.Draft, operation.Draft.Id, operation.Revision, operation.Control);
    }

    /// <summary>Reads current Owned registry configurations and detached Tenant rows, retaining no token in commands.</summary>
    /// <param name="token">Host/operation cancellation for the short database read.</param>
    /// <returns>Token-free inventory; empty channel lists are allowed and produce no candidates.</returns>
    /// <remarks>Tenant participation is independent from mandatory-join enforcement. Owned database rows are never used.</remarks>
    private async Task<InventoryBot[]> ReadInventoryAsync(CancellationToken token)
    {
        var inventory = _bots.Bots.Where(x => x.Type == BotInstanceTypes.Owned && !x.IsSalesAssistant)
            .Select(x => new InventoryBot(x.Id, false, x.Enabled, !string.IsNullOrWhiteSpace(x.Token), true,
                TelegramBotTokenIdentity.ExtractBotId(x.Token), x.ChannelIds?.ToArray() ?? [], false)).ToList();
        await using (var db = _users.CreateDbContext())
        {
            var tenants = await db.BotInstances.AsNoTracking().Where(x => x.Type == BotInstanceTypes.Tenant)
                .Select(x => new { x.Id, x.Enabled, x.Token, x.TenantPublicChannelPostsEnabled, x.TenantChannelIdsJson }).ToArrayAsync(token);
            foreach (var row in tenants)
            {
                var channels = ParseChannels(row.TenantChannelIdsJson, out var invalid);
                inventory.Add(new InventoryBot(row.Id, true, row.Enabled, !string.IsNullOrWhiteSpace(row.Token),
                    row.TenantPublicChannelPostsEnabled, TelegramBotTokenIdentity.ExtractBotId(row.Token), channels, invalid));
            }
        }
        return inventory.OrderBy(x => x.Id, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Parses only a JSON array of channel strings without changing stored settings.</summary>
    /// <param name="json">Persisted Tenant channel JSON, possibly null/empty.</param>
    /// <param name="invalid">True for invalid JSON, nonarray values or nonstring items.</param>
    /// <returns>Configured string entries; empty on missing or invalid settings.</returns>
    private static string[] ParseChannels(string? json, out bool invalid)
    {
        invalid = false;
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array ||
                document.RootElement.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String)) { invalid = true; return []; }
            return document.RootElement.EnumerateArray().Select(x => x.GetString()!).ToArray();
        }
        catch (JsonException) { invalid = true; return []; }
    }

    /// <summary>Canonicalizes only accepted public URL/bare username forms before the shared strict normalizer.</summary>
    /// <param name="raw">One saved channel setting, never a token.</param>
    /// <param name="normalized">Canonical numeric id or @username, empty on rejection.</param>
    /// <returns>True for nonzero numeric ids and plausible public usernames only.</returns>
    /// <remarks>Invites, post URLs, /c/ paths, arbitrary hosts, credentials, query strings and fragments are rejected.</remarks>
    private static bool TryNormalizeChannel(string? raw, out string normalized)
    {
        normalized = string.Empty;
        var value = raw?.Trim();
        if (string.IsNullOrEmpty(value)) return false;
        if (value.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                (uri.Host != "t.me" && uri.Host != "telegram.me") || !uri.IsDefaultPort ||
                uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0) return false;
            var path = uri.AbsolutePath;
            if (path.Length < 2 || path[0] != '/' || path.AsSpan(1).Contains('/') || path.Contains('%')) return false;
            value = "@" + path[1..];
        }
        else if (value[0] != '@' && !long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)) value = "@" + value;
        return TelegramDestination.TryNormalize(value, out normalized, out _);
    }

    /// <summary>Resolves every candidate with its own bot identity, real public channel and actual post permission.</summary>
    /// <param name="operation">Original immutable text/entities/photos for footer-inclusive validation.</param>
    /// <param name="token">Cancellable background operation lifetime.</param>
    /// <returns>Frozen stably sorted targets and sanitized preparation exclusions.</returns>
    /// <remarks>Aliases deduplicate only within one internal bot; different bot associations sharing a channel remain.</remarks>
    private async Task<Prepared> PrepareDestinationsAsync(Operation operation, CancellationToken token)
    {
        var targets = new List<Target>();
        var skipped = new List<PublicChannelPostTargetResult>();
        foreach (var bot in await ReadInventoryAsync(token))
        {
            token.ThrowIfCancellationRequested();
            if (bot.InvalidChannels) { skipped.Add(Detail(bot.Id, "تنظیم کانال", "skipped", "invalid_channel_list")); continue; }
            if (bot.Channels.Length == 0) continue;
            var excluded = !bot.Enabled ? "bot_disabled" : !bot.HasToken || bot.Identity is not > 0 ? "bot_transport_unavailable" :
                bot.Tenant && !bot.Participates ? "owner_opted_out" : null;
            var candidates = new List<string>();
            foreach (var raw in bot.Channels)
            {
                if (!TryNormalizeChannel(raw, out var channel)) { skipped.Add(Detail(bot.Id, "تنظیم کانال", "skipped", "invalid_channel_setting")); continue; }
                if (excluded != null) skipped.Add(Detail(bot.Id, channel, "skipped", excluded));
                else if (!candidates.Contains(channel, StringComparer.OrdinalIgnoreCase)) candidates.Add(channel);
            }
            if (candidates.Count == 0) continue;
            ITelegramBotClient client;
            Telegram.Bot.Types.User me;
            try
            {
                client = _clients.GetClient(bot.Id, bot.Identity!.Value);
                me = await WithBudgetAsync(ct => client.GetMe(ct), TimeSpan.FromSeconds(5), token);
                if (me.Id != bot.Identity || !ValidUsername(me.Username)) throw new PreparationException("bot_identity_unavailable", string.Empty);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                foreach (var channel in candidates) skipped.Add(Detail(bot.Id, channel, "skipped", "bot_transport_unavailable"));
                continue;
            }
            var byChat = new Dictionary<long, Target>();
            foreach (var channel in candidates)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var chat = await WithBudgetAsync(ct => client.GetChat(ToChatId(channel), ct), TimeSpan.FromSeconds(5), token);
                    if (chat.Type != ChatType.Channel || chat.Id >= 0)
                    { skipped.Add(Detail(bot.Id, channel, "skipped", "destination_is_not_channel")); continue; }
                    if (!ValidUsername(chat.Username))
                    { skipped.Add(Detail(bot.Id, channel, "skipped", "channel_has_no_public_username")); continue; }
                    var member = await WithBudgetAsync(ct => client.GetChatMember(chat.Id, me.Id, ct), TimeSpan.FromSeconds(5), token);
                    if (member is not ChatMemberAdministrator { CanPostMessages: true })
                    { skipped.Add(Detail(bot.Id, channel, "skipped", "channel_post_permission_missing")); continue; }
                    if (byChat.TryGetValue(chat.Id, out var prior))
                    {
                        byChat[chat.Id] = prior with { Associations = [.. prior.Associations, channel] };
                        continue;
                    }
                    var (text, entities) = AppendFooter(operation.Content, me.Username!, chat.Username!);
                    var limit = operation.Content.Photos.Length == 0 ? 4096 : 1024;
                    if (text.Length > limit)
                        throw new PreparationException("content_too_long", $"متن نهایی برای @{chat.Username} بیشتر از {limit} نویسه است؛ متن را کوتاه‌تر کنید.");
                    byChat.Add(chat.Id, new Target(bot.Id, bot.Tenant, bot.Identity.Value, chat.Id, me.Username!, chat.Username!,
                        [channel], text, entities));
                }
                catch (PreparationException) { throw; }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception) { skipped.Add(Detail(bot.Id, channel, "skipped", "channel_inaccessible")); }
            }
            targets.AddRange(byChat.Values);
        }
        return new Prepared(targets.OrderBy(x => x.BotId, StringComparer.Ordinal).ThenBy(x => x.ChatId).ToArray(), skipped.ToArray());
    }

    /// <summary>Validates usernames supplied by Telegram before adding clickable footer links.</summary>
    /// <param name="username">Telegram getMe/getChat username, without @.</param>
    /// <returns>True for a plausible public username.</returns>
    private static bool ValidUsername(string? username) => !string.IsNullOrEmpty(username) &&
        TelegramDestination.TryNormalize("@" + username, out _, out _);

    /// <summary>Converts an already canonical numeric or public setting to a typed SDK chat id.</summary>
    /// <param name="channel">Strict canonical channel selector.</param>
    /// <returns>Numeric SDK chat id when numeric; public username otherwise.</returns>
    private static ChatId ToChatId(string channel) => long.TryParse(channel, NumberStyles.AllowLeadingSign,
        CultureInfo.InvariantCulture, out var id) ? new ChatId(id) : new ChatId(channel);

    /// <summary>Appends the exact per-association footer and UTF-16 TextLink entities without parse mode.</summary>
    /// <param name="content">Original immutable common text/entities.</param>
    /// <param name="botUsername">Actual sending bot username, without @.</param>
    /// <param name="channelUsername">Actual public destination username, without @.</param>
    /// <returns>Final visible text and original-plus-footer entities, preserving original offsets.</returns>
    private static (string Text, MessageEntity[] Entities) AppendFooter(Content content, string botUsername, string channelUsername)
    {
        var start = content.Text.Length == 0 ? string.Empty : "\n\n";
        var botPrefix = start + "🤖 ربات: ";
        var channelPrefix = "\n📣 کانال: ";
        var text = content.Text + botPrefix + "@" + botUsername + channelPrefix + "@" + channelUsername;
        var entities = new MessageEntity[content.Entities.Length + 2];
        Array.Copy(content.Entities, entities, content.Entities.Length);
        entities[^2] = new MessageEntity { Type = MessageEntityType.TextLink, Offset = content.Text.Length + botPrefix.Length,
            Length = botUsername.Length + 1, Url = "https://t.me/" + botUsername };
        entities[^1] = new MessageEntity { Type = MessageEntityType.TextLink,
            Offset = content.Text.Length + botPrefix.Length + botUsername.Length + 1 + channelPrefix.Length,
            Length = channelUsername.Length + 1, Url = "https://t.me/" + channelUsername };
        return (text, entities);
    }

    /// <summary>Downloads each unique source file once into bounded draft-owned disk assets.</summary>
    /// <param name="operation">Frozen ordered photo metadata retaining this draft's assets.</param>
    /// <param name="source">Exact original source bot client.</param>
    /// <param name="token">Cancellable preparation lifetime.</param>
    /// <returns>Completion with all occurrences referencing reusable completed files.</returns>
    /// <remarks>Partial files are deleted only after awaited download/stream disposal. Completed cache entries survive
    /// caption edits, new photos and preview navigation; no bytes are cached per destination.</remarks>
    private async Task EnsureAssetsAsync(Operation operation, ITelegramBotClient source, CancellationToken token)
    {
        var draft = operation.Draft;
        foreach (var photo in operation.Content.Photos)
        {
            token.ThrowIfCancellationRequested();
            if (!IsAuthorized(draft.Key, out var identity) || identity != draft.SourceIdentity)
                throw new PreparationException("source_identity_changed", "هویت ربات مبدأ تغییر کرده است؛ پست تازه‌ای بسازید.");
            var cacheKey = draft.SourceIdentity.ToString(CultureInfo.InvariantCulture) + ":" + photo.FileId;
            if (draft.Assets.ContainsKey(cacheKey)) continue;
            if (photo.Size > MaximumPhotoBytes) throw new PreparationException("photo_too_large", "حجم هر عکس باید حداکثر ۱۰ مگابایت باشد.");
            Directory.CreateDirectory(draft.DirectoryPath);
            var path = Path.Combine(draft.DirectoryPath, Guid.NewGuid().ToString("N") + ".jpg");
            var complete = false;
            try
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
                budget.CancelAfter(_delivery.MediaGroupBudget);
                var file = await source.GetFile(photo.FileId, budget.Token);
                if (file.FileSize > MaximumPhotoBytes || string.IsNullOrEmpty(file.FilePath))
                    throw new PreparationException("photo_too_large", "دریافت عکس ناموفق بود یا حجم آن بیشتر از ۱۰ مگابایت است.");
                await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    using var bounded = new LimitedWriteStream(output, MaximumPhotoBytes);
                    await source.DownloadFile(file.FilePath, bounded, budget.Token);
                    await output.FlushAsync(budget.Token);
                    if (output.Length == 0) throw new PreparationException("photo_download_failed", "دریافت عکس ناموفق بود؛ دوباره پیش‌نمایش بگیرید.");
                }
                draft.Assets.Add(cacheKey, path);
                complete = true;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (PreparationException) { throw; }
            catch (Exception) { throw new PreparationException("photo_download_failed", "دریافت عکس ناموفق بود؛ محتوا حفظ شده است. دوباره پیش‌نمایش بگیرید."); }
            finally
            {
                if (!complete)
                {
                    try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
        }
    }

    /// <summary>Executes one bounded raw-client request without SDK/application retry.</summary>
    /// <typeparam name="T">SDK response type.</typeparam>
    /// <param name="request">Request accepting a linked timeout token; never retained after completion.</param>
    /// <param name="budget">Maximum request duration, in wall-clock time.</param>
    /// <param name="token">Operation/host lifetime token.</param>
    /// <returns>The actual SDK response, or the original failure for caller-specific classification.</returns>
    private static async Task<T> WithBudgetAsync<T>(Func<CancellationToken, Task<T>> request, TimeSpan budget, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
        linked.CancelAfter(budget);
        return await request(linked.Token);
    }

    /// <summary>Token-free current bot inventory; Owned registry and Tenant database sources remain distinct.</summary>
    /// <param name="Id">Internal association-owning bot id.</param>
    /// <param name="Tenant">Whether the detached Tenant database row is authoritative.</param>
    /// <param name="Enabled">Current source enabled state.</param>
    /// <param name="HasToken">Whether a configured token exists, without retaining it.</param>
    /// <param name="Participates">Exact Tenant owner participation preference; ignored for Owned.</param>
    /// <param name="Identity">Numeric token-prefix identity, or null for malformed/missing tokens.</param>
    /// <param name="Channels">Current stored channel strings; never normalized back into settings.</param>
    /// <param name="InvalidChannels">Whether stored JSON is invalid.</param>
    private sealed record InventoryBot(string Id, bool Tenant, bool Enabled, bool HasToken, bool Participates,
        long? Identity, string[] Channels, bool InvalidChannels);

    /// <summary>Frozen association and complete previewed representation; contains no clients or credentials.</summary>
    /// <param name="BotId">Internal exact sending bot id.</param>
    /// <param name="Tenant">Whether final eligibility must read this exact Tenant row.</param>
    /// <param name="Identity">Frozen numeric BotFather identity.</param>
    /// <param name="ChatId">Resolved canonical negative CHANNEL id, never a private recipient.</param>
    /// <param name="BotUsername">Actual previewed sending bot username.</param>
    /// <param name="ChannelUsername">Actual previewed public channel username.</param>
    /// <param name="Associations">Canonical saved selectors that resolved to this bot/channel association.</param>
    /// <param name="Text">Final footer-inclusive visible text/caption.</param>
    /// <param name="Entities">Original formatting plus exact footer TextLink entities.</param>
    private sealed record Target(string BotId, bool Tenant, long Identity, long ChatId, string BotUsername,
        string ChannelUsername, string[] Associations, string Text, MessageEntity[] Entities);

    /// <summary>Immutable validated inventory reused by preview navigation and frozen for publication.</summary>
    /// <param name="Targets">Eligible associations in stable bot-id/numeric-channel order.</param>
    /// <param name="Skipped">Sanitized preparation exclusions, separate from publication denominator.</param>
    private sealed record Prepared(Target[] Targets, PublicChannelPostTargetResult[] Skipped);

    /// <summary>Application-authored preparation failure carrying only closed code and sanitized user notice.</summary>
    private sealed class PreparationException : Exception
    {
        /// <summary>Closed machine-readable preparation reason.</summary>
        internal readonly string Reason;
        /// <summary>Safe application-generated notice; never raw provider/transport text.</summary>
        internal readonly string SafeMessage;
        /// <summary>Creates a bounded safe failure without preserving an inner raw exception.</summary>
        /// <param name="reason">Closed reason code.</param>
        /// <param name="safeMessage">Application-authored UI notice.</param>
        internal PreparationException(string reason, string safeMessage) { Reason = reason; SafeMessage = safeMessage; }
    }

    /// <summary>Non-owning write wrapper enforcing the photo byte limit even when Telegram metadata is wrong.</summary>
    /// <remarks>The enclosing awaited file scope owns disposal. All synchronous/asynchronous writes share one counter.</remarks>
    private sealed class LimitedWriteStream : Stream
    {
        /// <summary>Worker-owned underlying file stream.</summary>
        private readonly Stream _inner;
        /// <summary>Maximum permitted total downloaded bytes.</summary>
        private readonly long _limit;
        /// <summary>Bytes successfully written to the sequential stream.</summary>
        private long _written;
        /// <summary>Constructs a bounded non-owning sequential output stream.</summary>
        /// <param name="inner">Open worker-owned writable file stream.</param>
        /// <param name="limit">Maximum photo bytes, inclusive.</param>
        internal LimitedWriteStream(Stream inner, long limit) { _inner = inner; _limit = limit; }
        /// <inheritdoc />
        public override bool CanRead => false;
        /// <inheritdoc />
        public override bool CanSeek => false;
        /// <inheritdoc />
        public override bool CanWrite => true;
        /// <inheritdoc />
        public override long Length => _written;
        /// <inheritdoc />
        public override long Position { get => _written; set => throw new NotSupportedException(); }
        /// <inheritdoc />
        public override void Flush() => _inner.Flush();
        /// <inheritdoc />
        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        /// <inheritdoc />
        public override void SetLength(long value) => throw new NotSupportedException();
        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count)
        {
            CheckLimit(count); _inner.Write(buffer, offset, count); _written += count;
        }
        /// <inheritdoc />
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            CheckLimit(buffer.Length); _inner.Write(buffer); _written += buffer.Length;
        }
        /// <inheritdoc />
        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            CheckLimit(count); await _inner.WriteAsync(buffer, offset, count, cancellationToken); _written += count;
        }
        /// <inheritdoc />
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            CheckLimit(buffer.Length); await _inner.WriteAsync(buffer, cancellationToken); _written += buffer.Length;
        }
        /// <summary>Rejects a write before it would exceed Telegram's inclusive photo size limit.</summary>
        /// <param name="count">Incoming sequential byte count.</param>
        /// <remarks>The oversized chunk is never written; callers dispose the awaited file before deleting it.</remarks>
        private void CheckLimit(int count)
        {
            if (count > _limit - _written) throw new PreparationException("photo_too_large", "حجم هر عکس باید حداکثر ۱۰ مگابایت باشد.");
        }
    }
}
