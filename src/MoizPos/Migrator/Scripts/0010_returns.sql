-- 0010 — Sale and purchase returns (data-model.md §13–14, FR-024..028)
--
-- A return inverts the original transaction using the values RECORDED AT THE TIME, never the
-- product's current figures. Under the shop's latest-cost rule products.cost_price moves with
-- every purchase, so reversing at today's cost would leave a residue in profit exactly equal to
-- the drift (research.md R11).

CREATE TABLE sale_returns (
    id              BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    invoice_id      BIGINT UNSIGNED NOT NULL,
    return_number   VARCHAR(20)     NOT NULL,
    return_date_utc DATETIME(6)     NOT NULL,
    total_amount    DECIMAL(12,2)   NOT NULL,
    -- Money owed back when the original sale was already settled (FR-028). A return against an
    -- unpaid credit sale reduces the balance instead, and leaves this at zero.
    refund_due      DECIMAL(12,2)   NOT NULL DEFAULT 0,
    reason          VARCHAR(255)    NULL,
    user_id         BIGINT UNSIGNED NOT NULL,
    created_at_utc  DATETIME(6)     NOT NULL,

    PRIMARY KEY (id),
    UNIQUE KEY uq_sale_returns_number (return_number),
    KEY ix_sale_returns_invoice (invoice_id),
    KEY ix_sale_returns_date (return_date_utc),

    CONSTRAINT fk_sale_returns_invoice
        FOREIGN KEY (invoice_id) REFERENCES invoices (id) ON DELETE RESTRICT,
    CONSTRAINT fk_sale_returns_user
        FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE RESTRICT,

    CONSTRAINT ck_sale_returns_total_positive CHECK (total_amount > 0),
    CONSTRAINT ck_sale_returns_refund_non_negative CHECK (refund_due >= 0)
) ENGINE = InnoDB
  DEFAULT CHARSET = utf8mb4
  COLLATE = utf8mb4_unicode_ci;


CREATE TABLE sale_return_items (
    id              BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    sale_return_id  BIGINT UNSIGNED NOT NULL,
    invoice_item_id BIGINT UNSIGNED NOT NULL,
    product_id      BIGINT UNSIGNED NOT NULL,
    quantity        INT             NOT NULL,
    -- Copied from the invoice line, not from the product: this is what makes the reversal exact.
    unit_sale_price DECIMAL(12,2)   NOT NULL,
    unit_cost_price DECIMAL(12,4)   NOT NULL,
    line_total      DECIMAL(12,2)   NOT NULL,

    PRIMARY KEY (id),
    KEY ix_sale_return_items_return (sale_return_id),
    KEY ix_sale_return_items_invoice_item (invoice_item_id),

    CONSTRAINT fk_sale_return_items_return
        FOREIGN KEY (sale_return_id) REFERENCES sale_returns (id) ON DELETE RESTRICT,
    CONSTRAINT fk_sale_return_items_invoice_item
        FOREIGN KEY (invoice_item_id) REFERENCES invoice_items (id) ON DELETE RESTRICT,
    CONSTRAINT fk_sale_return_items_product
        FOREIGN KEY (product_id) REFERENCES products (id) ON DELETE RESTRICT,

    CONSTRAINT ck_sale_return_items_quantity_positive CHECK (quantity > 0)
) ENGINE = InnoDB
  DEFAULT CHARSET = utf8mb4
  COLLATE = utf8mb4_unicode_ci;


CREATE TABLE purchase_returns (
    id              BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    purchase_id     BIGINT UNSIGNED NOT NULL,
    supplier_id     BIGINT UNSIGNED NOT NULL,
    product_id      BIGINT UNSIGNED NOT NULL,
    return_number   VARCHAR(20)     NOT NULL,
    return_date_utc DATETIME(6)     NOT NULL,
    quantity        INT             NOT NULL,
    -- The cost of the PURCHASE being returned, so the payable unwinds by exactly what it added.
    unit_cost       DECIMAL(12,4)   NOT NULL,
    total           DECIMAL(12,2)   NOT NULL,
    reason          VARCHAR(255)    NULL,
    user_id         BIGINT UNSIGNED NOT NULL,
    created_at_utc  DATETIME(6)     NOT NULL,

    PRIMARY KEY (id),
    UNIQUE KEY uq_purchase_returns_number (return_number),
    KEY ix_purchase_returns_purchase (purchase_id),
    KEY ix_purchase_returns_supplier (supplier_id, return_date_utc),

    CONSTRAINT fk_purchase_returns_purchase
        FOREIGN KEY (purchase_id) REFERENCES purchases (id) ON DELETE RESTRICT,
    CONSTRAINT fk_purchase_returns_supplier
        FOREIGN KEY (supplier_id) REFERENCES suppliers (id) ON DELETE RESTRICT,
    CONSTRAINT fk_purchase_returns_product
        FOREIGN KEY (product_id) REFERENCES products (id) ON DELETE RESTRICT,
    CONSTRAINT fk_purchase_returns_user
        FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE RESTRICT,

    CONSTRAINT ck_purchase_returns_quantity_positive CHECK (quantity > 0)
) ENGINE = InnoDB
  DEFAULT CHARSET = utf8mb4
  COLLATE = utf8mb4_unicode_ci;
