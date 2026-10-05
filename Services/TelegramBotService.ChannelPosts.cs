using System.Globalization;
using Adminbot.Domain;
using Adminbot.Domain.Logging;
using Adminbot.Utils;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

/// <summary>Owned-only super-admin composition controls for the independent public-channel publisher.</summary>
/// <remarks>Intake is process-local; slow destination probes, downloads, previews and fan-out belong to the singleton manager.
/// No customer audience, financial setting, tenant conversation or private-customer broadcast is changed.</remarks>
public partial class TelegramBotService
{
    /// <summary>Closed ASCII callback namespace, isolated from private-customer broadcast and owner settings.</summary>
    private const string ChannelPostCallbackPrefix = "cpp:";
    /// <summary>Exact stale/invalid control notice; a replacement preview is required before confirmation.</summary>
    private const string ChannelPostStaleNotice = "این پیش‌نمایش قدیمی یا نامعتبر است؛ دوباره پیش‌نمایش بگیرید.";
    /// <summary>Exact authorization notice; colleague status and store ownership never grant platform authority.</summary>
    private const string ChannelPostDeniedNotice = "این بخش فقط برای سوپرادمین‌هاست.";
    /// <summary>Photo-only composer guidance; album collection waits for an explicit preview action, never a debounce.</summary>
    private const string ChannelPostComposerPrompt = "متن یا ۱ تا ۱۰ عکس را ارسال کنید. برای چند عکس یک کپشن مشترک بنویسید و بعد «👁 پیش‌نمایش» را بزنید.";

    /// <summary>Starts the privileged composer or consumes its current photo/text intake before legacy admin state.</summary>
    /// <param name="client">Required current source Owned-bot transport, used only for one bounded private response.</param>
    /// <param name="message">Original incoming Telegram message; sender/chat identity, not forwarded metadata, owns the draft.</param>
    /// <param name="isOwnedBot">Dispatcher-resolved Owned context; Tenant and assistant bots cannot compose.</param>
    /// <param name="cancellationToken">Incoming update lifetime, never retained by a publication job.</param>
    /// <returns>True for consumed entry/intake/cancel or denied entry; false for ordinary users, navigation and other admin actions.</returns>
    /// <remarks>Entry resets stale ordinary conversation before composition. Each accepted intake binds a freshly sent
    /// control message, so a previously submitted preview edit can affect only the older control. Unsupported media is
    /// consumed with a notice, never passed into another admin/payment handler. Already admitted jobs remain frozen.</remarks>
    /// <exception cref="OperationCanceledException">The incoming update is cancelled.</exception>
    /// <exception cref="TelegramForegroundDeliveryTimeoutException">The private response exceeds its budget; no resend occurs.</exception>
    /// <exception cref="Telegram.Bot.Exceptions.ApiRequestException">Telegram authoritatively rejects the private response.</exception>
    /// <example><code>if (await TryHandleChannelPostMessageAsync(client, message, isOwnedBot, token)) return;</code></example>
    private async Task<bool> TryHandleChannelPostMessageAsync(ITelegramBotClient client, Message message,
        bool isOwnedBot, CancellationToken cancellationToken)
    {
        var entry = string.Equals(message.Text, AdminChannelPostAction, StringComparison.Ordinal);
        if (entry)
        {
            if (!isOwnedBot || !IsChannelPostActor(message.From?.Id ?? 0, message.Chat))
            {
                await SendChannelPostNoticeAsync(client, message.Chat.Id, ChannelPostDeniedNotice, cancellationToken);
                return true;
            }
            await ResetCurrentBotConversationAsync(message.From.Id, cancellationToken);
            var initial = _publicChannelPosts.StartDraft(new(CurrentBot.Id, message.From.Id, message.Chat.Id));
            if (initial == null)
                await SendChannelPostNoticeAsync(client, message.Chat.Id, ChannelPostDeniedNotice, cancellationToken);
            else
                await SendChannelPostComposerControlAsync(client, initial, cancellationToken);
            return true;
        }
        if (!isOwnedBot || !IsChannelPostActor(message.From?.Id ?? 0, message.Chat)) return false;
        // Navigation has already reset ordinary state; admitted jobs survive that reset but must not consume /start.
        if (TelegramNavigationCommandParser.TryParse(message.Text, CurrentBot.Username, allowRefresh: true, out _) ||
            GetAdminActions().Contains(message.Text, StringComparer.Ordinal)) return false;
        var key = new PublicChannelPostKey(CurrentBot.Id, message.From.Id, message.Chat.Id);
        if (_publicChannelPosts.GetDraft(key) == null) return false;
        if (string.Equals(message.Text, "❌ لغو", StringComparison.Ordinal))
        {
            await ResetCurrentBotConversationAsync(message.From.Id, cancellationToken);
            await SendChannelPostNoticeAsync(client, message.Chat.Id, "❌ ترکیب پست لغو شد؛ ارسال‌های تأییدشده متوقف نمی‌شوند.", cancellationToken);
            return true;
        }
        var result = _publicChannelPosts.AddMessage(key, message);
        if (result.Accepted)
            await SendChannelPostComposerControlAsync(client, result.Draft, cancellationToken);
        else if (result.ReasonCode != "duplicate_message")
            await SendChannelPostNoticeAsync(client, message.Chat.Id, ChannelPostReasonNotice(result.ReasonCode), cancellationToken);
        return true;
    }

    /// <summary>Authenticates and consumes only closed, exact draft/revision/control-bound composer callbacks.</summary>
    /// <param name="client">Required current source Owned-bot transport; never stored in worker commands.</param>
    /// <param name="callback">Original callback whose From owns authority; its attached message author grants nothing.</param>
    /// <param name="cancellationToken">Update lifetime for bounded acknowledgement/status work only.</param>
    /// <returns>True for every cpp callback, including denied, malformed, expired and replayed actions; false otherwise.</returns>
    /// <remarks>Preview and publication are nonwaiting TryWrite admissions. A successful confirmation consumes its revision
    /// before any progress response, so timeout/failure/replay cannot admit another job. Refresh resolves a retained job's
    /// original key/id/revision/control even while the same administrator edits a newer draft. Tenant routing precedes this handler.</remarks>
    /// <exception cref="OperationCanceledException">The update is cancelled while an unpublished edit control is sent.</exception>
    /// <exception cref="TelegramForegroundDeliveryTimeoutException">A fresh editing control times out; publication remains unarmed.</exception>
    /// <exception cref="Telegram.Bot.Exceptions.ApiRequestException">Telegram rejects an editing control; no fallback send occurs.</exception>
    /// <example><code>if (await TryHandleChannelPostCallbackAsync(client, callback, token)) return;</code></example>
    private async Task<bool> TryHandleChannelPostCallbackAsync(ITelegramBotClient client, CallbackQuery callback,
        CancellationToken cancellationToken)
    {
        if (callback.Data?.StartsWith(ChannelPostCallbackPrefix, StringComparison.Ordinal) != true) return false;
        if (!IsSuperAdminUser(callback.From?.Id ?? 0) || CurrentBot?.Type != BotInstanceTypes.Owned)
        {
            await SafeAnswerCallbackQueryAsync(client, callback.Id, ChannelPostDeniedNotice, showAlert: true, cancellationToken: cancellationToken);
            return true;
        }
        if (callback.Message == null || !IsChannelPostActor(callback.From.Id, callback.Message.Chat) ||
            !TryParseChannelPostCallback(callback.Data, out var id, out var revision, out var action, out var index))
        {
            await SafeAnswerCallbackQueryAsync(client, callback.Id, ChannelPostStaleNotice, showAlert: true, cancellationToken: cancellationToken);
            return true;
        }
        var key = new PublicChannelPostKey(CurrentBot.Id, callback.From.Id, callback.Message.Chat.Id);
        var control = callback.Message.Id;
        if (action == "refresh")
        {
            if (!_publicChannelPosts.IsStatusBindingCurrent(key, id, revision, control))
                await SafeAnswerCallbackQueryAsync(client, callback.Id, ChannelPostStaleNotice, showAlert: true, cancellationToken: cancellationToken);
            else
            {
                await SafeAnswerCallbackQueryAsync(client, callback.Id, cancellationToken: cancellationToken);
                await _publicChannelPosts.RefreshStatusAsync(key, id, client, control, cancellationToken);
            }
            return true;
        }
        if (action == "cancel")
        {
            var current = _publicChannelPosts.GetDraft(key);
            if (current == null || current.Id != id || current.Revision != revision || current.ControlMessageId != control ||
                current.Phase is not (PublicChannelPostPhase.Editing or PublicChannelPostPhase.PreparingPreview or PublicChannelPostPhase.PreviewReady))
            {
                await SafeAnswerCallbackQueryAsync(client, callback.Id, ChannelPostStaleNotice, showAlert: true, cancellationToken: cancellationToken);
                return true;
            }
            _publicChannelPosts.CancelDraft(key.SourceBotId, key.TelegramUserId);
            await SafeAnswerCallbackQueryAsync(client, callback.Id, "پست لغو شد.", cancellationToken: cancellationToken);
            await EditChannelPostControlBestEffortAsync(client, key.ChatId, control, "❌ پست لغو شد.", new InlineKeyboardMarkup(Array.Empty<InlineKeyboardButton[]>()), cancellationToken);
            return true;
        }
        var result = action switch
        {
            "preview" or "destination" => _publicChannelPosts.QueuePreview(key, id, revision, control, index),
            "send" => _publicChannelPosts.QueuePublish(key, id, revision, control),
            "edit" => _publicChannelPosts.ResumeEditing(key, id, revision, control),
            _ => new PublicChannelPostCommandResult(false, "stale_preview", null)
        };
        if (!result.Accepted)
        {
            await SafeAnswerCallbackQueryAsync(client, callback.Id, ChannelPostReasonNotice(result.ReasonCode), showAlert: true, cancellationToken: cancellationToken);
            return true;
        }
        await SafeAnswerCallbackQueryAsync(client, callback.Id,
            action == "send" ? "انتشار در صف قرار گرفت." : action == "edit" ? "ادامه ویرایش" : "در حال آماده‌سازی پیش‌نمایش...",
            cancellationToken: cancellationToken);
        if (action == "edit")
            await SendChannelPostComposerControlAsync(client, result.Draft, cancellationToken);
        else if (action == "send")
        {
            // Admission is already irreversible. A failed progress edit cannot grant a second confirmation.
            var markup = new InlineKeyboardMarkup(new[] { new[] { InlineKeyboardButton.WithCallbackData("🔄 وضعیت ارسال",
                ChannelPostCallbackData(result.Draft, "refresh")) } });
            await EditChannelPostControlBestEffortAsync(client, key.ChatId, control,
                "📣 انتشار در صف قرار گرفت؛ وضعیت ارسال را از همین پیام پیگیری کنید.", markup, cancellationToken);
        }
        // The worker alone renders/arms preview controls; a foreground response must not overwrite its newer revision.
        return true;
    }

    /// <summary>Checks the exact active Owned source and configured super-admin in their own private chat without I/O.</summary>
    /// <param name="actor">Positive Telegram sender id from Message.From or CallbackQuery.From.</param>
    /// <param name="chat">Original Telegram chat, required private and equal to the actor.</param>
    /// <returns>True only for the exact active token-bearing Owned runtime source; false never grants composition authority.</returns>
    /// <remarks>Neither colleague profile nor tenant ownership is consulted. Registry lookup never falls back to the default bot.</remarks>
    /// <example><code>if (!IsChannelPostActor(callback.From.Id, callback.Message.Chat)) return;</code></example>
    private bool IsChannelPostActor(long actor, Chat chat)
    {
        if (actor <= 0 || chat?.Type != ChatType.Private || chat.Id != actor || !IsSuperAdminUser(actor) ||
            CurrentBot?.Type != BotInstanceTypes.Owned) return false;
        var source = _botRegistry?.Bots.FirstOrDefault(bot => string.Equals(bot.Id, CurrentBot.Id, StringComparison.Ordinal));
        return source?.Enabled == true && source.Type == BotInstanceTypes.Owned && !source.IsSalesAssistant &&
            TelegramBotTokenIdentity.ExtractBotId(source.Token) is > 0;
    }

    /// <summary>Parses only the bounded ASCII composer vocabulary, with checked hexadecimal numeric fields.</summary>
    /// <param name="data">Untrusted callback data, at most sixty-four ASCII bytes.</param>
    /// <param name="id">Validated ten-character hexadecimal draft id on success; empty on failure.</param>
    /// <param name="revision">Nonnegative hexadecimal callback revision, never an authority grant.</param>
    /// <param name="action">Closed preview/send/edit/cancel/refresh or destination action on success.</param>
    /// <param name="index">Nonnegative zero-based destination index; zero for nonnavigation actions.</param>
    /// <returns>True only for a complete valid envelope; malformed/unknown/overflowing input returns false.</returns>
    /// <remarks>Validation is syntactic only; the manager must still match actor/source/chat/id/revision/control and phase.</remarks>
    /// <example><code>TryParseChannelPostCallback(data, out var id, out var revision, out var action, out var index);</code></example>
    private static bool TryParseChannelPostCallback(string data, out string id, out long revision, out string action, out int index)
    {
        id = string.Empty; revision = 0; action = string.Empty; index = 0;
        if (data.Length > 64 || !data.StartsWith(ChannelPostCallbackPrefix, StringComparison.Ordinal)) return false;
        foreach (var value in data) if (!char.IsAscii(value)) return false;
        var remaining = data.AsSpan(ChannelPostCallbackPrefix.Length);
        var separator = remaining.IndexOf(':');
        if (separator != 10 || !IsChannelPostHex(remaining[..separator])) return false;
        var draftId = remaining[..separator];
        remaining = remaining[(separator + 1)..];
        separator = remaining.IndexOf(':');
        if (separator <= 0 || !IsChannelPostHex(remaining[..separator]) ||
            !long.TryParse(remaining[..separator], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out revision) || revision < 0) return false;
        var command = remaining[(separator + 1)..];
        if (command.SequenceEqual("preview")) action = "preview";
        else if (command.SequenceEqual("send")) action = "send";
        else if (command.SequenceEqual("edit")) action = "edit";
        else if (command.SequenceEqual("cancel")) action = "cancel";
        else if (command.SequenceEqual("refresh")) action = "refresh";
        else if (command.Length > 1 && command[0] == 'p' && IsChannelPostHex(command[1..]) &&
            int.TryParse(command[1..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out index) && index >= 0) action = "destination";
        else return false;
        id = draftId.ToString();
        return true;
    }

    /// <summary>Checks a callback numeric/id span without allocating a copied character array.</summary>
    /// <param name="value">Nonempty ASCII field from the bounded callback envelope.</param>
    /// <returns>True only when every character is an ASCII hexadecimal digit.</returns>
    /// <remarks>This does not parse the number or authorize a draft; the caller still checks overflow and bindings.</remarks>
    /// <example><code>if (!IsChannelPostHex(revisionField)) return false;</code></example>
    private static bool IsChannelPostHex(ReadOnlySpan<char> value)
    {
        foreach (var digit in value) if (!char.IsAsciiHexDigit(digit)) return false;
        return !value.IsEmpty;
    }

    /// <summary>Sends one fresh unarmed editing control and binds only Telegram's successfully returned message id.</summary>
    /// <param name="client">Current source Owned-bot transport; never stored in the manager.</param>
    /// <param name="draft">Accepted immutable current editing snapshot with exact key/id/revision.</param>
    /// <param name="cancellationToken">Incoming update lifetime, linked to the existing text-send budget.</param>
    /// <returns>Completion after a successful send and best-effort exact revision bind; failed sends leave no new binding.</returns>
    /// <remarks>New controls isolate stale already-submitted preview edits. No HTML/Markdown interpretation or send fallback is used.</remarks>
    /// <exception cref="OperationCanceledException">The incoming update is cancelled.</exception>
    /// <exception cref="TelegramForegroundDeliveryTimeoutException">The control send times out without retry.</exception>
    /// <exception cref="Telegram.Bot.Exceptions.ApiRequestException">Telegram rejects the control.</exception>
    /// <example><code>await SendChannelPostComposerControlAsync(client, accepted.Draft, token);</code></example>
    private async Task SendChannelPostComposerControlAsync(ITelegramBotClient client, PublicChannelPostDraftSnapshot draft,
        CancellationToken cancellationToken)
    {
        var text = ChannelPostComposerPrompt + $"\n\nتعداد عکس: {draft.PhotoCount} | متن/کپشن: {(draft.HasText ? "ثبت شده" : "ندارد")}" +
            (draft.HasCaptionConflict ? "\n⚠️ کپشن عکس‌ها متفاوت است؛ یک پیام متنی برای کپشن مشترک ارسال کنید." : string.Empty);
        var markup = new InlineKeyboardMarkup(new[] { new[]
        {
            InlineKeyboardButton.WithCallbackData("👁 پیش‌نمایش", ChannelPostCallbackData(draft, "preview")),
            InlineKeyboardButton.WithCallbackData("❌ لغو", ChannelPostCallbackData(draft, "cancel"))
        } });
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TelegramForegroundDeliveryPolicy.Production.OverallBudget);
        try
        {
            var control = await client.SendMessage(draft.Key.ChatId, text, replyMarkup: markup,
                linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true }, cancellationToken: budget.Token);
            _publicChannelPosts.BindControlMessage(draft.Key, draft.Id, draft.Revision, control.Id);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TelegramForegroundDeliveryTimeoutException("send_message", TelegramForegroundDeliveryPolicy.Production.OverallBudget); }
    }

    /// <summary>Builds a short ASCII callback for a manager-owned revision, never encoding actor authority.</summary>
    /// <param name="draft">Current immutable snapshot whose id/revision are bound separately to actor/chat/control.</param>
    /// <param name="action">Internally generated closed preview/cancel/refresh action.</param>
    /// <returns>ASCII cpp envelope below Telegram's sixty-four-byte limit.</returns>
    /// <remarks>Revision matching is enforced by the manager, not by the callback's mere presence.</remarks>
    /// <example><code>var data = ChannelPostCallbackData(draft, "preview");</code></example>
    private static string ChannelPostCallbackData(PublicChannelPostDraftSnapshot draft, string action)
        => $"cpp:{draft.Id}:{draft.Revision.ToString("x", CultureInfo.InvariantCulture)}:{action}";

    /// <summary>Maps closed manager failures to safe composer guidance without reflecting external payloads.</summary>
    /// <param name="reason">Application-owned manager reason code, never a Telegram error body.</param>
    /// <returns>Short plain Persian notice safe for callback alerts and private composition responses.</returns>
    /// <remarks>Stale/expired/missing/identity-changed actions all require a new valid preview. No failure grants confirmation.</remarks>
    /// <example><code>var text = ChannelPostReasonNotice(result.ReasonCode);</code></example>
    private static string ChannelPostReasonNotice(string reason) => reason switch
    {
        "unauthorized" => ChannelPostDeniedNotice,
        "queue_full" => "صف ارسال پر است؛ کمی بعد دوباره تلاش کنید.",
        "too_many_photos" => "هر پست حداکثر ۱۰ عکس دارد.",
        "unsupported_message" => "برای این پست متن یا عکس ارسال کنید.",
        "photo_too_large" => "حجم هر عکس باید حداکثر ۱۰ مگابایت باشد.",
        "multiple_albums" => "برای هر پست فقط یک آلبوم عکس ارسال کنید.",
        "caption_conflict" => "کپشن عکس‌ها متفاوت است؛ یک پیام متنی برای کپشن مشترک ارسال کنید.",
        "empty_content" => "ابتدا متن یا عکس پست را ارسال کنید.",
        "publication_already_queued" => "انتشار این پست قبلاً تأیید شده است؛ برای پست جدید دوباره وارد این بخش شوید.",
        _ => ChannelPostStaleNotice
    };

    /// <summary>Sends one bounded private composer notice without markup, retries or a fallback representation.</summary>
    /// <param name="client">Current source transport, retained only for this awaited request.</param>
    /// <param name="chatId">Original Telegram chat id for the rejected entry or consumed composition message.</param>
    /// <param name="text">Application-authored safe notice, never source caption or raw provider error.</param>
    /// <param name="cancellationToken">Incoming update lifetime, linked to the existing eight-second text budget.</param>
    /// <returns>Completion of the sole notice request.</returns>
    /// <remarks>This does not mutate content, control binding or queue admission.</remarks>
    /// <exception cref="OperationCanceledException">The incoming update is cancelled.</exception>
    /// <exception cref="TelegramForegroundDeliveryTimeoutException">The notice times out and is not repeated.</exception>
    /// <exception cref="Telegram.Bot.Exceptions.ApiRequestException">Telegram rejects the notice.</exception>
    /// <example><code>await SendChannelPostNoticeAsync(client, message.Chat.Id, ChannelPostDeniedNotice, token);</code></example>
    private static async Task SendChannelPostNoticeAsync(ITelegramBotClient client, long chatId, string text,
        CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TelegramForegroundDeliveryPolicy.Production.OverallBudget);
        try { await client.SendMessage(chatId, text, cancellationToken: budget.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TelegramForegroundDeliveryTimeoutException("send_message", TelegramForegroundDeliveryPolicy.Production.OverallBudget); }
    }

    /// <summary>Replaces controls after irreversible admission/cancellation without allowing UI failure to repeat content.</summary>
    /// <param name="client">Current source transport, never retained by a job.</param>
    /// <param name="chatId">Originating super-admin's positive private Telegram chat id.</param>
    /// <param name="messageId">Exact original control-message id whose keyboard must lose send permission.</param>
    /// <param name="text">Application-authored queued/cancelled notice.</param>
    /// <param name="markup">Refresh-only or empty markup; never another publication button.</param>
    /// <param name="cancellationToken">Update lifetime, linked to the existing text/status budget.</param>
    /// <returns>Completion after one best-effort bounded edit; failures never change admission.</returns>
    /// <remarks>Raw errors are never logged. Worker progress owns eventual terminal counts; the refresh action restores current status.</remarks>
    /// <example><code>await EditChannelPostControlBestEffortAsync(client, chat, control, queuedText, refreshMarkup, token);</code></example>
    private async Task EditChannelPostControlBestEffortAsync(ITelegramBotClient client, long chatId, int messageId,
        string text, InlineKeyboardMarkup markup, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TelegramForegroundDeliveryPolicy.Production.OverallBudget);
        try { await client.EditMessageText(chatId, messageId, text, replyMarkup: markup, cancellationToken: budget.Token); }
        catch (Exception)
        { _logger.LogWarning("Public channel post control update failed. reason={ReasonCode}", "status_update_failed"); }
    }
}
