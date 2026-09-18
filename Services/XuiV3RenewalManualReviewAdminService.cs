using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Adminbot.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

/// <summary>
/// Super-admin surface for the XUI v3 renewal operations that automatic reconciliation parked in manual review.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> A manual-review renewal keeps its account lock, so the customer cannot renew that account
/// again until a human decides what actually happened on the panel. Without an operator surface the only remedy was
/// editing users.db by hand, which is exactly the kind of change that can produce a double debit.
/// </para>
/// <para>
/// <b>Authorization.</b> Every entry point rechecks the configured super-admin allow-list, including the callback path,
/// because callback data is client-supplied and can be replayed or forged. A non-super-admin receives the same refusal
/// for every manual-review callback and no operation state is disclosed.
/// </para>
/// <para>
/// <b>What is displayed.</b> Internal numeric ids, fixed state categories, UTC timestamps, and the sanitized comparison
/// summary only. The account email, UUID, panel URL, API token, mutation payload, and raw panel error text are never
/// rendered, so the screen cannot leak panel or customer identity into a chat.
/// </para>
/// <para>
/// <b>Destructive-action safety.</b> Abandoning an operation is the only action that releases a customer lock, so it
/// always requires a second, separate confirmation message before the service is called.
/// </para>
/// </remarks>
public sealed class XuiV3RenewalManualReviewAdminService
{
    /// <summary>Callback prefix reserved for the manual-review administrator surface.</summary>
    private const string CallbackPrefix = "x3mr:";

    /// <summary>Callback verb that renders the pending list.</summary>
    private const string ListVerb = "l";

    /// <summary>Callback verb that re-checks one operation on the panel.</summary>
    private const string RecheckVerb = "r";

    /// <summary>Callback verb that confirms one operation as applied.</summary>
    private const string ConfirmVerb = "c";

    /// <summary>Callback verb that asks for the abandonment second confirmation.</summary>
    private const string AbandonVerb = "a";

    /// <summary>Callback verb that performs an abandonment already confirmed by the administrator.</summary>
    private const string AbandonConfirmedVerb = "a2";

    /// <summary>Callback verb that performs a legacy override abandonment already confirmed twice.</summary>
    private const string AbandonOverrideVerb = "a3";

    /// <summary>Callback verb that dismisses a confirmation prompt without any state change.</summary>
    private const string CancelVerb = "x";

    /// <summary>Maximum pending reviews shown in one list message.</summary>
    private const int ListPageSize = 10;

    private readonly XuiV3RenewalManualReviewService _manualReviewService;
    private readonly TelegramInteractionTimeouts _interactionTimeouts;
    private readonly AppConfig _appConfig;
    private readonly ILogger<XuiV3RenewalManualReviewAdminService> _logger;

    /// <summary>
    /// Creates the manual-review administrator surface.
    /// </summary>
    /// <param name="manualReviewService">Resolution service owning every durable transition and the settlement hand-off.</param>
    /// <param name="interactionTimeouts">Immutable callback-answer budget used for Telegram acknowledgements.</param>
    /// <param name="configuration">Runtime configuration supplying the super-admin allow-list.</param>
    /// <param name="logger">Local operational logger; it records operation ids and fixed categories only.</param>
    public XuiV3RenewalManualReviewAdminService(
        XuiV3RenewalManualReviewService manualReviewService,
        TelegramInteractionTimeouts interactionTimeouts,
        IConfiguration configuration,
        ILogger<XuiV3RenewalManualReviewAdminService> logger)
    {
        _manualReviewService = manualReviewService;
        _interactionTimeouts = interactionTimeouts;
        _appConfig = configuration.Get<AppConfig>() ?? new AppConfig();
        _logger = logger;
    }

    /// <summary>
    /// Reports whether one callback payload belongs to the manual-review administrator surface.
    /// </summary>
    /// <param name="callbackData">Raw callback data from an incoming callback query; null is allowed.</param>
    /// <returns><c>true</c> when the payload carries the manual-review prefix.</returns>
    /// <remarks>
    /// This is a pure routing check. It never authorizes the actor; authorization is rechecked inside
    /// <see cref="TryHandleCallbackAsync" />.
    /// </remarks>
    /// <example><code>if (XuiV3RenewalManualReviewAdminService.IsManualReviewCallback(data)) return;</code></example>
    public static bool IsManualReviewCallback(string callbackData) =>
        callbackData != null && callbackData.StartsWith(CallbackPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Handles a manual-review callback from a configured super-admin.
    /// </summary>
    /// <param name="botClient">Telegram client of the owned bot serving the callback.</param>
    /// <param name="callbackQuery">Callback query whose data starts with the manual-review prefix.</param>
    /// <param name="cancellationToken">Token that cancels panel reads, database work, Telegram edits, and acknowledgements.</param>
    /// <returns><c>true</c> when the payload was consumed, including the unauthorized and malformed cases.</returns>
    /// <remarks>
    /// The operation is always reloaded from users.db before the decision, and the durable transitions are performed by
    /// the service with conditional SQL updates, so a replayed or concurrent callback cannot resolve one operation
    /// twice or move money twice.
    /// </remarks>
    /// <example><code>await adminService.TryHandleCallbackAsync(botClient, callbackQuery, token);</code></example>
    public async Task<bool> TryHandleCallbackAsync(
        ITelegramBotClient botClient,
        CallbackQuery callbackQuery,
        CancellationToken cancellationToken)
    {
        var data = callbackQuery?.Data ?? string.Empty;
        if (!data.StartsWith(CallbackPrefix, StringComparison.Ordinal))
            return false;

        var actorTelegramUserId = callbackQuery?.From?.Id ?? 0;
        if (!IsConfiguredSuperAdmin(actorTelegramUserId))
        {
            _logger.LogWarning(
                "Manual-review callback rejected for a non-super-admin actor. botId={BotId}",
                BotContextAccessor.CurrentBotId);
            await AnswerSafelyAsync(botClient, callbackQuery, "اجازه انجام این عملیات را ندارید.", true, cancellationToken);
            return true;
        }

        var payload = data[CallbackPrefix.Length..];
        var separatorIndex = payload.IndexOf(':');
        var verb = separatorIndex < 0 ? payload : payload[..separatorIndex];
        var argument = separatorIndex < 0 ? null : payload[(separatorIndex + 1)..];

        if (string.Equals(verb, CancelVerb, StringComparison.Ordinal))
        {
            await EditAsync(botClient, callbackQuery, "عملیات لغو شد. هیچ تغییری ثبت نشد.", null, cancellationToken);
            await AnswerSafelyAsync(botClient, callbackQuery, "لغو شد.", false, cancellationToken);
            return true;
        }

        if (string.Equals(verb, ListVerb, StringComparison.Ordinal))
        {
            await ShowPendingListAsync(botClient, callbackQuery.Message?.Chat.Id ?? 0, cancellationToken);
            await AnswerSafelyAsync(botClient, callbackQuery, "لیست بروزرسانی شد.", false, cancellationToken);
            return true;
        }

        if (!int.TryParse(argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out var operationId) ||
            operationId <= 0)
        {
            await AnswerSafelyAsync(botClient, callbackQuery, "شناسه عملیات معتبر نیست.", true, cancellationToken);
            return true;
        }

        switch (verb)
        {
            case RecheckVerb:
                await HandleRecheckAsync(botClient, callbackQuery, operationId, cancellationToken);
                return true;

            case ConfirmVerb:
                await HandleConfirmAsync(botClient, callbackQuery, operationId, cancellationToken);
                return true;

            case AbandonVerb:
                await PromptAbandonConfirmationAsync(botClient, callbackQuery, operationId, CancellationToken.None);
                return true;

            case AbandonConfirmedVerb:
                await HandleAbandonAsync(botClient, callbackQuery, operationId, legacyOverride: false, cancellationToken);
                return true;

            case AbandonOverrideVerb:
                await HandleAbandonAsync(botClient, callbackQuery, operationId, legacyOverride: true, cancellationToken);
                return true;

            default:
                await AnswerSafelyAsync(botClient, callbackQuery, "این عملیات پشتیبانی نمی‌شود.", true, cancellationToken);
                return true;
        }
    }

    /// <summary>
    /// Sends the pending manual-review list to one super-admin chat.
    /// </summary>
    /// <param name="botClient">Telegram client of the owned bot.</param>
    /// <param name="chatId">
    /// Target chat id of the configured super-admin. Must be a non-zero Telegram chat id; a zero value means the caller
    /// had no usable destination and the send is skipped instead of throwing.
    /// </param>
    /// <param name="cancellationToken">Token that cancels the users.db read and the Telegram send.</param>
    /// <returns>A task that completes after the list message is sent.</returns>
    /// <remarks>
    /// The message carries one compact button row per operation. Callback payloads use the internal numeric key only, so
    /// no account identity or panel reference ever travels through Telegram callback data.
    /// </remarks>
    /// <example><code>await adminService.ShowPendingListAsync(botClient, message.Chat.Id, token);</code></example>
    public async Task ShowPendingListAsync(
        ITelegramBotClient botClient,
        long chatId,
        CancellationToken cancellationToken)
    {
        if (chatId == 0)
            return;

        var pending = await _manualReviewService.ListPendingAsync(ListPageSize, cancellationToken);
        await botClient.SendMessage(
            chatId: chatId,
            text: BuildListText(pending),
            parseMode: ParseMode.Html,
            replyMarkup: BuildListKeyboard(pending),
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Builds the pending-list message text without disclosing account or panel identity.
    /// </summary>
    /// <param name="pending">Sanitized review views returned by the manual-review service.</param>
    /// <returns>HTML text listing each pending operation, or an explicit empty-state sentence.</returns>
    /// <remarks>The per-field mismatch summary is sanitized by the reconciliation engine and additionally HTML-escaped.</remarks>
    private static string BuildListText(IReadOnlyList<XuiV3RenewalManualReviewService.ManualReviewReview> pending)
    {
        var builder = new StringBuilder();
        builder.AppendLine("🧾 <b>تمدیدهای در انتظار بررسی دستی</b>");
        if (pending.Count == 0)
        {
            builder.AppendLine();
            builder.Append("در این لحظه هیچ تمدیدی در انتظار بررسی دستی نیست.");
            return builder.ToString();
        }

        builder.AppendLine();
        foreach (var item in pending)
        {
            builder.AppendLine(FormatReviewBlock(item));
            builder.AppendLine();
        }

        builder.Append("برای هر عملیات می‌توانید پنل را دوباره بررسی کنید، اعمال‌شدن را تایید کنید، یا عملیات قدیمی را رها کنید. رها کردن همیشه تایید دوم می‌خواهد.");
        return builder.ToString();
    }

    /// <summary>
    /// Formats one pending operation as a compact, secret-free block.
    /// </summary>
    /// <param name="item">Sanitized review view for one operation.</param>
    /// <returns>A multi-line HTML fragment safe for Telegram.</returns>
    private static string FormatReviewBlock(XuiV3RenewalManualReviewService.ManualReviewReview item)
    {
        var builder = new StringBuilder();
        builder.Append("• عملیات <code>#").Append(item.OperationId).Append("</code>").AppendLine();
        builder.Append("ربات: <code>").Append(Escape(item.BotId)).Append("</code>").AppendLine();
        builder.Append("کاربر: <code>").Append(item.TelegramUserId).Append("</code>").AppendLine();
        builder.Append("ایجاد: ").Append(Escape(FormatUtc(item.CreatedAtUtc))).AppendLine();
        if (item.ManualReviewAtUtc.HasValue)
            builder.Append("ارجاع به بررسی: ").Append(Escape(FormatUtc(item.ManualReviewAtUtc.Value))).AppendLine();
        builder.Append("وضعیت: <code>").Append(Escape(item.Status)).Append("</code> / <code>").Append(Escape(item.SettlementStatus)).Append("</code>").AppendLine();
        builder.Append("قابل بازیابی خودکار: ").Append(item.RecoveryEligible ? "بله" : "خیر (عملیات قدیمی)").AppendLine();
        builder.Append("تلاش‌های خودکار: ").Append(item.ReconcileAttemptCount).AppendLine();
        builder.Append("نتیجه آخرین مقایسه: <code>").Append(Escape(item.LastComparisonOutcome ?? "نامشخص")).Append("</code>").AppendLine();
        if (!string.IsNullOrWhiteSpace(item.LastMismatchSummary))
            builder.Append("جزئیات میدانی: <code>").Append(Escape(item.LastMismatchSummary)).Append("</code>").AppendLine();
        builder.Append("مبلغ تمدید: ").Append(item.PriceToman.ToString("N0", CultureInfo.InvariantCulture)).Append(" تومان");
        return builder.ToString();
    }

    /// <summary>
    /// Builds the inline keyboard with one action row per pending operation.
    /// </summary>
    /// <param name="pending">Sanitized review views returned by the manual-review service.</param>
    /// <returns>An inline keyboard whose payloads contain internal numeric ids only.</returns>
    private static InlineKeyboardMarkup BuildListKeyboard(
        IReadOnlyList<XuiV3RenewalManualReviewService.ManualReviewReview> pending)
    {
        var rows = new List<InlineKeyboardButton[]>();
        foreach (var item in pending)
        {
            rows.Add(new[]
            {
                InlineKeyboardButton.WithCallbackData("🔄 بررسی مجدد پنل", CallbackPrefix + RecheckVerb + ":" + item.OperationId),
                InlineKeyboardButton.WithCallbackData("✅ تایید اعمال‌شده", CallbackPrefix + ConfirmVerb + ":" + item.OperationId)
            });
            rows.Add(new[]
            {
                InlineKeyboardButton.WithCallbackData("🗑 رها کردن عملیات", CallbackPrefix + AbandonVerb + ":" + item.OperationId)
            });
        }

        rows.Add(new[]
        {
            InlineKeyboardButton.WithCallbackData("🔄 بروزرسانی لیست", CallbackPrefix + ListVerb)
        });
        return new InlineKeyboardMarkup(rows);
    }

    /// <summary>
    /// Runs the read-only panel re-check for one operation and reports the sanitized evidence.
    /// </summary>
    /// <param name="botClient">Telegram client used to edit the administrator message.</param>
    /// <param name="callbackQuery">Callback that requested the re-check.</param>
    /// <param name="operationId">Internal users.db key of the operation.</param>
    /// <param name="cancellationToken">Token that cancels the panel read and the Telegram edit.</param>
    /// <returns>A task that completes after the message and acknowledgement are delivered.</returns>
    /// <remarks>The re-check never changes status, settlement, or the account lock, so it is always safe to press repeatedly.</remarks>
    private async Task HandleRecheckAsync(
        ITelegramBotClient botClient,
        CallbackQuery callbackQuery,
        int operationId,
        CancellationToken cancellationToken)
    {
        var result = await _manualReviewService.ReprobeAsync(operationId, cancellationToken);
        await EditAsync(botClient, callbackQuery, BuildOutcomeText(result, "🔄 نتیجه بررسی مجدد پنل"), null, cancellationToken);
        await AnswerSafelyAsync(botClient, callbackQuery, "بررسی مجدد انجام شد.", false, cancellationToken);
    }

    /// <summary>
    /// Confirms one operation as applied and continues the existing exactly-once settlement.
    /// </summary>
    /// <param name="botClient">Telegram client used to edit the administrator message.</param>
    /// <param name="callbackQuery">Callback that requested the confirmation.</param>
    /// <param name="operationId">Internal users.db key of the operation.</param>
    /// <param name="cancellationToken">Token that cancels the panel read, the transition, and settlement work.</param>
    /// <returns>A task that completes after the message and acknowledgement are delivered.</returns>
    /// <remarks>
    /// The confirmation is deliberately single-step: it can only ever prove an already-applied renewal and then run the
    /// pre-existing settlement, which cannot debit twice.
    /// </remarks>
    private async Task HandleConfirmAsync(
        ITelegramBotClient botClient,
        CallbackQuery callbackQuery,
        int operationId,
        CancellationToken cancellationToken)
    {
        var result = await _manualReviewService.ConfirmAppliedAsync(
            operationId,
            callbackQuery.From?.Id ?? 0,
            cancellationToken);
        await EditAsync(botClient, callbackQuery, BuildOutcomeText(result, "✅ نتیجه تایید اعمال‌شده"), null, cancellationToken);
        await AnswerSafelyAsync(botClient, callbackQuery, "تایید بررسی شد.", false, cancellationToken);
    }

    /// <summary>
    /// Shows the mandatory second confirmation before an operation can be abandoned.
    /// </summary>
    /// <param name="botClient">Telegram client used to edit the administrator message.</param>
    /// <param name="callbackQuery">Callback that requested the abandonment.</param>
    /// <param name="operationId">Internal users.db key of the operation.</param>
    /// <param name="cancellationToken">Token that cancels the users.db read, the panel read, and the Telegram edit.</param>
    /// <returns>A task that completes after the confirmation prompt is shown.</returns>
    /// <remarks>
    /// The prompt re-runs the read-only comparison so the administrator sees the evidence that decides which
    /// confirmation buttons are even offered. A recovery-eligible operation shows the plain confirmation, while a
    /// historical recovery-ineligible operation additionally offers the explicit override which is recorded as such.
    /// Nothing is changed by this step.
    /// </remarks>
    private async Task PromptAbandonConfirmationAsync(
        ITelegramBotClient botClient,
        CallbackQuery callbackQuery,
        int operationId,
        CancellationToken cancellationToken)
    {
        var reprobe = await _manualReviewService.ReprobeAsync(operationId, cancellationToken);
        var review = reprobe.Review;
        if (review == null || !string.Equals(review.Status, XuiV3RenewalOperationStatuses.ManualReview, StringComparison.Ordinal))
        {
            await EditAsync(botClient, callbackQuery, BuildOutcomeText(reprobe, "⚠️ رها کردن عملیات"), null, cancellationToken);
            return;
        }

        var text = new StringBuilder()
            .AppendLine("⚠️ <b>تایید دوم لازم است</b>")
            .AppendLine()
            .AppendLine(FormatReviewBlock(review))
            .AppendLine()
            .AppendLine("با رها کردن این عملیات، قفل اکانت آزاد می‌شود و مشتری می‌تواند تمدید جدید ثبت کند. این کار فقط وقتی درست است که مطمئن باشید تمدید قبلی روی پنل اعمال نشده و هیچ مبلغی برای آن کسر نشده است.")
            .AppendLine()
            .Append("در صورت شک، گزینه انصراف را انتخاب کنید و ابتدا پنل را دوباره بررسی کنید.")
            .ToString();

        var rows = new List<InlineKeyboardButton[]>();
        if (review.RecoveryEligible)
        {
            rows.Add(new[]
            {
                InlineKeyboardButton.WithCallbackData(
                    "✅ بله، اعمال نشده و رها شود",
                    CallbackPrefix + AbandonConfirmedVerb + ":" + operationId)
            });
        }
        else
        {
            rows.Add(new[]
            {
                InlineKeyboardButton.WithCallbackData(
                    "✅ تایید مدیر ارشد: رها شود",
                    CallbackPrefix + AbandonOverrideVerb + ":" + operationId)
            });
        }

        rows.Add(new[]
        {
            InlineKeyboardButton.WithCallbackData("انصراف", CallbackPrefix + CancelVerb),
            InlineKeyboardButton.WithCallbackData("🔄 بررسی مجدد پنل", CallbackPrefix + RecheckVerb + ":" + operationId)
        });

        await EditAsync(botClient, callbackQuery, text, new InlineKeyboardMarkup(rows), CancellationToken.None);
        await AnswerSafelyAsync(botClient, callbackQuery, "برای ادامه، تایید دوم را بزنید.", false, cancellationToken);
    }

    /// <summary>
    /// Performs the confirmed abandonment of one operation.
    /// </summary>
    /// <param name="botClient">Telegram client used to edit the administrator message.</param>
    /// <param name="callbackQuery">Callback carrying the administrator's second confirmation.</param>
    /// <param name="operationId">Internal users.db key of the operation.</param>
    /// <param name="legacyOverride">
    /// Whether this confirmation is the explicit super-admin override for a recovery-ineligible historical operation.
    /// The service still refuses the override for a recovery-eligible operation.
    /// </param>
    /// <param name="cancellationToken">Token that cancels the financial receipt checks, the panel read, and the transition.</param>
    /// <returns>A task that completes after the message and acknowledgement are delivered.</returns>
    /// <remarks>
    /// The service revalidates every precondition, so a stale confirmation prompt cannot unlock an operation that has
    /// since acquired a financial artifact or moved out of manual review.
    /// </remarks>
    private async Task HandleAbandonAsync(
        ITelegramBotClient botClient,
        CallbackQuery callbackQuery,
        int operationId,
        bool legacyOverride,
        CancellationToken cancellationToken)
    {
        var result = await _manualReviewService.AbandonAsNotAppliedAsync(
            operationId,
            callbackQuery.From?.Id ?? 0,
            legacyOverride,
            cancellationToken);
        await EditAsync(botClient, callbackQuery, BuildOutcomeText(result, "🗑 نتیجه رها کردن عملیات"), null, cancellationToken);
        await AnswerSafelyAsync(botClient, callbackQuery, "نتیجه ثبت شد.", false, cancellationToken);
    }

    /// <summary>
    /// Renders one service result as administrator-facing Persian text.
    /// </summary>
    /// <param name="result">Structured result returned by the manual-review service.</param>
    /// <param name="title">Fixed Persian heading for the action that produced the result.</param>
    /// <returns>HTML text that states the durable outcome and never exposes account or panel identity.</returns>
    /// <remarks>
    /// The text reports the durable state after the action rather than the intent, so an administrator always sees what
    /// users.db actually records.
    /// </remarks>
    private static string BuildOutcomeText(
        XuiV3RenewalManualReviewService.ManualReviewActionResult result,
        string title)
    {
        var builder = new StringBuilder();
        builder.AppendLine("<b>").Append(Escape(title)).AppendLine("</b>");
        builder.AppendLine();
        builder.Append("عملیات: <code>#").Append(result.OperationId).Append("</code>").AppendLine();

        switch (result.Outcome)
        {
            case XuiV3RenewalManualReviewService.ManualReviewOutcome.ConfirmedApplied:
                builder.AppendLine("✅ تمدید به‌عنوان اعمال‌شده تایید شد و تسویه آن با موفقیت تکمیل شد.");
                break;
            case XuiV3RenewalManualReviewService.ManualReviewOutcome.ConfirmedAppliedSettlementPending:
                builder.AppendLine("⚠️ تایید اعمال‌شده ثبت شد، اما تسویه هنوز کامل نشده است. تا تکمیل تسویه، تمدید جدید این اکانت قفل می‌ماند.");
                break;
            case XuiV3RenewalManualReviewService.ManualReviewOutcome.Abandoned:
                builder.AppendLine("🗑 عملیات به‌عنوان «اعمال‌نشده» رها شد، وضعیت <code>failed</code> ثبت شد و قفل اکانت آزاد شد.");
                break;
            case XuiV3RenewalManualReviewService.ManualReviewOutcome.ReprobeCompared:
                builder.AppendLine("🔄 بررسی مجدد پنل انجام شد. هیچ وضعیتی تغییر نکرد.");
                break;
            case XuiV3RenewalManualReviewService.ManualReviewOutcome.NotFound:
                builder.AppendLine("❌ عملیاتی با این شناسه پیدا نشد.");
                break;
            case XuiV3RenewalManualReviewService.ManualReviewOutcome.NotUnderManualReview:
                builder.AppendLine("ℹ️ این عملیات دیگر در بررسی دستی نیست و نیازی به اقدام ندارد.");
                break;
            case XuiV3RenewalManualReviewService.ManualReviewOutcome.AlreadyResolved:
                builder.AppendLine("ℹ️ این عملیات پیش از این توسط مدیر دیگری تعیین وضعیت شده است. وضعیت ثبت‌شده معتبر است.");
                break;
            case XuiV3RenewalManualReviewService.ManualReviewOutcome.ComparisonNotApplied:
                builder.AppendLine("❌ بررسی مجدد پنل تایید نکرد که تمدید اعمال شده باشد. هیچ تغییری ثبت نشد.");
                break;
            case XuiV3RenewalManualReviewService.ManualReviewOutcome.ComparisonNotPreMutation:
                builder.AppendLine("❌ بررسی مجدد نشان نداد که اکانت دقیقاً در حالت قبل از تمدید باقی مانده است، بنابراین رها کردن انجام نشد.");
                break;
            case XuiV3RenewalManualReviewService.ManualReviewOutcome.SettlementArtifactExists:
                builder.AppendLine("⛔️ برای این عملیات یک سند مالی دائمی وجود دارد؛ رها کردن ممنوع است. این مورد باید مالی بررسی شود.");
                break;
            case XuiV3RenewalManualReviewService.ManualReviewOutcome.LegacyOverrideRequired:
                builder.AppendLine("⚠️ این عملیات قدیمی است و وضعیت پنل آن هرگز قابل اثبات خودکار نیست؛ رها کردن آن به تایید صریح مدیر ارشد نیاز دارد.");
                break;
            case XuiV3RenewalManualReviewService.ManualReviewOutcome.SettlementNotPending:
                builder.AppendLine("⛔️ وضعیت تسویه این عملیات <code>pending</code> نیست؛ ممکن است مبلغی کسر شده باشد، پس قفل آزاد نشد.");
                break;
            default:
                builder.AppendLine("نتیجه: <code>").Append(Escape(result.Outcome.ToString())).Append("</code>");
                break;
        }

        if (!string.IsNullOrWhiteSpace(result.Status))
        {
            builder.AppendLine();
            builder.Append("وضعیت فعلی: <code>").Append(Escape(result.Status)).Append("</code> / <code>")
                .Append(Escape(result.SettlementStatus ?? string.Empty)).Append("</code>");
        }

        if (!string.IsNullOrWhiteSpace(result.ComparisonOutcome))
        {
            builder.AppendLine();
            builder.Append("نتیجه مقایسه: <code>").Append(Escape(result.ComparisonOutcome)).Append("</code>");
        }

        if (!string.IsNullOrWhiteSpace(result.ComparisonSummary))
        {
            builder.AppendLine();
            builder.Append("جزئیات میدانی: <code>").Append(Escape(result.ComparisonSummary)).Append("</code>");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Replaces the administrator message with new text when the callback carries a message.
    /// </summary>
    /// <param name="botClient">Telegram client used for the edit.</param>
    /// <param name="callbackQuery">Callback whose message should be edited.</param>
    /// <param name="text">HTML text to display.</param>
    /// <param name="replyMarkup">Optional inline keyboard, or null to remove the buttons.</param>
    /// <param name="cancellationToken">Token that cancels the Telegram edit.</param>
    /// <returns>A task that completes after Telegram accepts the edit or reports it as unnecessary.</returns>
    /// <remarks>
    /// A "message is not modified" response is treated as success because it means Telegram already shows the intended
    /// state; it must never be surfaced as an error on a review screen.
    /// </remarks>
    private static async Task EditAsync(
        ITelegramBotClient botClient,
        CallbackQuery callbackQuery,
        string text,
        InlineKeyboardMarkup replyMarkup,
        CancellationToken cancellationToken)
    {
        if (callbackQuery?.Message == null)
            return;

        try
        {
            await botClient.EditMessageText(
                callbackQuery.Message.Chat.Id,
                callbackQuery.Message.MessageId,
                text,
                parseMode: ParseMode.Html,
                replyMarkup: replyMarkup,
                cancellationToken: cancellationToken);
        }
        catch (ApiRequestException ex) when (ex.Message?.Contains("message is not modified", StringComparison.OrdinalIgnoreCase) == true)
        {
            // Presentation-only duplicate; the administrator already sees the intended state.
        }
    }

    /// <summary>
    /// Answers a callback query without letting a stale Telegram query interrupt the panel work.
    /// </summary>
    /// <param name="botClient">Telegram client used to answer the query.</param>
    /// <param name="callbackQuery">Callback query to answer; null and empty ids are ignored.</param>
    /// <param name="text">Short toast or alert text.</param>
    /// <param name="showAlert">Whether Telegram should show an alert rather than a transient toast.</param>
    /// <param name="cancellationToken">Token that cancels the acknowledgement.</param>
    /// <returns>A task that completes after Telegram accepts or rejects the answer.</returns>
    /// <remarks>The bounded interaction timeout keeps a slow Telegram response from delaying the surrounding update.</remarks>
    private async Task AnswerSafelyAsync(
        ITelegramBotClient botClient,
        CallbackQuery callbackQuery,
        string text,
        bool showAlert,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(callbackQuery?.Id))
            return;

        await TelegramCallbackAnswerPolicy.TryAnswerAsync(
            botClient,
            callbackQuery.Id,
            text,
            showAlert,
            cancellationToken: cancellationToken,
            logger: _logger,
            botId: BotContextAccessor.CurrentBotId,
            telegramUserId: callbackQuery.From?.Id,
            timeout: _interactionTimeouts.CallbackAnswer);
    }

    /// <summary>
    /// Checks whether a Telegram user id belongs to the configured super-admin allow-list.
    /// </summary>
    /// <param name="telegramUserId">Numeric Telegram user id supplied by an incoming message or callback.</param>
    /// <returns><c>true</c> only when the id is configured as a super-admin.</returns>
    /// <remarks>Tenant ownership and colleague status confer no authority over another account's renewal lock.</remarks>
    private bool IsConfiguredSuperAdmin(long telegramUserId) =>
        telegramUserId > 0 && _appConfig.AdminsUserIds?.Contains(telegramUserId) == true;

    /// <summary>
    /// Formats one UTC timestamp for administrator display.
    /// </summary>
    /// <param name="value">UTC instant stored on the operation row.</param>
    /// <returns>A fixed <c>yyyy-MM-dd HH:mm</c> UTC string.</returns>
    private static string FormatUtc(DateTime value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    /// <summary>
    /// HTML-escapes one dynamic fragment before it is placed in an HTML-parsed Telegram message.
    /// </summary>
    /// <param name="value">Dynamic text that may contain characters Telegram interprets as markup.</param>
    /// <returns>An escaped string, or an empty string when the input is null.</returns>
    /// <remarks>
    /// The sanitized comparison summary and stored bot ids are produced by trusted code, but escaping keeps a malformed
    /// historical value from breaking the whole message and hiding an account lock from the administrator.
    /// </remarks>
    private static string Escape(string value) => WebUtility.HtmlEncode(value ?? string.Empty);
}
