# Wallet-charge campaign: audit and reviewed remediation

This is a read-only process for issue #15. The rollout changes **new** owned
invoices only. Neither deployment nor startup modifies historical promises or
credits historical payments.

## Find and verify candidates

Use trusted SQLite copies (or read-only access to both live files) and an
explicit UTC discovery window:

```sh
python3 scripts/audit-wallet-charge-bonuses.py /path/users.db /path/credentials.db \
  --start-utc 2026-11-26T20:30:00Z --end-utc 2026-11-27T20:30:00Z
```

Both SQLite connections are opened read-only. Output omits users' Telegram
identities and provider secrets. `review_candidate` is not authorization to
credit. It means an owned charge has a zero promise, a terminal paid status,
and matching principal wallet and ledger receipts, with no known campaign
credit evidence. Review excluded and missing evidence separately; never
relax the checks mechanically. When auditing backups, use an independently
verified timeline and recheck the latest live records before any write.

- Establish the activation, pause, reactivation and rate timeline from operator
  evidence or timestamped backups. Current settings and the first/last positive
  invoice are observations, **not** proof of continuous activation. Check the
  invoice issuance time, not settlement time; the provider row's creation time
  is only a proxy near a boundary and needs corroboration there.
- Start the review with NOWPayments **157**, NOWPayments **158**, and AtlasPay
  **804**. At a verified 20% rate their proposed gifts are 55,000, 42,000 and
  80,000 toman. Do not include the default-bot AtlasPay **797** based on the
  broad discovery window alone: it predates the first observed positive promise.
- For each candidate verify the originating bot was owned, official provider
  inquiry matches the exact payment, principal and buyer, and neither a refund,
  reversal, previous promotion, nor manual adjustment already compensated it.
  Confirm the original principal via the payment, credentials receipt and
  users.db ledger. Resolve partial or provisional cases separately.

### Read-only production evidence for issue #15

The November 27, 2026 Tehran activity logs showed the super-admin enabling the
campaign at 09:53 and no later pause or rate change through the audit. The first
positive 20% promise was issued at 09:54:16, and the saved campaign was still
enabled at 20%. The audit matched the official principal receipts and ledger
entries of all three listed payments to their zero promises, with no known
campaign credit. Their invoice times in Tehran were 15:34:12, 17:40:57 and
17:45:32, respectively. AtlasPay 797 was issued at 09:34:52, before activation,
so it is excluded. The proposed review manifest for the three is 177,000 toman;
this finding does not authorize a wallet mutation.

## Approval and execution boundary

1. Create a fixed manifest with the exact provider/local payment id, origin,
   verified issuance window and rate evidence, principal, proposed positive
   whole-toman gift, and prior-credit checks. Obtain explicit approval for the
   manifest and the write procedure. Do not approve a time range or arbitrary
   query result as a batch.
2. Use a separately reviewed, operator-only command after a verified backup.
   Recheck all evidence on the live databases immediately before each write.
   Credit **only the gift** through `CredentialsStore.MutateWalletAsync` using
   the canonical key `wallet-charge-bonus:{provider}:{paymentId}`. The key,
   target wallet and positive amount must be fixed across retries. If an
   existing key matches, recover its ledger/receipt, not the balance. If
   anything differs, stop for investigation.
3. Record a separate `wallet_charge_bonus` ledger entry with the same key,
   linking back to the payment and the reviewed adjustment. Report balances
   from the credit receipt. Deliver an independently idempotent adjustment
   notice only after ledger verification; do not replay the principal
   notification. Preserve the original invoice snapshot and principal
   receipts. The ordinary worker does not handle ledger recovery for an
   out-of-band gift, so the reviewed command must explicitly resume that
   step if the process stops.
4. Dry-run the reviewed command on copies, then reconcile the completed
   manifest against both databases after execution. No write tool or
   production balance adjustment is included with this rollout.
