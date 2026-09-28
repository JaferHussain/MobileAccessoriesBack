-- 0006 — Purchases and supplier payments (data-model.md §11–12, FR-008..011)
--
-- Recording a purchase is ONE transaction that does six things (data-model.md §11):
--   1. insert the purchase row
--   2. products.quantity_on_hand += quantity
--   3. products.cost_price = unit_cost      <-- the owner's latest-cost rule (FR-011a)
--   4. suppliers.payable_balance += total
--   5. insert a stock movement
--   6. insert audit rows for the quantity and cost changes
-- All of it, or none of it.

CREATE TABLE purchases (
    id                BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    supplier_id       BIGINT UNSIGNED NOT NULL,
    product_id        BIGINT UNSIGNED NOT NULL,
    purchase_date_utc DATETIME(6)     NOT NULL,
    -- Four decimal places: cost is the basis of every profit figure, so it carries headroom.
    unit_cost         DECIMAL(12,4)   NOT NULL,
    quantity          INT             NOT NULL,
    total             DECIMAL(12,2)   NOT NULL,
    returned_qty      INT             NOT NULL DEFAULT 0,
    user_id           BIGINT UNSIGNED NOT NULL,
    created_at_utc    DATETIME(6)     NOT NULL,

    PRIMARY KEY (id),
    KEY ix_purchases_supplier (supplier_id, purchase_date_utc),
    KEY ix_purchases_product (product_id, purchase_date_utc),
    KEY ix_purchases_date (purchase_date_utc),

    CONSTRAINT fk_purchases_supplier
        FOREIGN KEY (supplier_id) REFERENCES suppliers (id) ON DELETE RESTRICT,
    CONSTRAINT fk_purchases_product
        FOREIGN KEY (product_id) REFERENCES products (id) ON DELETE RESTRICT,
    CONSTRAINT fk_purchases_user
        FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE RESTRICT,

    CONSTRAINT ck_purchases_quantity_positive CHECK (quantity > 0),
    CONSTRAINT ck_purchases_unit_cost_positive CHECK (unit_cost > 0),
    CONSTRAINT ck_purchases_returned_within_quantity CHECK (returned_qty >= 0 AND returned_qty <= quantity)
) ENGINE = InnoDB
  DEFAULT CHARSET = utf8mb4
  COLLATE = utf8mb4_unicode_ci;


CREATE TABLE supplier_payments (
    id               BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    supplier_id      BIGINT UNSIGNED NOT NULL,
    amount           DECIMAL(12,2)   NOT NULL,
    payment_date_utc DATETIME(6)     NOT NULL,
    payment_method   ENUM('Cash', 'BankTransfer', 'JazzCash', 'EasyPaisa', 'Raast', 'Credit', 'Partial')
                     NOT NULL DEFAULT 'Cash',
    -- True only when the caller deliberately paid more than was owed (FR-009).
    is_overpayment   BOOLEAN         NOT NULL DEFAULT FALSE,
    note             VARCHAR(255)    NULL,
    user_id          BIGINT UNSIGNED NOT NULL,
    created_at_utc   DATETIME(6)     NOT NULL,

    PRIMARY KEY (id),
    KEY ix_supplier_payments_supplier (supplier_id, payment_date_utc),

    CONSTRAINT fk_supplier_payments_supplier
        FOREIGN KEY (supplier_id) REFERENCES suppliers (id) ON DELETE RESTRICT,
    CONSTRAINT fk_supplier_payments_user
        FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE RESTRICT,

    CONSTRAINT ck_supplier_payments_amount_positive CHECK (amount > 0)
) ENGINE = InnoDB
  DEFAULT CHARSET = utf8mb4
  COLLATE = utf8mb4_unicode_ci;
