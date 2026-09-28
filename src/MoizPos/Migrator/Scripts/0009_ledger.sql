-- 0009 — Customer ledger and payments (data-model.md §9–10, FR-020..023)
--
-- The udhaar register, in the same shape the shopkeeper already keeps by hand: each entry
-- records what was billed, what was paid, and the balance that resulted.
--
--   balance_after = previous balance_after + bill_amount - paid_amount
--
-- Persisting balance_after makes the ledger screen one indexed read instead of a replay of
-- every prior entry, and lets an integration test assert it against customers.outstanding_balance.

CREATE TABLE customer_payments (
    id               BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    customer_id      BIGINT UNSIGNED NOT NULL,
    receipt_number   VARCHAR(20)     NOT NULL,
    amount           DECIMAL(12,2)   NOT NULL,
    payment_method   ENUM('Cash', 'BankTransfer', 'JazzCash', 'EasyPaisa', 'Raast', 'Credit', 'Partial')
                     NOT NULL DEFAULT 'Cash',
    payment_date_utc DATETIME(6)     NOT NULL,
    -- True only when the caller deliberately accepted more than was owed (FR-022).
    is_overpayment   BOOLEAN         NOT NULL DEFAULT FALSE,
    note             VARCHAR(255)    NULL,
    user_id          BIGINT UNSIGNED NOT NULL,
    created_at_utc   DATETIME(6)     NOT NULL,

    PRIMARY KEY (id),
    UNIQUE KEY uq_customer_payments_receipt (receipt_number),
    KEY ix_customer_payments_customer (customer_id, payment_date_utc),

    CONSTRAINT fk_customer_payments_customer
        FOREIGN KEY (customer_id) REFERENCES customers (id) ON DELETE RESTRICT,
    CONSTRAINT fk_customer_payments_user
        FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE RESTRICT,

    CONSTRAINT ck_customer_payments_amount_positive CHECK (amount > 0)
) ENGINE = InnoDB
  DEFAULT CHARSET = utf8mb4
  COLLATE = utf8mb4_unicode_ci;


CREATE TABLE ledger_entries (
    id             BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    customer_id    BIGINT UNSIGNED NOT NULL,
    entry_date_utc DATETIME(6)     NOT NULL,
    entry_type     ENUM('Invoice', 'Payment', 'SaleReturn', 'Adjustment') NOT NULL,
    -- The invoice / payment / return this entry belongs to.
    reference_id   BIGINT UNSIGNED NULL,

    bill_amount    DECIMAL(12,2)   NOT NULL DEFAULT 0,
    paid_amount    DECIMAL(12,2)   NOT NULL DEFAULT 0,
    balance_after  DECIMAL(12,2)   NOT NULL,

    user_id        BIGINT UNSIGNED NOT NULL,
    created_at_utc DATETIME(6)     NOT NULL,

    PRIMARY KEY (id),
    -- Serves the ledger screen: one customer, in date order.
    KEY ix_ledger_customer (customer_id, entry_date_utc, id),

    CONSTRAINT fk_ledger_customer
        FOREIGN KEY (customer_id) REFERENCES customers (id) ON DELETE RESTRICT,
    CONSTRAINT fk_ledger_user
        FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE RESTRICT,

    CONSTRAINT ck_ledger_amounts_non_negative CHECK (bill_amount >= 0 AND paid_amount >= 0)
) ENGINE = InnoDB
  DEFAULT CHARSET = utf8mb4
  COLLATE = utf8mb4_unicode_ci;
