-- 0007 — Customers (data-model.md §6, FR-019)
--
-- outstanding_balance is a running total maintained ONLY inside invoice, payment and
-- sale-return transactions. It must always equal the latest ledger_entries.balance_after
-- (invariant 2), which an integration test asserts.

CREATE TABLE customers (
    id                  BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    name                VARCHAR(150)    NOT NULL,
    -- Needed for WhatsApp delivery; stored normalised to digits with a country code.
    mobile_number       VARCHAR(20)     NULL,
    address             VARCHAR(255)    NULL,
    -- Negative only when an overpayment was explicitly confirmed (FR-022).
    outstanding_balance DECIMAL(12,2)   NOT NULL DEFAULT 0,
    is_active           BOOLEAN         NOT NULL DEFAULT TRUE,
    created_at_utc      DATETIME(6)     NOT NULL,
    updated_at_utc      DATETIME(6)     NULL,

    PRIMARY KEY (id),
    KEY ix_customers_name (name),
    KEY ix_customers_mobile (mobile_number),
    -- Backs the receivables report: who owes the shop money (FR-036).
    KEY ix_customers_balance (is_active, outstanding_balance)
) ENGINE = InnoDB
  DEFAULT CHARSET = utf8mb4
  COLLATE = utf8mb4_unicode_ci;
