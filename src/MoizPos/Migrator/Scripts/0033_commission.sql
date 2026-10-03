-- 0033 — The salesman's commission.
--
-- The owner's rule: a field salesman keeps half of whatever he sells above the owner's price, and
-- earns it once the customer has paid. He may never sell below that price.
--
-- Each of his sale lines records, at the moment of sale, the owner's price that applied and the
-- rate — the same discipline as unit_cost_price: a price changed tomorrow, or a new rate, must
-- never rewrite what last week's sales earned. Both are NULL on every other line: only a field
-- salesman's sales earn commission.
--
-- What has been EARNED is not stored. It is worked out from what the customers have paid, oldest
-- debt first, so it can never drift from the ledger. What has been PAID OUT to the salesman is
-- stored, in commission_payouts; the difference is what the shop still owes him.

ALTER TABLE invoice_items
    ADD COLUMN base_unit_price DECIMAL(12,2) NULL AFTER unit_sale_price,
    ADD COLUMN commission_rate DECIMAL(5,2)  NULL AFTER base_unit_price;

CREATE TABLE commission_payouts (
    id                  BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    -- The salesman paid.
    user_id             BIGINT UNSIGNED NOT NULL,
    amount              DECIMAL(12,2)   NOT NULL,
    payment_method      ENUM('Cash', 'BankTransfer', 'JazzCash', 'EasyPaisa', 'Raast') NOT NULL,
    note                VARCHAR(255)    NULL,
    paid_at_utc         DATETIME(6)     NOT NULL,
    -- The owner who paid him.
    recorded_by_user_id BIGINT UNSIGNED NOT NULL,
    created_at_utc      DATETIME(6)     NOT NULL,

    PRIMARY KEY (id),
    KEY ix_commission_payouts_user (user_id, paid_at_utc),
    KEY ix_commission_payouts_date (paid_at_utc),

    CONSTRAINT fk_commission_payouts_user
        FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE RESTRICT,
    CONSTRAINT fk_commission_payouts_recorded_by
        FOREIGN KEY (recorded_by_user_id) REFERENCES users (id) ON DELETE RESTRICT,

    CONSTRAINT ck_commission_payouts_amount_positive CHECK (amount > 0)
) ENGINE = InnoDB
  DEFAULT CHARSET = utf8mb4
  COLLATE = utf8mb4_unicode_ci;
