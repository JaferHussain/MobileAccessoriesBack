-- 0005 — Stock movements (data-model.md §5, FR-005)
--
-- Append-only. Written inside the SAME transaction as the products.quantity_on_hand update it
-- explains, so the two can never disagree (invariant 1). Never updated, never deleted.

CREATE TABLE stock_movements (
    id             BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    product_id     BIGINT UNSIGNED NOT NULL,
    -- Signed: positive for stock in, negative for stock out.
    change_qty     INT             NOT NULL,
    -- Persisted so the history reads correctly without replaying every prior row.
    resulting_qty  INT             NOT NULL,
    reason         ENUM('Purchase', 'Sale', 'SaleReturn', 'PurchaseReturn', 'Adjustment') NOT NULL,
    -- The purchase / invoice / return this movement belongs to.
    reference_id   BIGINT UNSIGNED NULL,
    user_id        BIGINT UNSIGNED NOT NULL,
    note           VARCHAR(255)    NULL,
    created_at_utc DATETIME(6)     NOT NULL,

    PRIMARY KEY (id),
    KEY ix_stock_movements_product (product_id, created_at_utc, id),
    KEY ix_stock_movements_reason (reason, created_at_utc),

    CONSTRAINT fk_stock_movements_product
        FOREIGN KEY (product_id) REFERENCES products (id)
        ON DELETE RESTRICT,

    -- RESTRICT: deleting a user must never erase the record of stock they moved.
    CONSTRAINT fk_stock_movements_user
        FOREIGN KEY (user_id) REFERENCES users (id)
        ON DELETE RESTRICT,

    CONSTRAINT ck_stock_movements_resulting_non_negative CHECK (resulting_qty >= 0),
    CONSTRAINT ck_stock_movements_change_non_zero CHECK (change_qty <> 0)
) ENGINE = InnoDB
  DEFAULT CHARSET = utf8mb4
  COLLATE = utf8mb4_unicode_ci;
