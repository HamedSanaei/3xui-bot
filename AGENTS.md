# AGENTS.md

## Primary rule: write IntelliSense-visible documentation

This repository requires documentation that appears when developers hover over or call a method in the IDE.
For C# code, this means **XML documentation comments** written directly above the type or member using triple-slash comments:

```csharp
/// <summary>
/// ...
/// </summary>
```

Normal implementation comments such as `// Save user state` are not enough for methods, classes, services, models, or public/internal APIs. Inline comments may be added for complex logic, but they do not replace XML documentation comments.

Treat missing XML documentation on any new or materially changed C# member as an incomplete implementation.

---

## Critical source-text safety rule: preserve UTF-8 Persian and emoji text

This repository contains Persian/RTL Telegram messages, payment texts, reply keyboards, inline buttons, admin-panel labels, Sales Assistant messages, and emoji. These strings are production UI. Codex MUST treat every non-ASCII source string as user-visible behavior, not as disposable formatting.

Encoding corruption is a release blocker. A build that succeeds after Persian text has become mojibake, replacement characters, missing emoji, or wrong casing is still a failed implementation.

Forbidden operations:

- Do NOT run bulk "encoding repair", mojibake repair, transliteration, text-normalization, Unicode-normalization, or search/replace scripts across `.cs`, `.cshtml`, `.razor`, `.resx`, `.json`, `.md`, `.sql`, or migration files unless the user explicitly asked for that exact repair and the diff scope has been reviewed first.
- Do NOT use PowerShell `Get-Content | Set-Content`, `Out-File`, `>` redirection, `-replace`, or hashtable-based replacement maps to rewrite source files that contain Persian/RTL text or emoji. PowerShell defaults can change encodings, and many PowerShell comparisons/replacements are case-insensitive unless explicitly made case-sensitive.
- Do NOT use default PowerShell hashtables (`@{}`) for repair maps where key casing matters. They are not safe for case-sensitive source transformations. If a map is unavoidable, use a case-sensitive, ordinal comparer such as a .NET dictionary with `StringComparer.Ordinal`, and still review the exact diff before continuing.
- Do NOT run `.ToUpper()`, `.ToLower()`, `.ToUpperInvariant()`, `.ToLowerInvariant()`, culture conversion, casing cleanup, or regex cleanup over a full source file or repository.
- Do NOT "fix" mojibake by guessing replacement text. Restore the clean content from git, history, backups, or user-provided source, then make the smallest intended edit.

Required handling when editing files with Persian/RTL text or emoji:

- Read and write the file as UTF-8 explicitly. Preserve existing line endings when possible.
- Keep edits surgical: change only the intended lines and avoid whole-file rewrites.
- Before saving, inspect nearby Persian strings and confirm they are readable in the editor/terminal.
- After saving, run `git diff --word-diff` or an equivalent diff and verify the exact Persian strings, emoji, and casing.
- Search touched files for obvious mojibake markers such as `Ø`, `Ù`, `Û`, `Ã`, `Â`, `�`, and unexpected `????`. Any match in user-visible text must be investigated before continuing.
- If any non-ASCII corruption appears, stop immediately and restore the affected file from source control before making a new attempt.

For Telegram customer-facing text, reply keyboards, inline buttons, payment receipt flows, card-to-card messages, Sales Assistant messages, support messages, and admin/partner panels, verify the final labels exactly when they exist in the touched file. Examples include `💳 خرید اکانت`, `📋 تعرفه‌ها`, `اکانت‌های من`, `تمدید اکانت`, `جستجوی اکانت`, `راهنما نصب`, and `💬 پشتیبانی`.

`dotnet build` is not an encoding-safety check. The project can compile while Persian UI text is corrupted. Build success is required, but Codex must also verify the human-readable text diff for every touched file that contains Persian/RTL text or emoji.

---

## Required C# XML documentation coverage

Codex MUST add or update XML documentation comments for every new or materially changed:

- Class, record, struct, interface, and enum.
- Method, constructor, extension method, handler, controller action, service method, repository method, store method, background job method, and helper method.
- Property and field when the meaning is not obvious or the value is used across layers.
- DTO, request model, response model, entity model, configuration model, options class, migration model, and view model.
- Enum value when the value affects business behavior.

This applies to public, protected, internal, and non-trivial private members.

If Codex edits a method signature, parameter, return type, side effect, business rule, or exception behavior, it MUST update the XML docs in the same patch.

---

## Method documentation requirements

Every new or materially changed method MUST include:

- `<summary>` explaining what the method does and why it exists.
- `<param>` for every parameter.
- `<returns>` when the method returns a value, including `Task<T>`, `bool`, nullable values, collections, and result objects.
- `<remarks>` for usage guidance, side effects, tenant rules, state-machine rules, payment rules, transaction rules, or external API behavior.
- `<exception>` for expected exceptions that callers may need to handle.
- `<example>` for reusable methods, service APIs, helpers, extension methods, and methods whose usage is not immediately obvious.

The documentation must be useful at the call site. A developer reading IntelliSense should understand what values to pass, what comes back, and what side effects may happen.

---

## Required method template

Use this structure for new methods unless the surrounding file already has a stronger local convention:

```csharp
/// <summary>
/// Gets or creates the per-tenant Telegram conversation state for a user.
/// </summary>
/// <param name="tenantBotId">
/// The internal database identifier of the tenant bot that owns the conversation state.
/// Pass the current TenantBot.Id from the resolved bot runtime context. Do not pass a
/// Telegram bot id, Telegram chat id, or Telegram user id here.
/// </param>
/// <param name="telegramUserId">
/// The numeric Telegram user id of the customer or admin whose state is being read.
/// This value must come from the Telegram update sender, not from a username or display name.
/// </param>
/// <param name="cancellationToken">
/// A token used to cancel the database operation when the update handler is stopped or the request times out.
/// </param>
/// <returns>
/// The existing state row for the specified tenant and Telegram user, or a newly initialized state object
/// when no state exists yet. The returned state is tenant-scoped and must not be reused for another bot.
/// </returns>
/// <remarks>
/// Use this method after the active tenant bot has been resolved for the incoming Telegram update.
/// The composite key of <paramref name="tenantBotId" /> and <paramref name="telegramUserId" /> prevents
/// the same Telegram user from sharing temporary purchase state across multiple bots.
///
/// Side effects:
/// This method may create and persist a new state row when the user has no existing state.
///
/// Concurrency:
/// Callers that modify the returned state should save it immediately after applying the intended transition.
/// </remarks>
/// <example>
/// <code>
/// var state = await userStateStore.GetOrCreateAsync(
///     context.TenantBotId,
///     update.Message.From.Id,
///     cancellationToken);
///
/// state.LastStep = BotStep.SelectPaymentMethod;
/// await userStateStore.SaveAsync(state, cancellationToken);
/// </code>
/// </example>
public Task<UserBotState> GetOrCreateAsync(
    long tenantBotId,
    long telegramUserId,
    CancellationToken cancellationToken)
{
    ...
}
```

Do not copy this template blindly. Replace every description with the exact behavior of the real method.

---

## Parameter documentation rules

Each `<param>` tag MUST explain:

- What the parameter represents.
- Which layer or system the value comes from.
- Whether the value is required, optional, nullable, or allowed to be empty.
- Which identifier type it is: internal database id, tenant id, Telegram user id, Telegram chat id, Telegram bot id, x-ui inbound id, payment provider id, or external invoice id.
- Whether the value is global or tenant-scoped.
- Expected units for numeric values, such as seconds, minutes, days, GB, bytes, IRT, USDT, percent, retry count, or account count.
- Validation requirements, including minimum and maximum values.
- Security requirements, such as whether the value must be encrypted, normalized, sanitized, masked, or never logged.

Bad:

```csharp
/// <param name="amount">Amount.</param>
```

Good:

```csharp
/// <param name="amount">
/// The wallet debit amount in Iranian toman, stored as a major currency unit.
/// Must be greater than zero and must not exceed the user's available wallet balance.
/// </param>
```

---

## Return value documentation rules

Each `<returns>` tag MUST explain:

- What the returned value represents.
- Whether it may be `null`.
- Whether a collection can be empty.
- Whether a boolean means success, eligibility, existence, validation, or another exact condition.
- Whether the returned entity is tracked by EF Core or detached.
- Whether the caller must save, send, retry, log, dispose, or further validate the result.
- Whether the returned data is safe to expose to Telegram users, admins, tenant owners, or logs.

Bad:

```csharp
/// <returns>True if successful.</returns>
```

Good:

```csharp
/// <returns>
/// `true` when the user's wallet balance was high enough and the debit was persisted;
/// `false` when the balance was insufficient and no database changes were saved.
/// </returns>
```

---

## Remarks and usage guidance

Use `<remarks>` whenever the method has important behavior that is not obvious from the signature.

The `<remarks>` section should document, when relevant:

- The normal caller of the method.
- Required preconditions before calling the method.
- State transitions caused by the method.
- Database writes and transaction boundaries.
- Telegram messages sent by the method.
- External API calls made by the method.
- Payment, wallet, ledger, or partner-balance effects.
- Tenant isolation and multi-bot behavior.
- Idempotency rules and duplicate callback behavior.
- Retry behavior and timeout behavior.
- Security rules and access-control assumptions.

Example:

```csharp
/// <remarks>
/// Called by the payment IPN handler after the provider signature has been verified.
/// This method is idempotent: if the payment intent is already marked as delivered,
/// the method returns without creating another x-ui account or another ledger entry.
/// </remarks>
```

---

## Examples for call-site clarity

For reusable methods, service methods, helpers, and extension methods, include an `<example>` block when it helps a future developer call the method correctly.

The example should show realistic values and the expected calling pattern. Do not include production secrets, real bot tokens, real payment credentials, private keys, or real wallet addresses.

Example:

```csharp
/// <example>
/// <code>
/// var invoice = await paymentIntentService.CreateDirectPurchaseInvoiceAsync(
///     tenantBotId: context.TenantBotId,
///     customerTelegramUserId: update.Message.From.Id,
///     planKey: selectedPlan.Key,
///     cancellationToken: cancellationToken);
///
/// await botClient.SendTextMessageAsync(
///     chatId: update.Message.Chat.Id,
///     text: invoice.PaymentUrl,
///     cancellationToken: cancellationToken);
/// </code>
/// </example>
```

---

## Inline comments are still required for complex logic

XML documentation explains how to use a member from the outside. Inline comments explain non-obvious implementation details inside the member.

Add inline comments for:

- Complex state-machine transitions.
- Multi-tenant or multi-bot routing logic.
- Composite keys such as `TenantBotId + TelegramUserId`.
- Payment confirmation, IPN/webhook processing, invoice creation, wallet debit/credit, partner profit, ledger entries, and withdrawals.
- Idempotency checks that prevent duplicate account delivery or duplicate balance changes.
- Concurrency protections, locks, retries, transaction scopes, and isolation assumptions.
- External calls to Telegram, payment gateways, x-ui panels, crypto services, or other systems.
- Security-sensitive behavior such as bot token handling, secret encryption, forced-join checks, admin checks, and webhook signature validation.

Do not add noisy comments that merely repeat syntax.

Bad:

```csharp
// Increment counter.
counter++;
```

Good:

```csharp
// Increment before the next gateway request so the final audit log shows the exact
// number of external attempts made for this invoice.
counter++;
```

---

## Tenant and bot-state documentation requirements

When adding or changing tenant-aware code, XML docs and inline comments MUST explain:

- Which tenant or bot owns the data.
- Whether the data is global or isolated per tenant.
- Why the code uses `TenantBotId`, `TelegramUserId`, or both.
- How the same Telegram user can have separate state in different bots.
- Whether the method applies to owner bots, partner bots, or both.
- Whether support channels, forced-join channels, tutorials, payment settings, and plan prices come from global configuration or tenant settings.

Never add tenant-aware code without documentation that explains tenant isolation.

---

## Payment, wallet, ledger, and withdrawal documentation requirements

Financial code requires the highest documentation standard.

When adding or changing payment, wallet, invoice, partner profit, ledger, or withdrawal code, XML docs and inline comments MUST explain:

- The financial event represented by the code.
- The source of truth for the amount.
- The currency and unit.
- Whether the operation credits or debits a user, partner, wallet, ledger, or withdrawal account.
- Whether negative values are allowed.
- Whether the operation is idempotent.
- How duplicate payment callbacks are handled.
- Whether the operation runs inside a database transaction.
- Which record links the payment provider event to the local order or invoice.
- What happens if payment succeeds but account delivery fails.
- What happens if account delivery succeeds but the final notification fails.

Never update a balance silently. Every balance change must have documentation explaining why the change is correct and how it can be audited.

---

## Database and migration documentation requirements

When adding or changing database schema, document:

- Why the table or column exists.
- Whether the data is global or tenant-scoped.
- The meaning of each id column.
- Uniqueness rules and indexes.
- Foreign-key expectations, even when SQLite constraints are not enforced.
- Migration and backfill strategy for existing data.
- Data retention expectations.

For state tables, document the state lifecycle.

For payment and ledger tables, document:

- Whether records are append-only.
- Which fields are immutable after creation.
- Which operation creates each record.
- Which operation consumes or settles each record.
- Idempotency keys or duplicate-prevention rules.

---

## Test documentation requirements

Tests must also include clear comments or XML docs when the scenario is not obvious.

When writing or modifying tests:

- Explain the business scenario being tested.
- Explain why the chosen inputs matter.
- For regression tests, mention the failure mode being protected against.
- For financial, tenant, state-machine, and payment tests, explicitly document the expected invariant.

Example:

```csharp
// Regression test: the same Telegram user can hold independent purchase state in two bots.
// This protects against accidentally keying state only by TelegramUserId instead of the
// composite TenantBotId + TelegramUserId key.
```

---

## Refactoring documentation requirements

When refactoring code, Codex MUST:

- Preserve correct existing XML docs.
- Remove or update comments that became false.
- Update `<param>`, `<returns>`, `<remarks>`, `<exception>`, and `<example>` when signatures or behavior change.
- Add XML docs to undocumented helpers created during the refactor.
- Avoid moving undocumented behavior into a new method without documenting the new method.

A refactor is not complete until the documentation still matches the behavior.

---

## Build and warning expectations

When the project is configured to generate XML documentation files or report missing XML comments, Codex must not suppress documentation warnings to finish faster.

If a new public or internal member causes documentation warnings, add accurate XML documentation instead of disabling the warning.

Do not use placeholder text such as `TODO`, `TBD`, `method`, `parameter`, or copied template sentences in XML docs.

---

## Definition of done

Before completing any task, Codex MUST review the diff and verify:

1. Every added or materially changed C# type/member/method has IntelliSense-visible XML documentation.
2. Every method parameter has a meaningful `<param>` description.
3. Every returned value has a meaningful `<returns>` description when applicable.
4. Every reusable or non-trivial method has usage guidance through `<remarks>` or `<example>`.
5. Every side effect is documented.
6. Every payment, wallet, ledger, tenant, state-machine, and external API change has explicit documentation.
7. Inline comments explain non-obvious implementation details without repeating syntax.
8. No comment is stale, misleading, copied from a template without editing, or too vague to help at the call site.
9. Build, tests, linting, or formatting checks were run when applicable.
10. Every touched file containing Persian/RTL text or emoji was reviewed in `git diff` for exact readable text, emoji preservation, and casing preservation.
11. Touched files were checked for mojibake markers such as `Ø`, `Ù`, `Û`, `Ã`, `Â`, `�`, and unexpected `????`.
12. Any Telegram/customer-facing Persian label changed by the patch was manually compared against the intended label; `dotnet build` alone was not treated as proof that the UI text is correct.

Use `git diff` before finalizing the patch.

---

## Final response requirement

After coding, Codex must include a short section named `Documentation added` and mention:

- Which files received new or updated XML documentation comments.
- Which important methods, classes, or properties were documented.
- Which inline comments were added for complex implementation logic.
- Any area that still needs human review because exact business behavior was ambiguous.

When the patch touches any file containing Persian/RTL text or emoji, Codex must also include a short section named `Source text safety checked` and mention:

- Which files with Persian/RTL text or emoji were touched.
- How the readable Persian text, emoji, and casing were verified.
- Whether mojibake-marker checks found anything and how it was resolved.
- Any remaining user-visible text that still needs human review.

Do not claim documentation or source-text safety is complete unless the diff was checked against this AGENTS.md file.