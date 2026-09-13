# Telegram premium UI infrastructure (Phase 1)

Phase 1 builds the foundation for Telegram custom emoji, semantic button colours and safe fallback. It deliberately
does **not** migrate existing menus: the only new user-visible surface is the storefront owner's premium-appearance
setting, and the single new message Telegram ever receives is the capability preview.

## 1. What this phase ships

| Component | File | Purpose |
| --- | --- | --- |
| Emoji catalog asset | `Assets/telegram-ui/emoji-map.json` | The only source of logical emoji keys, fallbacks and custom-emoji identifiers. |
| Catalog port + keys | `Domain/TelegramUi/TelegramUiEmoji.cs` | `ITelegramUiEmojiCatalog`, `TelegramUiEmoji`, `TelegramUiEmojiKeys`. |
| Catalog loader | `Domain/TelegramUi/TelegramUiEmojiCatalog.cs` | Strict validating loader; fails application startup on a bad asset. |
| Button factory | `Domain/TelegramUi/TelegramUiButtonFactory.cs` | `TelegramUiButtonTone` plus inline/reply/URL button creation. |
| Text builder | `Domain/TelegramUi/TelegramUiTextBuilder.cs` | `TelegramUiText` plus explicit `CustomEmoji` entities with UTF-16 offsets. |
| Mode resolver | `Domain/TelegramUi/TelegramUiModeResolver.cs` | Answers whether premium visuals are requested and currently active. |
| Runtime circuit | `Domain/TelegramUi/TelegramPremiumUiRuntimeState.cs` | Ephemeral per-bot capability state plus the durable storefront auto-disable. |
| Capability probe | `Domain/TelegramUi/TelegramPremiumUiCapabilityProbe.cs` | Proves capability through the exact storefront bot token. |
| Fallback executor | `Domain/TelegramUi/TelegramPremiumUiFallbackExecutor.cs` | One premium attempt, at most one classic fallback. |

## 2. Architecture

```text
configuration.json  ->  AppConfig.OwnedBotPremiumUiEnabled   (owned bots, global)
users.db            ->  BotInstance.TenantPremiumUiEnabled   (storefronts, per store)
                                     |
                                     v
                        ITelegramUiModeResolver
                                     |
              requested -----------+----------- capable
                                     |                |
                        ITelegramPremiumUiRuntimeState
                                     |
                        TelegramUiButtonFactory / TelegramUiTextBuilder
                                     |
                        TelegramPremiumUiFallbackExecutor
                                     |
                        ForegroundBoundedTelegramBotClient
```

`OwnedBotPremiumUiEnabled` is startup-bound: change `Data/configuration.json` and restart the service. Nothing writes
it back at runtime.

## 3. Asset format

```json
{
  "version": 1,
  "items": {
    "premium_probe": { "fallback": "✨", "customEmojiId": null },
    "home":          { "fallback": "🏠", "customEmojiId": null }
  }
}
```

Rules enforced by the loader (a violation stops startup):

* `version` must be `1`;
* `items` must exist and contain every key declared in `TelegramUiEmojiKeys.All`;
* logical keys are unique, trimmed, `lower_snake_case`, and start with a letter;
* `fallback` is required, non-empty, printable, and must not contain control characters;
* `customEmojiId` is optional or `null`; when present it must be a **canonical positive decimal string** (`"0"`,
  `"-1"`, `"+1"`, `"00123"`, `"12abc"`, and surrounding whitespace are all rejected). Validation uses `BigInteger`, so
  identifiers are never limited to `int`;
* duplicate JSON members inside `items` are rejected explicitly — `JsonDocument` preserves them, and a plain dictionary
  deserialization would silently keep only the last value.

`Assets/telegram-ui/**` is copied to both the build output and the publish artifact; the runtime resolves it from
`AppContext.BaseDirectory`, never from the process working directory.

## 4. Adding real custom emoji identifiers later

1. Obtain the identifier from an authoritative Telegram source. `getCustomEmojiStickers` is the documented lookup once
   you already know an identifier; it is not a discovery mechanism.
2. Put the canonical decimal string into the matching `customEmojiId` in `Assets/telegram-ui/emoji-map.json`.
3. Keep the `fallback` unchanged: it remains the visible placeholder and the classic rendering.
4. Release and restart. The catalog is loaded and frozen once at startup.

**Identifiers must never be invented, scraped, or learned from arbitrary user messages.** A wrong identifier either
fails the whole send or renders nothing, and Telegram treats the value as opaque. This phase ships **zero** curated
identifiers, and a regression test asserts that the production asset contains none.

## 5. Enabling premium visuals

### Owned bots

Set `"ownedBotPremiumUiEnabled": true` in `Data/configuration.json` and restart. There is no Telegram toggle and no
database column for owned bots in this phase. If Telegram definitively rejects a decorated payload, the ephemeral
runtime circuit marks that bot rejected until the next restart; the configuration file is never edited automatically.

### Storefronts

The storefront owner opens the exact store panel and presses `✨ فعال‌سازی ظاهر پریمیوم`. The sequence is:

```text
authorize the addressed store and its persisted owner
-> reload the exact storefront row
-> require CallbackQuery.From.IsPremium == true
-> resolve the catalog's premium_probe identifier
-> prove capability over the storefront's OWN bot token
-> re-read the storefront and re-validate the panel revision
-> persist TenantPremiumUiEnabled = true
-> refresh the runtime registry
-> render the fresh panel
```

Every step fails closed. A Premium owner account alone is **not** sufficient: Telegram's BotFather ownership state is
only authoritative through the storefront bot's own token, so a real probe is always required. The probe runs between
two independent database reads, so no SQLite transaction is open while Telegram is contacted.

Disabling (`🚫 غیرفعال‌سازی ظاهر پریمیوم`) requires no Telegram Premium status, no probe, and no configured catalog
identifier, so a storefront can always return to the classic appearance.

### What the probe actually proves

The probe sends one inert preview to the owner's private chat through the storefront bot: the text
`✨ پیش‌نمایش ظاهر پریمیوم` and one button labelled `ظاهر پریمیوم` carrying `IconCustomEmojiId` and
`Style = Primary`, with the callback payload `PUI:preview`. That payload is answered by the dispatcher before any
business handler, so pressing the preview can never change state.

Style alone proves nothing: button colour is an ordinary Bot API field and does **not** require Telegram Premium. The
capability under test is `IconCustomEmojiId`, which is why both the decorated and the baseline preview use the same
style and differ only in the custom emoji identifier.

## 6. Safe failure semantics

| Outcome | Meaning | Action |
| --- | --- | --- |
| Telegram `400` on the decorated send | Definitive: the decorated request was not accepted. | Exactly one plain baseline preview is sent. Baseline success ⇒ `Rejected`; baseline failure ⇒ `BaselineRejected`. |
| Timeout, caller-independent cancellation, connection reset, `HttpRequestException`, `RequestException`, `IOException`, 429, 5xx, 403 | Ambiguous or transient. The request may already have reached Telegram. | **No** baseline send, no retry, no state change. Reported as `Ambiguous` or `TransientFailure`. |
| Caller cancellation | The operator or shutdown stopped the work. | Propagated unchanged; no capability state is recorded. |
| No curated identifier | The catalog is not ready. | `CatalogUnavailable`; nothing is sent. |
| Owner not Premium | The precondition is absent. | `PremiumRequired`; nothing is sent. |

`TelegramPremiumUiFailureClassification` keeps the definitive and ambiguous predicates disjoint, so a caller reaches
the same decision whichever order it checks them in. A local foreground budget expiry
(`TelegramForegroundDeliveryTimeoutException`) is always ambiguous: the existing foreground delivery policy is reused,
never replaced, and a timed-out decorated send is never followed by a classic copy because that would duplicate a
message.

## 7. Premium expiration

A storefront owner may enable while subscribed and later lose Telegram Premium, so the stored flag can become stale.
`ITelegramPremiumUiRuntimeState.MarkTenantCapabilityRejectedAsync` exists for that case: after a definitive decorated
rejection where a classic fallback succeeded, it reloads the exact storefront, clears the persisted preference,
refreshes the runtime registry, and is idempotent.

It is deliberately **not** wired into every customer send in this phase, and ambiguous failures never call it — a
network problem is not proof that a subscription expired.

## 8. Button colours

Telegram's `primary`/`success`/`danger` button styles are plain Bot API fields and are available without Telegram
Premium. This project chooses to group colours with custom emoji under one "premium visual mode" for a consistent
identity; that is a product decision, not a Telegram restriction. In classic mode the factory never sets a style, so
adopting the factory cannot change an existing keyboard.

## 9. Phase 2 migration

Existing menus are untouched. Migrate incrementally, for example:

```csharp
// before
InlineKeyboardButton.WithCallbackData("💳 خرید", callback);

// after
_uiButtons.Callback(
    label: "خرید",
    callbackData: callback,
    emojiKey: TelegramUiEmojiKeys.Card,
    tone: TelegramUiButtonTone.Primary,
    premiumMode: _uiMode.IsPremiumVisualsActive(BotContextAccessor.CurrentBotId));
```

Guidelines:

* Prefer `ITelegramUiModeResolver` over a static flag: mode depends on the bot executing the update, so one storefront
  can be premium while another is classic.
* Build message text with `TelegramUiTextBuilder` when the message should carry custom emoji, and pass
  `TelegramUiText.Entities` explicitly. Do not use a parse mode to create custom emoji.
* Use `TelegramPremiumUiFallbackExecutor` rather than hand-rolling a premium attempt plus fallback.
* Reference `TelegramUiEmojiKeys` constants instead of raw strings.

## 10. Verifying a change

```bash
dotnet build Adminbot.sln -c Release --no-restore
dotnet test Adminbot.sln -c Release --no-build
dotnet test Adminbot.Tests/Adminbot.Tests.csproj -c Release --no-build \
  --filter "FullyQualifiedName~PremiumUi|FullyQualifiedName~EmojiCatalog"
dotnet ef migrations has-pending-model-changes --context UserDbContext
dotnet ef migrations has-pending-model-changes --context CredentialsDbContext
dotnet publish Adminbot.csproj -c Release -f net10.0 -r linux-x64 --self-contained false
```

After publishing, confirm `Assets/telegram-ui/emoji-map.json` exists in the publish directory.
