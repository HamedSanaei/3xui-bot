# Telegram.Bot v22 compatibility and patch updates

Pinned the explicitly requested Telegram.Bot version from 19.0.0 to 22.10.3. No business-policy, database-schema,
receiver scheduling or payment changes are intended.

## Stable patch follow-up (2026-10-01)

The current package pin is **22.10.3.2**, the latest stable version in the
[official NuGet index](https://api.nuget.org/v3-flatcontainer/telegram.bot/index.json).
The [upstream release](https://github.com/TelegramBots/Telegram.Bot/releases/tag/v22.10.3.2)
and [22.10.3-to-22.10.3.2 comparison](https://github.com/TelegramBots/Telegram.Bot/compare/v22.10.3...v22.10.3.2)
include reply-markup serialization fixes, rich-message/media improvements and 64-bit draft identifiers.
No application call-site migration was needed for this patch; application-owned retry and foreground budgets remain unchanged.

An isolated runtime smoke used the actual SDK with controlled Bot API HTTP responses and temporary SQLite databases.
Repeated customer code entry preserved the original quote/message and net price on an identical-edit confirmation;
an unbound quote recovered its binding; a genuine missing target still created a replacement and expired the old buttons.
Both owner help examples were entered through the real callbacks/text parser and saved with their exact fields.
The changed discount/Telegram regression selection passed 55 cases. No live Telegram/provider call or production-data mutation was used;
Telegram-client visual rendering was not exercised.

Windows test execution was blocked by Defender quarantining the application assembly.
Runtime verification used an isolated Linux source copy with SDK 10.0.302 and full ICU globalization instead;
no Windows security settings or exclusions were changed.

Final Linux verification: the complete existing Release suite passed **1051 tests, 0 failures** with the actual source
commit supplied through `SourceRevisionId`. The earlier source-copy run lacked Git metadata and used Debug configuration;
its provenance check failed before that verification prerequisite was corrected. Twelve existing test-fixture warnings remain.
Standalone production `Adminbot.csproj` publish succeeded with `-c Release -f net10.0 -r linux-x64 --self-contained false`;
no auxiliary test project is part of that publish dependency graph.
The published application also completed `--migration-check`: both `UserDbContext` and `CredentialsDbContext` reported `OK`.

## Compatibility fixes

- Renamed Telegram methods to the v22 API (Async suffix removal; SendTextMessageAsync to SendMessage,
  MakeRequestAsync to SendRequest). Updated XML references alongside call sites.
- Replaced IReplyMarkup with ReplyMarkup. Adapted both client decorators and existing fixture clients to non-nullable
  BotId and the additional TGFile download overload. Updated writable fixture Message.MessageId initializers to Id.
- Adapted request constructors to initializers; mapped preview settings to LinkPreviewOptions and reply settings to
  ReplyParameters. Preserved foreground timeout interception and owner-store message prefixes.
- Updated named cancellation arguments after signature reordering, ReceiveAsync's errorHandler name, and nullable
  parse modes/alert flags to the same None/false defaults.
- Disabled new SDK automatic rate-limit retries at runtime client creation (RetryCount=0); existing retry ownership stays
  with the application. Polling remains explicitly started and controlled by the existing receiver lifecycle.
- Used System.Text.Json with Telegram's JsonBotAPI.Options for durable updates. v22 no longer uses the Newtonsoft
  attributes relied upon by the previous inbox serializer. Existing Bot API snake-case payloads retain their wire format;
  no payload or schema migration is introduced. Compatibility was reviewed statically, not tested at runtime.

## Initial upgrade verification

`dotnet restore Adminbot.sln` succeeded.
`dotnet build Adminbot.sln -c Release --no-restore -v q` succeeded: zero errors, six warnings in existing test fixtures
(nullable annotations and xUnit analyzers). Existing fixtures were changed only where needed for API/compile compatibility.
No tests were added or run. No commit, push, publish or deployment was performed.
Diff whitespace and strict UTF-8 review completed; non-ASCII source sequences match HEAD, preserving Persian/emoji.
Existing corrupted fixture strings in `Adminbot.Tests/GatewayAdminCallbackTests.cs` and question-mark-only messages
in `Services/TenantBotService.cs` were already present in HEAD and remain untouched; repairing them is outside this upgrade.

## Changed files

Follow-up regression: `Adminbot.Tests/TelegramUpdateLegacyJsonTests.cs` contains one Fact using JSON generated from
Telegram.Bot 19.0.0.0 with Newtonsoft.Json.JsonConvert.SerializeObject. It inserts the fixed payload directly into
temporary SQLite, disposes the writer, and claims it through a fresh v22 TelegramUpdateInboxStore. Assertions cover
bot/user identity, 64-bit Telegram ids, callback data, message id, Unix timestamp, enums, entities, inline keyboard,
Persian/emoji and the durable queued-to-running transition with duplicate claim rejection.

Only `ConcurrencyTests.Inbox_claim_deserializes_persisted_TelegramBot_v19_update_json` was executed using an exact
FullyQualifiedName filter: 1 passed, 0 failed, 0 skipped. No production correction was needed. The full suite was not run.
The following inventory describes the initial upgrade; this follow-up adds the named regression file and updates this
report and CODE_MAP.md only.

- `Adminbot.Tests/AtlasPayTests.cs`
- `Adminbot.Tests/BackupRecoveryTests.cs`
- `Adminbot.Tests/ClientDownloadTests.cs`
- `Adminbot.Tests/GatewayAdminCallbackTests.cs`
- `Adminbot.Tests/MultiStoreTests.cs`
- `Adminbot.Tests/TelegramForegroundLatencyFollowUpTests.cs`
- `Adminbot.Tests/TelegramForegroundLatencyTests.cs`
- `Adminbot.Tests/TelegramLaneOwnerRoutingTests.cs`
- `Adminbot.Tests/TelegramLogNoiseRoutingTests.cs`
- `Adminbot.Tests/TenantCardReceiptIngestionTests.cs`
- `Adminbot.Tests/TenantTutorialTests.cs`
- `Adminbot.csproj`
- `CODE_MAP.md`
- `Domain/Logging/TelegramLogDispatcher.cs`
- `Domain/PaymentProviderAmountPolicy.cs`
- `Services/BotRuntimeServices.cs`
- `Services/BroadcastManager.cs`
- `Services/ClientDownloadFlow.cs`
- `Services/ForegroundBoundedTelegramBotClient.cs`
- `Services/OwnedBotNotificationService.cs`
- `Services/PaymentSettlementNotificationWorker.cs`
- `Services/ReferralService.cs`
- `Services/SalesAssistantService.cs`
- `Services/TelegramBotService.ClientDownload.cs`
- `Services/TelegramBotService.cs`
- `Services/TelegramCallbackAnswerPolicy.cs`
- `Services/TelegramInboxAdminService.cs`
- `Services/TelegramPhoneVerification.cs`
- `Services/TelegramUpdateInboxStore.cs`
- `Services/TenantBotService.ClientDownload.cs`
- `Services/TenantBotService.cs`
- `Services/TenantOrderNotificationWorker.cs`
- `Services/TenantOwnerPanelClient.cs`
- `Services/TenantStorefrontFundingAlertService.cs`
- `Services/TenantTutorialAlbumSender.cs`
- `Services/UsageReportChartRenderer.cs`
- `Services/WeeklyUsageReportHostedService.cs`
- `Services/XuiV3AccountExpiryReminderService.cs`
- `Services/XuiV3AdminFlowService.cs`
- `Services/XuiV3BotFlowService.cs`
- `Services/XuiV3PurchaseService.cs`
- `Services/XuiV3UserSafeError.cs`
- `Services/XuiV3VolumeExpirationReminderService.cs`
- `Utils/TelegramBotClientExtensions.cs`
- `docs/telegram-bot-22-upgrade.md`
