-- Purchase bills: one supplier bill, many products, and the payment made with it.
--
-- Until now a purchase was a single product, so a supplier's bill of ten items was ten unrelated
-- purchases with no bill number, no total and nowhere for the bill itself. A bill groups its
-- lines; each line is still an ordinary `purchases` row, so stock, the latest-cost rule, returns
-- and the supplier ledger keep working exactly as before.
--
-- Payable stays supplier-level (purchases − returns − payments); a payment may now also name the
-- bill it was made against, which is what lets a bill say "paid / part paid / owed".

CREATE TABLE purchase_bills (
    id               BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    supplier_id      BIGINT UNSIGNED NOT NULL,
    -- The number printed on the supplier's own bill, when there is one.
    bill_number      VARCHAR(60)     NULL,
    -- The shop's day the goods were bought — a date, never a time something can shift.
    bill_date        DATE            NOT NULL,
    total            DECIMAL(12,2)   NOT NULL,
    note             VARCHAR(255)    NULL,
    -- A photo of the supplier's bill, in the private proof directory. Only the path lives here.
    bill_image_path  VARCHAR(255)    NULL,
    user_id          BIGINT UNSIGNED NOT NULL,
    created_at_utc   DATETIME(6)     NOT NULL,

    PRIMARY KEY (id),
    KEY ix_purchase_bills_supplier (supplier_id, bill_date),
    KEY ix_purchase_bills_date (bill_date),

    CONSTRAINT fk_purchase_bills_supplier FOREIGN KEY (supplier_id) REFERENCES suppliers (id) ON DELETE RESTRICT,
    CONSTRAINT fk_purchase_bills_user     FOREIGN KEY (user_id)     REFERENCES users (id)     ON DELETE RESTRICT,
    CONSTRAINT ck_purchase_bills_total_positive CHECK (total > 0)
) ENGINE = InnoDB
  DEFAULT CHARSET = utf8mb4
  COLLATE = utf8mb4_unicode_ci;

-- Each line of a bill. NULL for every purchase recorded before bills existed.
ALTER TABLE purchases
    ADD COLUMN purchase_bill_id BIGINT UNSIGNED NULL AFTER supplier_id,
    ADD KEY ix_purchases_bill (purchase_bill_id),
    ADD CONSTRAINT fk_purchases_bill FOREIGN KEY (purchase_bill_id) REFERENCES purchase_bills (id) ON DELETE RESTRICT;

-- The bill a payment was made against. NULL for a payment on the supplier's account in general.
ALTER TABLE supplier_payments
    ADD COLUMN purchase_bill_id BIGINT UNSIGNED NULL AFTER supplier_id,
    ADD KEY ix_supplier_payments_bill (purchase_bill_id),
    ADD CONSTRAINT fk_supplier_payments_bill FOREIGN KEY (purchase_bill_id) REFERENCES purchase_bills (id) ON DELETE RESTRICT;
