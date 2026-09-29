using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations
{
    /// <summary>
    /// Reopens the falsely marked payment-audit slot for officially settled tenant AtlasPay orders.
    /// </summary>
    /// <remarks>
    /// Earlier tenant settlement marked <c>SuccessLoggedAtUtc</c> without ever emitting its separate customer-payment
    /// audit. Only linked, fulfilled orders with an already settled provider payment are selected. The normal audit
    /// worker emits the missing report later; this migration does not query AtlasPay, create an account, change a
    /// balance, or send Telegram traffic. The existing financial and account-delivery outcomes stay immutable.
    /// </remarks>
    [DbContext(typeof(UserDbContext))]
    [Migration("20260929120000_RequeueAtlasPayTenantPaymentAudits")]
    public partial class RequeueAtlasPayTenantPaymentAudits : Migration
    {
        /// <summary>Clears only the false audit marker for matching settled direct tenant orders.</summary>
        /// <param name="migrationBuilder">The users.db migration builder; never the credentials wallet database.</param>
        /// <remarks>
        /// The reverse order/payment foreign-key check excludes stray or mismatched provider rows. Payment amounts are
        /// already in toman and are not changed here. The background audit scan checks these links again before logging.
        /// </remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE "AtlasPayPaymentInfos" AS p
                SET "SuccessLoggedAtUtc" = NULL
                WHERE p."SuccessLoggedAtUtc" IS NOT NULL
                  AND p."PaymentPurpose" = 'tenant_order'
                  AND p."SettlementState" = 'settled'
                  AND p."IsAddedToBalance" = 1
                  AND p."PaidAtUtc" IS NOT NULL
                  AND p."ProviderStatus" IN ('confirmed', 'settled')
                  AND EXISTS (
                      SELECT 1 FROM "TenantBotOrders" AS o
                      WHERE o."Id" = p."TenantBotOrderId"
                        AND o."AtlasPayPaymentInfoId" = p."Id"
                        AND o."TenantBotId" = p."BotId"
                        AND o."CustomerTelegramUserId" = p."TelegramUserId"
                        AND o."OwnerTelegramUserId" = p."TenantOwnerTelegramUserId"
                        AND o."SalePriceToman" = p."BaseAmountToman"
                        AND o."PaymentProvider" = 'atlaspay'
                        AND o."IsFulfilled" = 1
                  );
                """);
        }

        /// <summary>Keeps already emitted audits immutable on downgrade.</summary>
        /// <param name="migrationBuilder">The users.db migration builder; no rollback mutation is safe.</param>
        /// <remarks>
        /// Restoring an old timestamp could hide an audit that was not sent yet, while clearing a new one could
        /// duplicate a delivered report. Rollback therefore leaves payment and audit state untouched.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
