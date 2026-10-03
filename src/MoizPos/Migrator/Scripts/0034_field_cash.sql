-- 0034 — Udhaar customers, and the cash a salesman carries.
--
-- 1. UDHAAR CUSTOMERS. Only the owner may complete a sale that leaves money owing — except that a
--    field salesman may now sell on udhaar to a customer the OWNER has marked as an udhaar
--    customer. The mark is the owner's decision, set only by the owner.
--
-- 2. FIELD CASH. Cash a salesman takes in the market is in his pocket, not in the counter drawer,
--    until he hands it over. Counting it as drawer cash would show every market sale as a short
--    at day close. So each sale, recovery and refund records whether it happened in the field —
--    decided at that moment from the seller's job, like every other snapshot here — and the drawer
--    counts only what did not. Cash he hands over is recorded in salesman_handovers and joins the
--    drawer then.

-- ------------------------------------------------------------------------------ udhaar customers

ALTER TABLE customers
    ADD COLUMN credit_allowed BOOLEAN NOT NULL DEFAULT FALSE AFTER sale_type;

-- Customers already on udhaar are the owner's udhaar customers: anyone who owes, anyone with an
-- amount brought forward from the register, anyone who ever bought with money left owing.
UPDATE customers c
SET c.credit_allowed = TRUE
WHERE c.outstanding_balance <> 0
   OR c.opening_balance IS NOT NULL
   OR EXISTS (SELECT 1 FROM invoices i WHERE i.customer_id = c.id AND i.amount_remaining > 0);

-- ------------------------------------------------------------------------------------ field cash

-- Everything recorded before now happened at the counter.
ALTER TABLE invoices          ADD COLUMN in_field BOOLEAN NOT NULL DEFAULT FALSE;
ALTER TABLE customer_payments ADD COLUMN in_field BOOLEAN NOT NULL DEFAULT FALSE;
ALTER TABLE sale_returns      ADD COLUMN in_field BOOLEAN NOT NULL DEFAULT FALSE;

-- Money a salesman hands to the shop: in cash into the drawer, or into one of the shop's accounts.
CREATE TABLE salesman_handovers (
    id                  BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    user_id             BIGINT UNSIGNED NOT NULL,
    amount              DECIMAL(12,2)   NOT NULL,
    payment_method      ENUM('Cash', 'BankTransfer', 'JazzCash', 'EasyPaisa', 'Raast') NOT NULL,
    note                VARCHAR(255)    NULL,
    received_at_utc     DATETIME(6)     NOT NULL,
    received_by_user_id BIGINT UNSIGNED NOT NULL,
    created_at_utc      DATETIME(6)     NOT NULL,

    PRIMARY KEY (id),
    KEY ix_salesman_handovers_user (user_id, received_at_utc),
    KEY ix_salesman_handovers_date (received_at_utc),

    CONSTRAINT fk_salesman_handovers_user
        FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE RESTRICT,
    CONSTRAINT fk_salesman_handovers_received_by
        FOREIGN KEY (received_by_user_id) REFERENCES users (id) ON DELETE RESTRICT,

    CONSTRAINT ck_salesman_handovers_amount_positive CHECK (amount > 0)
) ENGINE = InnoDB
  DEFAULT CHARSET = utf8mb4
  COLLATE = utf8mb4_unicode_ci;

-- A closing snapshots what the salesmen handed over in cash that day, like every other figure.
ALTER TABLE day_closings
    ADD COLUMN cash_from_salesmen DECIMAL(12,2) NOT NULL DEFAULT 0 AFTER cash_recovery;
