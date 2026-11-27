#!/usr/bin/env python3
"""Inspect zero-bonus owned top-ups without modifying either SQLite database."""

import argparse
from contextlib import closing
from datetime import datetime, timedelta
import json
from pathlib import Path
import sqlite3


# Fixed schema identifiers only. Window boundaries are bound parameters.
PROVIDERS = (
    ("hooshpay", "HooshPayPaymentInfos", "AmountToman", "PaymentStatus", ("paid",)),
    ("tetraminator", "TetraminatorPaymentInfos", "AmountToman", "PaymentStatus", ("paid",)),
    ("uniquepay", "UniquePayPaymentInfos", "BaseAmountToman", "PaymentStatus", ("paid",)),
    ("atlaspay", "AtlasPayPaymentInfos", "BaseAmountToman", "ProviderStatus", ("settled",)),
    # Be stricter than live settlement: only finished crypto is an audit candidate.
    ("nowpayments", "SwapinoPaymentInfos", "AmountToman", "PaymentStatus", ("finished",)),
)


def utc_time(value):
    """Accept a timezone-aware UTC boundary and reject implicit local time."""
    try:
        parsed = datetime.fromisoformat(value)
    except ValueError as error:
        raise argparse.ArgumentTypeError("expected ISO-8601 UTC, e.g. 2026-11-27T08:30:00Z") from error
    if parsed.utcoffset() != timedelta(0):
        raise argparse.ArgumentTypeError("window boundaries must be UTC")
    return parsed


def read_only(path):
    """Resolve an existing database and return an SQLite read-only URI."""
    return path.resolve(strict=True).as_uri() + "?mode=ro"


def audit(db, start, end):
    """Return masked, non-authorizing evidence with an explicit exclusion reason per zero promise."""
    invoices = " UNION ALL ".join(
        f"""SELECT '{provider}' AS provider, Id, BotId, TelegramUserId, {amount} AS principal,
                   CreatedAtUtc, SettledAtUtc, WalletOriginBotType, PaymentPurpose,
                   IsAddedToBalance, lower(trim({status})) IN ({','.join(repr(s) for s in statuses)}) AS paid
            FROM {table}"""
        for provider, table, amount, status, statuses in PROVIDERS
    )
    return [dict(row) for row in db.execute(f"""
        WITH invoices AS ({invoices}),
        scoped AS (
            SELECT i.*, b.Name, b.Percent, b.BonusToman, b.CompletedAtUtc, b.NextAttemptAtUtc,
                   b.TelegramUserId AS promise_user, b.BotId AS promise_bot, b.PrincipalToman,
                   'payment:' || i.provider || ':' || i.Id || ':credit' AS principal_key,
                   'wallet-charge-bonus:' || i.provider || ':' || i.Id AS gift_key
            FROM invoices i JOIN WalletChargeBonuses b ON b.Provider = i.provider AND b.PaymentId = i.Id
            WHERE i.WalletOriginBotType = 'owned' AND i.PaymentPurpose = 'wallet_charge'
              AND b.BonusToman = 0 AND julianday(i.CreatedAtUtc) >= julianday(:start)
              AND julianday(i.CreatedAtUtc) < julianday(:end)
        )
        SELECT s.provider, s.Id AS payment_id, s.BotId AS origin, s.CreatedAtUtc AS invoice_utc,
               s.SettledAtUtc AS settlement_utc, s.principal AS principal_toman,
               CASE
                 WHEN coalesce(s.BotId, '') = '' OR s.promise_bot IS NOT s.BotId
                   OR s.promise_user IS NOT s.TelegramUserId OR s.PrincipalToman != s.principal OR s.principal <= 0
                   THEN 'identity_or_amount_mismatch'
                 WHEN s.CompletedAtUtc IS NOT NULL OR s.NextAttemptAtUtc IS NOT NULL OR s.Percent != 0
                   OR EXISTS (SELECT 1 FROM credentials.WalletOperations WHERE OperationKey = s.gift_key)
                   OR EXISTS (SELECT 1 FROM WalletLedgerEntries WHERE IdempotencyKey = s.gift_key
                       OR (Reason = 'wallet_charge_bonus' AND Provider = s.provider
                           AND ReferenceId = CAST(s.Id AS TEXT)))
                   OR EXISTS (SELECT 1 FROM PaymentSettlementNotifications
                       WHERE NotificationKey = 'owned-wallet:' || s.provider || ':' || s.Id || ':campaign-confirmed')
                   THEN 'prior_bonus_evidence'
                 WHEN s.paid IS NOT 1 OR s.IsAddedToBalance IS NOT 1 OR s.SettledAtUtc IS NULL
                   THEN 'not_final_or_unsettled'
                 WHEN r.OperationKey IS NULL OR r.ApprovalKind IS NOT 'official'
                   OR r.TelegramUserId IS NOT s.TelegramUserId OR r.BotId IS NOT s.BotId OR r.AmountToman != s.principal
                   THEN 'no_matching_official_wallet_receipt'
                 WHEN NOT EXISTS (SELECT 1 FROM WalletLedgerEntries l
                     WHERE l.IdempotencyKey = s.principal_key AND l.TelegramUserId = s.TelegramUserId
                       AND l.Direction = 'credit' AND l.AmountToman = s.principal)
                   THEN 'no_matching_principal_ledger'
                 ELSE 'review_candidate'
               END AS assessment
        FROM scoped s LEFT JOIN credentials.WalletOperations r ON r.OperationKey = s.principal_key
        ORDER BY s.CreatedAtUtc, s.provider, s.Id
    """, {"start": start.isoformat(), "end": end.isoformat()})]


def main():
    """Read two SQLite files without applying changes and print only safe evidence."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("users", type=Path)
    parser.add_argument("credentials", type=Path)
    parser.add_argument("--start-utc", required=True, type=utc_time)
    parser.add_argument("--end-utc", required=True, type=utc_time)
    args = parser.parse_args()
    if args.start_utc >= args.end_utc:
        parser.error("start must be before end (exclusive)")
    with closing(sqlite3.connect(read_only(args.users), uri=True)) as db:
        db.row_factory = sqlite3.Row
        db.execute("ATTACH DATABASE ? AS credentials", (read_only(args.credentials),))
        db.execute("PRAGMA query_only = ON")
        db.execute("BEGIN")
        current = db.execute("SELECT Name, Percent, Enabled FROM WalletChargeCampaigns WHERE Id = 1").fetchone()
        observed = [dict(row) for row in db.execute("""
            WITH invoices(provider, id, issued) AS (
                SELECT 'hooshpay', Id, CreatedAtUtc FROM HooshPayPaymentInfos
                UNION ALL SELECT 'tetraminator', Id, CreatedAtUtc FROM TetraminatorPaymentInfos
                UNION ALL SELECT 'uniquepay', Id, CreatedAtUtc FROM UniquePayPaymentInfos
                UNION ALL SELECT 'atlaspay', Id, CreatedAtUtc FROM AtlasPayPaymentInfos
                UNION ALL SELECT 'nowpayments', Id, CreatedAtUtc FROM SwapinoPaymentInfos
            )
            SELECT b.Name AS name, b.Percent AS percent, min(i.issued) AS first_positive_invoice_utc,
                   max(i.issued) AS last_positive_invoice_utc, count(*) AS positive_promises
            FROM WalletChargeBonuses b JOIN invoices i ON i.provider = b.Provider AND i.id = b.PaymentId
            WHERE b.BonusToman > 0 AND julianday(i.issued) >= julianday(?) AND julianday(i.issued) < julianday(?)
            GROUP BY b.Name, b.Percent ORDER BY first_positive_invoice_utc
        """, (args.start_utc.isoformat(), args.end_utc.isoformat()))]
        result = {
            "warning": "Discovery only: verify the activation window, official provider results and prior credits before approving any repair.",
            "current_settings": dict(current) if current else None,
            "positive_invoice_observations": observed,
            "zero_promise_evidence": audit(db, args.start_utc, args.end_utc),
        }
        print(json.dumps(result, ensure_ascii=True, indent=2))


if __name__ == "__main__":
    main()
