using System;

namespace Adminbot.Domain
{
    /// <summary>
    /// Customer-safe reason why a new renewal for one panel account is currently blocked.
    /// </summary>
    /// <remarks>
    /// The category is derived only from durable operation state. It exists so every renewal entry point explains the
    /// same situation with the same wording instead of the previous single generic "automatic review" sentence.
    /// </remarks>
    public enum XuiV3RenewalBlockingNoticeKind
    {
        /// <summary>
        /// Automatic GET-only reconciliation is still running for a recovery-eligible operation.
        /// </summary>
        AutomaticReview,

        /// <summary>
        /// Automatic reconciliation stayed inconclusive and this operation now needs support review.
        /// </summary>
        ManualReviewRequired,

        /// <summary>
        /// The previous renewal is already applied on the panel and only its settlement is still completing.
        /// </summary>
        AppliedSettlementInProgress,

        /// <summary>
        /// A historical operation recorded before the recovery protocol can never be re-proven automatically, so it
        /// needs support review.
        /// </summary>
        HistoricalManualReview
    }

    /// <summary>
    /// Ready-to-send customer notice describing one blocking renewal operation.
    /// </summary>
    /// <remarks>
    /// Both texts are fixed Persian sentences selected by state. They deliberately contain no operation id, no UUID, no
    /// account email, no panel URL, no token, and no internal error text, so a notice can never leak panel or customer
    /// identity.
    /// </remarks>
    public sealed class XuiV3RenewalBlockingNotice
    {
        /// <summary>Durable state category that produced this notice.</summary>
        public XuiV3RenewalBlockingNoticeKind Kind { get; init; }

        /// <summary>
        /// Full sentence sent to the customer inside the renewal conversation.
        /// </summary>
        public string Text { get; init; }

        /// <summary>
        /// Short single clause used where a compact reason is needed, such as a tenant order error message.
        /// </summary>
        public string ShortText { get; init; }
    }

    /// <summary>
    /// Maps a blocking <see cref="XuiV3RenewalOperation"/> to the exact customer-facing explanation for its state.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists.</b> <c>FindBlockingOperationAsync</c> blocks a new renewal for any pending, processing,
    /// ambiguous, manual-review, or applied-but-unsettled row. Previously every one of those states produced the same
    /// "under automatic review" sentence, which was wrong for two of them: a <c>manual_review</c> row is no longer
    /// being reviewed automatically, and an <c>applied</c> row has already succeeded and is only finishing settlement.
    /// Customers were told to wait for an automatic process that would never resolve their lock.
    /// </para>
    /// <para>
    /// <b>Resolution order.</b> Manual review wins over everything because it is the state that needs a human.
    /// An applied-but-unsettled row is reported as applied. Any other recovery-ineligible row is historical, because
    /// the pre-mutation evidence needed for automatic reconciliation was never recorded for it. Everything else is
    /// recovery-eligible and still under automatic review.
    /// </para>
    /// </remarks>
    public static class XuiV3RenewalBlockingNoticeBuilder
    {
        /// <summary>
        /// Builds the customer-safe notice for the operation currently blocking a renewal.
        /// </summary>
        /// <param name="operation">
        /// Detached blocking operation returned by <c>FindBlockingOperationAsync</c>, or null when the caller only knows
        /// that some lock exists. Only its status, settlement status, and recovery eligibility are read; identity
        /// columns are never used.
        /// </param>
        /// <returns>
        /// A notice whose <see cref="XuiV3RenewalBlockingNotice.Kind"/> matches the durable state and whose texts are
        /// safe to send to the customer verbatim.
        /// </returns>
        /// <remarks>
        /// A null operation is treated as automatic review, which is the conservative superset: it still tells the
        /// customer the renewal is locked while it is being verified and never claims a human is involved.
        /// </remarks>
        /// <example>
        /// <code>
        /// var blocking = await store.FindBlockingOperationAsync(client.Uuid, client.Email, cancellationToken: token);
        /// if (blocking != null)
        /// {
        ///     var notice = XuiV3RenewalBlockingNoticeBuilder.Build(blocking);
        ///     await botClient.SendMessage(chatId, notice.Text, cancellationToken: token);
        /// }
        /// </code>
        /// </example>
        public static XuiV3RenewalBlockingNotice Build(XuiV3RenewalOperation operation)
        {
            if (operation == null)
                return AutomaticReview();

            if (string.Equals(operation.Status, XuiV3RenewalOperationStatuses.ManualReview, StringComparison.Ordinal))
            {
                // A recovery-ineligible row can never be re-proven, so the customer is told the truth that a historical
                // operation needs support, not that an automatic process is still working on it.
                return operation.RecoveryEligible ? ManualReviewRequired() : HistoricalManualReview();
            }

            if (string.Equals(operation.Status, XuiV3RenewalOperationStatuses.Applied, StringComparison.Ordinal))
                return AppliedSettlementInProgress();

            return operation.RecoveryEligible ? AutomaticReview() : HistoricalManualReview();
        }

        /// <summary>Creates the automatic-review notice for a recovery-eligible operation.</summary>
        /// <returns>A notice stating that automatic verification is in progress.</returns>
        private static XuiV3RenewalBlockingNotice AutomaticReview() => new()
        {
            Kind = XuiV3RenewalBlockingNoticeKind.AutomaticReview,
            Text = "یک تمدید قبلی برای این اکانت هنوز در حال بررسی خودکار است. برای جلوگیری از تمدید تکراری، تمدید جدید این اکانت موقتاً قفل شده است.",
            ShortText = "تمدید قبلی این اکانت در حال بررسی خودکار است و تمدید جدید موقتاً قفل شده است."
        };

        /// <summary>Creates the support-review notice for a recovery-eligible manual-review operation.</summary>
        /// <returns>A notice that asks the customer to contact support.</returns>
        private static XuiV3RenewalBlockingNotice ManualReviewRequired() => new()
        {
            Kind = XuiV3RenewalBlockingNoticeKind.ManualReviewRequired,
            Text = "نتیجه تمدید قبلی این اکانت به‌صورت خودکار قطعی نشد و برای بررسی پشتیبانی ثبت شده است. تمدید جدید تا تعیین نتیجه توسط پشتیبانی قفل می‌ماند. لطفاً با پشتیبانی تماس بگیرید.",
            ShortText = "نتیجه تمدید قبلی این اکانت قطعی نشد و منتظر بررسی پشتیبانی است."
        };

        /// <summary>Creates the applied-but-unsettled notice.</summary>
        /// <returns>A notice stating that the previous renewal succeeded and settlement is finishing.</returns>
        private static XuiV3RenewalBlockingNotice AppliedSettlementInProgress() => new()
        {
            Kind = XuiV3RenewalBlockingNoticeKind.AppliedSettlementInProgress,
            Text = "تمدید قبلی این اکانت در پنل اعمال شده است و تسویه آن در حال تکمیل است. تمدید جدید تا پایان تسویه موقتاً قفل می‌ماند.",
            ShortText = "تمدید قبلی این اکانت اعمال شده و تسویه آن در حال تکمیل است."
        };

        /// <summary>Creates the historical-operation notice for a recovery-ineligible blocking row.</summary>
        /// <returns>A notice that asks the customer to contact support.</returns>
        private static XuiV3RenewalBlockingNotice HistoricalManualReview() => new()
        {
            Kind = XuiV3RenewalBlockingNoticeKind.HistoricalManualReview,
            Text = "یک عملیات تمدید قدیمی برای این اکانت باز مانده است و به‌صورت خودکار قابل تعیین وضعیت نیست. تمدید جدید تا بررسی آن توسط پشتیبانی قفل می‌ماند. لطفاً با پشتیبانی تماس بگیرید.",
            ShortText = "یک عملیات تمدید قدیمی این اکانت باز مانده و نیاز به بررسی پشتیبانی دارد."
        };
    }
}
