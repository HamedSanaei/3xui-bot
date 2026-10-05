#nullable enable
using System.Globalization;
using System.Text;
using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

/// <summary>Identity-preserving one-shot publication, live eligibility checks and private retained progress.</summary>
public sealed partial class PublicChannelPostManager
{
    /// <summary>Processes one frozen job through each target's own bot and retains terminal accounting.</summary>
    /// <param name="operation">One-shot admitted immutable job; editing/navigation cannot cancel it.</param>
    /// <param name="hostToken">Host lifetime, the only cancellation that may interrupt publication.</param>
    /// <returns>Completion after each target has exactly one terminal accounting result.</returns>
    /// <remarks>Per-target/status errors are isolated. No status failure can rerun an actual content request.</remarks>
    private async Task PublishAsync(Operation operation, CancellationToken hostToken)
    {
        var draft = operation.Draft;
        var prepared = operation.Prepared!;
        lock (draft.Sync)
        {
            if (draft.Phase != PublicChannelPostPhase.PublishQueued) return;
            draft.Phase = PublicChannelPostPhase.Publishing;
        }
        await UpdateJobStatusAsync(draft, hostToken);
        try
        {
            for (var index = 0; index < prepared.Targets.Length; index++)
            {
                var target = prepared.Targets[index];
                AttemptResult outcome;
                if (hostToken.IsCancellationRequested) outcome = new("skipped", "host_stopped");
                else
                {
                    try { outcome = await SendTargetAsync(operation, target, hostToken); }
                    catch (Exception) { outcome = new("uncertain", "delivery_uncertain"); }
                }
                lock (draft.Sync)
                {
                    var progress = draft.Progress!;
                    progress.Processed++;
                    switch (outcome.Outcome)
                    {
                        case "sent": progress.Sent++; break;
                        case "skipped": progress.Skipped++; break;
                        case "failed": progress.Failed++; break;
                        default: progress.Uncertain++; break;
                    }
                    if (outcome.Outcome != "sent") progress.Details.Add(Detail(target.BotId, "@" + target.ChannelUsername, outcome.Outcome, outcome.Reason));
                }
                await UpdateJobStatusAsync(draft, hostToken);
                if (index + 1 < prepared.Targets.Length && !hostToken.IsCancellationRequested)
                {
                    try { await Task.Delay(_delayMs, hostToken); }
                    catch (OperationCanceledException) when (hostToken.IsCancellationRequested) { }
                }
            }
        }
        finally
        {
            lock (draft.Sync)
            {
                draft.Phase = PublicChannelPostPhase.Completed;
                draft.Expires = DateTimeOffset.UtcNow + Lifetime;
            }
            await UpdateJobStatusAsync(draft, hostToken);
            if (!hostToken.IsCancellationRequested)
            {
                try
                {
                    if (IsAuthorized(draft.Key, out var identity) && identity == draft.SourceIdentity)
                    {
                        var client = _clients.GetClient(draft.Key.SourceBotId, draft.SourceIdentity);
                        PublicChannelPostTargetResult[] details;
                        lock (draft.Sync) details = draft.Progress!.Details.ToArray();
                        await SendDetailMessagesAsync(client, draft.Key.ChatId, details, hostToken);
                    }
                }
                catch (Exception) { _logger.LogWarning("Public channel post status update failed. reason={ReasonCode}", "status_update_failed"); }
            }
        }
    }

    /// <summary>Delivers one association, retrying only explicit 429 rejection and checking live eligibility each time.</summary>
    /// <param name="operation">Frozen publication content and source authority.</param>
    /// <param name="target">Exact previewed bot/negative-channel association and footer.</param>
    /// <param name="token">Host lifetime; update-handler timeout never reaches this job.</param>
    /// <returns>Exactly one terminal sent/skipped/failed/uncertain outcome.</returns>
    /// <remarks>Transport timeouts, 5xx and any ambiguous POST result are uncertain and never retried. A supplied
    /// nonnegative RetryAfter (including zero) waits that many seconds plus one before rechecking live eligibility;
    /// absent/negative values do not retry. Uploads reopen the same bytes/order; a committed opt-out suppresses retry.</remarks>
    private async Task<AttemptResult> SendTargetAsync(Operation operation, Target target, CancellationToken token)
    {
        for (var retry = 0; ; retry++)
        {
            if (token.IsCancellationRequested) return new("skipped", "host_stopped");
            if (!IsAuthorized(operation.Draft.Key, out var sourceIdentity) || sourceIdentity != operation.Draft.SourceIdentity)
                return new("skipped", "source_authority_lost");
            LiveTarget live;
            try { live = await ResolveLiveTargetAsync(target, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return new("skipped", "host_stopped"); }
            catch (Exception) { return new("skipped", "destination_probe_failed"); }
            if (live.Client == null) return new("skipped", live.Reason);
            if (!IsAuthorized(operation.Draft.Key, out sourceIdentity) || sourceIdentity != operation.Draft.SourceIdentity)
                return new("skipped", "source_authority_lost");
            try
            {
                // This is the only content POST. Every failure after it starts is classified without a fallback send.
                await SendContentAsync(live.Client, target.ChatId, operation.Content, target, operation.Draft, preview: false, token);
                return new("sent", string.Empty);
            }
            catch (ApiRequestException ex) when (ex.ErrorCode == 429)
            {
                var seconds = ex.Parameters?.RetryAfter;
                if (retry >= _maxRetryCount || seconds is not >= 0) return new("failed", "telegram_rate_limit_rejected");
                try { await Task.Delay(TimeSpan.FromSeconds((double)seconds.Value + 1), token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return new("skipped", "host_stopped_after_rate_limit"); }
            }
            catch (ApiRequestException ex) when (ex.ErrorCode >= 400 && ex.ErrorCode < 500)
            { return new("failed", "telegram_send_rejected"); }
            catch (Exception) { return new("uncertain", "delivery_uncertain"); }
        }
    }

    /// <summary>Re-reads the exact association and probes actual current identity/channel/permission before each POST.</summary>
    /// <param name="target">Frozen previewed association, identity, usernames and saved selectors.</param>
    /// <param name="token">Host lifetime for detached DB reads and five-second metadata probes.</param>
    /// <returns>Exact current raw transport, or sanitized reason why this association must be skipped.</returns>
    /// <remarks>All contexts are disposed before Telegram I/O. A final database/configuration read after probes catches
    /// opt-out committed while probes were pending; no newly configured destination or unpreviewed footer is substituted.</remarks>
    private async Task<LiveTarget> ResolveLiveTargetAsync(Target target, CancellationToken token)
    {
        var current = await ReadLiveBotAsync(target, token);
        var reason = LiveEligibilityReason(current, target);
        if (reason.Length != 0) return new(null, reason);
        ITelegramBotClient client;
        try { client = _clients.GetClient(target.BotId, target.Identity); }
        catch (Exception) { return new(null, "bot_transport_unavailable"); }
        Telegram.Bot.Types.User me;
        try { me = await WithBudgetAsync(ct => client.GetMe(ct), TimeSpan.FromSeconds(5), token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { return new(null, "bot_transport_unavailable"); }
        if (me.Id != target.Identity || !string.Equals(me.Username, target.BotUsername, StringComparison.Ordinal)) return new(null, "destination_changed");
        var selector = FindCurrentAssociation(current!, target);
        try
        {
            var association = await WithBudgetAsync(ct => client.GetChat(ToChatId(selector!), ct), TimeSpan.FromSeconds(5), token);
            if (association.Id != target.ChatId || association.Type != ChatType.Channel ||
                !string.Equals(association.Username, target.ChannelUsername, StringComparison.Ordinal)) return new(null, "destination_changed");
            var chat = await WithBudgetAsync(ct => client.GetChat(target.ChatId, ct), TimeSpan.FromSeconds(5), token);
            if (chat.Id != target.ChatId || chat.Id >= 0 || chat.Type != ChatType.Channel ||
                !string.Equals(chat.Username, target.ChannelUsername, StringComparison.Ordinal)) return new(null, "destination_changed");
            var member = await WithBudgetAsync(ct => client.GetChatMember(target.ChatId, me.Id, ct), TimeSpan.FromSeconds(5), token);
            if (member is not ChatMemberAdministrator { CanPostMessages: true }) return new(null, "channel_post_permission_missing");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { return new(null, "channel_post_permission_missing"); }
        var latest = await ReadLiveBotAsync(target, token);
        reason = LiveEligibilityReason(latest, target);
        if (reason.Length != 0) return new(null, reason);
        if (!string.Equals(FindCurrentAssociation(latest!, target), selector, StringComparison.OrdinalIgnoreCase))
            return new(null, "destination_changed");
        try { client = _clients.GetClient(target.BotId, target.Identity); }
        catch (Exception) { return new(null, "bot_transport_unavailable"); }
        return new(client, string.Empty);
    }

    /// <summary>Reads one exact current destination bot without retaining its context, tracker or token.</summary>
    /// <param name="target">Frozen association deciding Owned-registry versus Tenant-database authority.</param>
    /// <param name="token">Host lifetime for a detached database read.</param>
    /// <returns>Token-free inventory or null when the exact bot no longer exists.</returns>
    private async Task<InventoryBot?> ReadLiveBotAsync(Target target, CancellationToken token)
    {
        if (!target.Tenant)
        {
            var bot = _bots.Bots.FirstOrDefault(x => x.Id == target.BotId && x.Type == BotInstanceTypes.Owned && !x.IsSalesAssistant);
            return bot == null ? null : new InventoryBot(bot.Id, false, bot.Enabled, !string.IsNullOrWhiteSpace(bot.Token), true,
                TelegramBotTokenIdentity.ExtractBotId(bot.Token), bot.ChannelIds?.ToArray() ?? [], false);
        }
        await using var db = _users.CreateDbContext();
        var row = await db.BotInstances.AsNoTracking().Where(x => x.Id == target.BotId && x.Type == BotInstanceTypes.Tenant)
            .Select(x => new { x.Id, x.Enabled, x.Token, x.TenantPublicChannelPostsEnabled, x.TenantChannelIdsJson }).SingleOrDefaultAsync(token);
        if (row == null) return null;
        var channels = ParseChannels(row.TenantChannelIdsJson, out var invalid);
        return new InventoryBot(row.Id, true, row.Enabled, !string.IsNullOrWhiteSpace(row.Token), row.TenantPublicChannelPostsEnabled,
            TelegramBotTokenIdentity.ExtractBotId(row.Token), channels, invalid);
    }

    /// <summary>Checks current exact bot state and frozen channel association without network work.</summary>
    /// <param name="current">Current token-free Owned/ Tenant source, or null if removed.</param>
    /// <param name="target">Frozen previewed association.</param>
    /// <returns>Empty for eligible metadata, otherwise one closed skip reason.</returns>
    private static string LiveEligibilityReason(InventoryBot? current, Target target)
    {
        if (current == null) return "destination_changed";
        if (!current.Enabled) return "bot_disabled";
        if (current.Tenant && !current.Participates) return "owner_opted_out";
        if (!current.HasToken) return "bot_transport_unavailable";
        if (current.Identity != target.Identity || current.InvalidChannels || FindCurrentAssociation(current, target) == null) return "destination_changed";
        return string.Empty;
    }

    /// <summary>Finds a still-configured selector belonging to the previewed numeric/public association.</summary>
    /// <param name="current">Live exact bot inventory.</param>
    /// <param name="target">Frozen numeric channel, public username and original aliases.</param>
    /// <returns>Canonical configured selector, or null when association has disappeared.</returns>
    /// <remarks>Public URL/bare forms canonicalize identically; newly added unrelated channels are never admitted.</remarks>
    private static string? FindCurrentAssociation(InventoryBot current, Target target)
    {
        foreach (var raw in current.Channels)
        {
            if (!TryNormalizeChannel(raw, out var channel)) continue;
            if (target.Associations.Contains(channel, StringComparer.OrdinalIgnoreCase) ||
                string.Equals(channel, "@" + target.ChannelUsername, StringComparison.OrdinalIgnoreCase) ||
                channel == target.ChatId.ToString(CultureInfo.InvariantCulture)) return channel;
        }
        return null;
    }

    /// <summary>Sends exactly the prepared representation using original ids for private preview or fresh uploads for channels.</summary>
    /// <param name="client">Exact source preview client or exact destination publication client.</param>
    /// <param name="chatId">Originating positive private chat for preview, resolved negative CHANNEL id for publication.</param>
    /// <param name="content">Frozen original ordered photo occurrences.</param>
    /// <param name="target">Frozen final text/caption and explicit entities.</param>
    /// <param name="draft">Asset-owning draft retained by this operation's lease.</param>
    /// <param name="preview">True only for source-bot original-file-id private preview.</param>
    /// <param name="token">Operation/host cancellation, linked to the request's text/media budget.</param>
    /// <returns>Completion only after the actual SDK content request returns successfully.</returns>
    /// <remarks>Two through ten photos form exactly one media group, caption/entities only on its first item.
    /// Every destination/retry opens independent read streams, held through awaited request completion and disposed in finally.</remarks>
    private async Task SendContentAsync(ITelegramBotClient client, long chatId, Content content, Target target, Draft draft,
        bool preview, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (content.Photos.Length == 0)
        {
            await WithBudgetAsync(ct => client.SendMessage(chatId, target.Text, entities: target.Entities,
                linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true }, cancellationToken: ct), _delivery.OverallBudget, token);
            return;
        }
        var streams = new List<FileStream>(preview ? 0 : content.Photos.Length);
        try
        {
            var inputs = new InputFile[content.Photos.Length];
            for (var index = 0; index < content.Photos.Length; index++)
            {
                if (preview) inputs[index] = InputFile.FromFileId(content.Photos[index].FileId);
                else
                {
                    var cacheKey = draft.SourceIdentity.ToString(CultureInfo.InvariantCulture) + ":" + content.Photos[index].FileId;
                    var stream = new FileStream(draft.Assets[cacheKey], FileMode.Open, FileAccess.Read, FileShare.Read,
                        81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    streams.Add(stream);
                    inputs[index] = InputFile.FromStream(stream, $"photo-{index + 1}.jpg");
                }
            }
            if (inputs.Length == 1)
            {
                await WithBudgetAsync(ct => client.SendPhoto(chatId, inputs[0], caption: target.Text,
                    captionEntities: target.Entities, cancellationToken: ct), _delivery.MediaGroupBudget, token);
                return;
            }
            var media = new IAlbumInputMedia[inputs.Length];
            for (var index = 0; index < inputs.Length; index++)
                media[index] = new InputMediaPhoto(inputs[index])
                { Caption = index == 0 ? target.Text : null, CaptionEntities = index == 0 ? target.Entities : null };
            await WithBudgetAsync(ct => client.SendMediaGroup(chatId, media, cancellationToken: ct), _delivery.MediaGroupBudget, token);
        }
        finally
        {
            foreach (var stream in streams) await stream.DisposeAsync();
        }
    }

    /// <summary>Refreshes only an authorized retained job's exact original progress control, never sending content again.</summary>
    /// <param name="key">Incoming exact source/actor/private-chat ownership.</param>
    /// <param name="draftId">Retained admitted/completed job id, possibly no longer the active composition.</param>
    /// <param name="statusClient">Foreground source client used only during this invocation, never retained by a job.</param>
    /// <param name="controlMessageId">Exact original bound job control-message id.</param>
    /// <param name="cancellationToken">Incoming refresh lifetime, bounded additionally for each status/detail request.</param>
    /// <returns>Completion after bounded status/details delivery; invalid bindings perform no network work.</returns>
    /// <remarks>Parent validates the callback revision with IsStatusBindingCurrent before calling this API.
    /// UI failures are swallowed with sanitized diagnostics and cannot alter admission or trigger a content retry.</remarks>
    /// <example><code>await manager.RefreshStatusAsync(key, draftId, sourceClient, originalControlId, cancellationToken);</code></example>
    public async Task RefreshStatusAsync(PublicChannelPostKey key, string draftId, ITelegramBotClient statusClient,
        int controlMessageId, CancellationToken cancellationToken)
    {
        if (!_retained.TryGetValue(draftId, out var draft)) return;
        long revision;
        lock (draft.Sync) revision = draft.Revision;
        if (!IsStatusBindingCurrent(key, draftId, revision, controlMessageId)) return;
        try
        {
            PublicChannelPostProgressSnapshot progress;
            PublicChannelPostPhase phase;
            lock (draft.Sync) { progress = ProgressSnapshot(draft.Progress!, includeDetails: true); phase = draft.Phase; }
            await WithBudgetAsync(ct => statusClient.EditMessageText(key.ChatId, controlMessageId, JobControlText(phase, progress),
                replyMarkup: StatusKeyboard(draft.Id, revision), cancellationToken: ct), _delivery.OverallBudget, cancellationToken);
            if (IsStatusBindingCurrent(key, draftId, revision, controlMessageId))
                await SendDetailMessagesAsync(statusClient, key.ChatId, progress.Details, cancellationToken);
        }
        catch (Exception) { _logger.LogWarning("Public channel post status update failed. reason={ReasonCode}", "status_update_failed"); }
    }

    /// <summary>Updates the retained original job control through an exact source identity-bound raw client.</summary>
    /// <param name="draft">Retained admitted/completed job owning original binding and accounting.</param>
    /// <param name="token">Host lifetime, never a captured scoped handler token.</param>
    /// <returns>Completion after a best-effort bounded status edit.</returns>
    /// <remarks>Status failure never changes terminal counts or retries channel content. Summary-only snapshots avoid
    /// repeatedly copying an accumulating exclusion/failure list after every destination.</remarks>
    private async Task UpdateJobStatusAsync(Draft draft, CancellationToken token)
    {
        if (token.IsCancellationRequested || !IsAuthorized(draft.Key, out var identity) || identity != draft.SourceIdentity) return;
        try
        {
            PublicChannelPostProgressSnapshot progress;
            PublicChannelPostPhase phase;
            long revision;
            int control;
            lock (draft.Sync) { progress = ProgressSnapshot(draft.Progress!, includeDetails: false); phase = draft.Phase; revision = draft.Revision; control = draft.ControlMessageId; }
            var client = _clients.GetClient(draft.Key.SourceBotId, draft.SourceIdentity);
            await WithBudgetAsync(ct => client.EditMessageText(draft.Key.ChatId, control, JobControlText(phase, progress),
                replyMarkup: StatusKeyboard(draft.Id, revision), cancellationToken: ct), _delivery.OverallBudget, token);
        }
        catch (Exception) { _logger.LogWarning("Public channel post status update failed. reason={ReasonCode}", "status_update_failed"); }
    }

    /// <summary>Builds the closed ASCII callback namespace for an exact draft/revision/action.</summary>
    /// <param name="id">Ten-character hexadecimal draft id.</param>
    /// <param name="revision">Positive callback revision.</param>
    /// <param name="action">Closed action generated internally, optionally p plus hexadecimal destination index.</param>
    /// <returns>Callback data below Telegram's 64-byte limit.</returns>
    private static string Callback(string id, long revision, string action) => $"cpp:{id}:{revision.ToString("x", CultureInfo.InvariantCulture)}:{action}";

    /// <summary>Builds unarmed composer controls for the failed preparation's current revision.</summary>
    /// <param name="operation">Current exact revision/control operation.</param>
    /// <returns>Preview and cancel only; no send permission.</returns>
    private static InlineKeyboardMarkup EditingKeyboard(Operation operation) => new(new[]
    {
        new[] { InlineKeyboardButton.WithCallbackData("👁 پیش‌نمایش", Callback(operation.Draft.Id, operation.Revision, "preview")),
            InlineKeyboardButton.WithCallbackData("❌ لغو", Callback(operation.Draft.Id, operation.Revision, "cancel")) }
    });

    /// <summary>Builds selected destination navigation and final confirmation only after actual content preview.</summary>
    /// <param name="operation">Exact incremented preview callback revision/control.</param>
    /// <param name="prepared">Frozen eligible target inventory.</param>
    /// <returns>Previous/next, one send-to-all, edit and cancel controls.</returns>
    private static InlineKeyboardMarkup PreviewKeyboard(Operation operation, Prepared prepared)
    {
        var rows = new List<InlineKeyboardButton[]>();
        var navigation = new List<InlineKeyboardButton>(2);
        if (operation.DestinationIndex > 0) navigation.Add(InlineKeyboardButton.WithCallbackData("⬅️ قبلی",
            Callback(operation.Draft.Id, operation.Revision, "p" + (operation.DestinationIndex - 1).ToString("x", CultureInfo.InvariantCulture))));
        if (operation.DestinationIndex + 1 < prepared.Targets.Length) navigation.Add(InlineKeyboardButton.WithCallbackData("بعدی ➡️",
            Callback(operation.Draft.Id, operation.Revision, "p" + (operation.DestinationIndex + 1).ToString("x", CultureInfo.InvariantCulture))));
        if (navigation.Count != 0) rows.Add(navigation.ToArray());
        rows.Add([InlineKeyboardButton.WithCallbackData("✅ ارسال به همه کانال‌ها", Callback(operation.Draft.Id, operation.Revision, "send"))]);
        rows.Add([InlineKeyboardButton.WithCallbackData("✏️ ادامه ویرایش", Callback(operation.Draft.Id, operation.Revision, "edit")),
            InlineKeyboardButton.WithCallbackData("❌ لغو", Callback(operation.Draft.Id, operation.Revision, "cancel"))]);
        return new InlineKeyboardMarkup(rows);
    }

    /// <summary>Builds a refresh-only retained job keyboard, never exposing another confirmation.</summary>
    /// <param name="id">Retained job's draft identity.</param>
    /// <param name="revision">Frozen admitted callback revision.</param>
    /// <returns>One exact-bound status-refresh button.</returns>
    private static InlineKeyboardMarkup StatusKeyboard(string id, long revision) => new(new[]
    { new[] { InlineKeyboardButton.WithCallbackData("🔄 وضعیت ارسال", Callback(id, revision, "refresh")) } });

    /// <summary>Renders bounded safe private preview controls with selected identities and target counts.</summary>
    /// <param name="prepared">Frozen eligible targets/exclusions.</param>
    /// <param name="index">Zero-based selected destination.</param>
    /// <returns>Private status text, not the actual preview content.</returns>
    private static string PreviewControlText(Prepared prepared, int index)
    {
        var target = prepared.Targets[index];
        return $"پیش‌نمایش {index + 1} از {prepared.Targets.Length}\n🤖 ربات: @{target.BotUsername}\n📣 کانال: @{target.ChannelUsername}\n" +
            $"کانال‌های واجد شرایط: {prepared.Targets.Length}\nردشده در آماده‌سازی: {prepared.Skipped.Length}\n" +
            "این پیش‌نمایش فقط برای شماست؛ تا تأیید نکنید هیچ پستی در کانال‌ها منتشر نمی‌شود.";
    }

    /// <summary>Renders summary accounting without listing every successful delivery.</summary>
    /// <param name="phase">Retained queued/running/completed phase.</param>
    /// <param name="progress">Detached exact terminal-count accounting.</param>
    /// <returns>Bounded safe Persian progress/final status.</returns>
    private static string JobControlText(PublicChannelPostPhase phase, PublicChannelPostProgressSnapshot progress) =>
        (phase == PublicChannelPostPhase.Completed ? "✅ انتشار پایان یافت." : "📣 انتشار در صف / در حال انجام است.") +
        $"\nکانال‌های آماده: {progress.EligibleTotal}\nپردازش‌شده: {progress.Processed} از {progress.EligibleTotal}" +
        $"\nارسال موفق: {progress.Sent}\nردشده در آماده‌سازی: {progress.SkippedDuringPreparation}" +
        $"\nردشده پس از آماده‌سازی: {progress.SkippedAfterPreparation}\nناموفق: {progress.Failed}\nنتیجه نامطمئن: {progress.Uncertain}" +
        (progress.Uncertain == 0 ? string.Empty : "\nارسال‌های نامطمئن خودکار تکرار نمی‌شوند؛ کانال را بررسی کنید.");

    /// <summary>Sends sanitized excluded/failed/uncertain details as consecutive messages of at most 4096 UTF-16 units.</summary>
    /// <param name="client">Authorized exact source bot client; never a target client.</param>
    /// <param name="chat">Original positive private super-admin chat.</param>
    /// <param name="details">Application-sanitized target outcomes, excluding successful deliveries.</param>
    /// <param name="token">Status invocation or host lifetime.</param>
    /// <returns>Completion after consecutive bounded messages; no callback/page namespace is introduced.</returns>
    private async Task SendDetailMessagesAsync(ITelegramBotClient client, long chat, IEnumerable<PublicChannelPostTargetResult> details, CancellationToken token)
    {
        var text = new StringBuilder();
        foreach (var item in details)
        {
            var line = $"{item.BotId} / {item.Channel}: {item.Outcome} — {item.ReasonCode}\n";
            if (text.Length + line.Length > 4096)
            {
                await WithBudgetAsync(ct => client.SendMessage(chat, text.ToString(),
                    linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true }, cancellationToken: ct), _delivery.OverallBudget, token);
                text.Clear();
            }
            text.Append(line);
        }
        if (text.Length != 0)
            await WithBudgetAsync(ct => client.SendMessage(chat, text.ToString(),
                linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true }, cancellationToken: ct), _delivery.OverallBudget, token);
    }

    /// <summary>Creates one bounded safe detail, never reflecting arbitrary stored settings or raw errors.</summary>
    /// <param name="botId">Internal bot id sanitized to printable ASCII label.</param>
    /// <param name="channel">Strictly normalized channel/numeric id or application-authored generic label.</param>
    /// <param name="outcome">Closed skipped/failed/uncertain category.</param>
    /// <param name="reason">Closed application reason code.</param>
    /// <returns>Safe bounded target detail suitable for the original super-admin only.</returns>
    private static PublicChannelPostTargetResult Detail(string botId, string channel, string outcome, string reason) =>
        new(new string(botId.Where(x => char.IsAsciiLetterOrDigit(x) || x is '-' or '_' or '.').Take(64).ToArray()), channel, outcome, reason);

    /// <summary>Creates a detached read-only accounting snapshot while the owning draft lock is held.</summary>
    /// <param name="progress">Mutable worker-updated counters/details.</param>
    /// <param name="includeDetails">True for explicit refresh/detail consumers; false for frequent counter-only status edits.</param>
    /// <returns>Immutable progress with a copied read-only detail collection when requested, otherwise an empty collection.</returns>
    /// <remarks>The caller holds the job lock. Omitting unused details avoids quadratic copying during fan-out.</remarks>
    /// <example><code>var summary = ProgressSnapshot(progress, includeDetails: false);</code></example>
    private static PublicChannelPostProgressSnapshot ProgressSnapshot(Progress progress, bool includeDetails) => new(progress.Eligible,
        progress.Processed, progress.Sent, progress.PreparationSkipped, progress.Skipped, progress.Failed, progress.Uncertain,
        includeDetails ? Array.AsReadOnly(progress.Details.ToArray()) : Array.Empty<PublicChannelPostTargetResult>());

    /// <summary>Mutable accounting protected by the owning draft's in-memory lock.</summary>
    private sealed class Progress
    {
        /// <summary>Frozen eligible destination count, never inflated by preparation exclusions.</summary>
        internal readonly int Eligible;
        /// <summary>Preparation exclusions, separate from the frozen denominator.</summary>
        internal readonly int PreparationSkipped;
        /// <summary>Sanitized preparation and post-admission unsuccessful details.</summary>
        internal readonly List<PublicChannelPostTargetResult> Details;
        /// <summary>Terminal results within the frozen inventory.</summary>
        internal int Processed;
        /// <summary>Actual successful content responses.</summary>
        internal int Sent;
        /// <summary>Frozen targets suppressed by final live eligibility checks.</summary>
        internal int Skipped;
        /// <summary>Authoritative Telegram content rejections.</summary>
        internal int Failed;
        /// <summary>Ambiguous content POST outcomes, never automatically replayed.</summary>
        internal int Uncertain;
        /// <summary>Initializes denominator and excluded details at atomic publication admission.</summary>
        /// <param name="prepared">Immutable inventory whose preview was actually shown.</param>
        internal Progress(Prepared prepared)
        { Eligible = prepared.Targets.Length; PreparationSkipped = prepared.Skipped.Length; Details = [.. prepared.Skipped]; }
    }

    /// <summary>One exact terminal content-attempt classification.</summary>
    /// <param name="Outcome">Closed sent/skipped/failed/uncertain category.</param>
    /// <param name="Reason">Sanitized reason code, empty only on sent.</param>
    private sealed record AttemptResult(string Outcome, string Reason);

    /// <summary>Exact currently eligible raw target transport, never stored in the frozen job.</summary>
    /// <param name="Client">Identity-bound bot transport, or null for suppressed delivery.</param>
    /// <param name="Reason">Sanitized skip code, empty when eligible.</param>
    private sealed record LiveTarget(ITelegramBotClient? Client, string Reason);
}
