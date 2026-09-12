using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations
{
    /// <summary>
    /// Adds <c>BotUserStates.PendingReceiptOrderDbId</c>, the durable per-bot, per-customer binding between a
    /// card-to-card receipt-upload request and the exact tenant order that request belongs to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the column exists.</b> A tenant card-to-card receipt used to be attached to whichever unfulfilled card order
    /// was newest when the image arrived. A customer who started an upload for one order could therefore have the receipt
    /// recorded against a different order opened in between, which corrupted both orders' review state. The target is now
    /// recorded at the moment the customer presses the receipt button and consumed when the image arrives.
    /// </para>
    /// <para>
    /// <b>Scope.</b> The value is scoped by the table's existing composite key of <c>BotId</c> plus <c>TelegramUserId</c>,
    /// so the same Telegram user can hold independent receipt targets in different storefront bots. It is nullable and
    /// performs no backfill: rows created before this migration simply fall back to the previous newest-eligible lookup
    /// for any upload that was already in flight.
    /// </para>
    /// <para>
    /// <b>Not an authorization grant.</b> The id is an intent pointer only. The receipt handler revalidates that the order
    /// still belongs to the same bot and customer, is still card-funded, is still unfulfilled, and is still in an eligible
    /// receipt state before anything is persisted, so a stale or forged value fails closed.
    /// </para>
    /// <para>
    /// No financial, order, receipt, or fulfillment column is touched and no existing value is rewritten.
    /// </para>
    /// </remarks>
    public partial class AddTenantReceiptUploadTarget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PendingReceiptOrderDbId",
                table: "BotUserStates",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PendingReceiptOrderDbId",
                table: "BotUserStates");
        }
    }
}
