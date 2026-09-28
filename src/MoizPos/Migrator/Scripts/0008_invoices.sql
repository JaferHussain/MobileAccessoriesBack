-- 0008 — Invoices and their line items (data-model.md §7–8, FR-012..018)
--
-- Saving a sale is ONE transaction (data-model.md §7):
--   1. lock the affected product rows, in id order
--   2. verify stock, refusing the whole sale if any line is short
--   3. insert the invoice and its items, snapshotting the cost onto each line
--   4. decrement stock and write a movement per line
--   5. if anything remains unpaid, raise the customer's balance and append a ledger entry
-- All of it, or none of it.

CREATE TABLE invoices (
    id                BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    invoice_number    VARCHAR(20)     NOT NULL,
    -- NULL only for a fully paid walk-in sale; required when anything is owed (FR-017).
    customer_id       BIGINT UNSIGNED NULL,
    invoice_date_utc  DATETIME(6)     NOT NULL,

    subtotal          DECIMAL(12,2)   NOT NULL,
    order_discount    DECIMAL(12,2)   NOT NULL DEFAULT 0,
    total             DECIMAL(12,2)   NOT NULL,
    amount_paid       DECIMAL(12,2)   NOT NULL DEFAULT 0,
    amount_remaining  DECIMAL(12,2)   NOT NULL DEFAULT 0,
    -- Starts equal to total, reduced by sale returns (FR-024).
    net_amount        DECIMAL(12,2)   NOT NULL,

    payment_method    ENUM('Cash', 'BankTransfer', 'JazzCash', 'EasyPaisa', 'Raast', 'Credit', 'Partial')
                      NOT NULL,
    -- Guards against a double-tap at a busy counter creating two identical sales.
    idempotency_key   VARCHAR(64)     NULL,
    user_id           BIGINT UNSIGNED NOT NULL,
    created_at_utc    DATETIME(6)     NOT NULL,

    PRIMARY KEY (id),
    UNIQUE KEY uq_invoices_number (invoice_number),
    UNIQUE KEY uq_invoices_idempotency (idempotency_key),
    KEY ix_invoices_date (invoice_date_utc),
    KEY ix_invoices_customer (customer_id, invoice_date_utc),

    CONSTRAINT fk_invoices_customer
        FOREIGN KEY (customer_id) REFERENCES customers (id) ON DELETE RESTRICT,
    CONSTRAINT fk_invoices_user
        FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE RESTRICT,

    CONSTRAINT ck_invoices_total_non_negative CHECK (total >= 0),
    CONSTRAINT ck_invoices_paid_non_negative CHECK (amount_paid >= 0),
    CONSTRAINT ck_invoices_discount_non_negative CHECK (order_discount >= 0)
) ENGINE = InnoDB
  DEFAULT CHARSET = utf8mb4
  COLLATE = utf8mb4_unicode_ci;


CREATE TABLE invoice_items (
    id               BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    invoice_id       BIGINT UNSIGNED NOT NULL,
    product_id       BIGINT UNSIGNED NOT NULL,
    -- Snapshot: renaming or retiring a product must leave old invoices readable.
    product_name     VARCHAR(150)    NOT NULL,

    quantity         INT             NOT NULL,
    unit_sale_price  DECIMAL(12,2)   NOT NULL,
    line_discount    DECIMAL(12,2)   NOT NULL DEFAULT 0,

    -- THE most important column for correctness: the product's cost at the MOMENT of sale.
    -- Under the shop's latest-cost rule a later purchase overwrites products.cost_price, and
    -- without this snapshot every historical profit figure would silently change (FR-011c).
    unit_cost_price  DECIMAL(12,4)   NOT NULL,

    line_total       DECIMAL(12,2)   NOT NULL,
    returned_qty     INT             NOT NULL DEFAULT 0,

    PRIMARY KEY (id),
    KEY ix_invoice_items_invoice (invoice_id),
    KEY ix_invoice_items_product (product_id),

    CONSTRAINT fk_invoice_items_invoice
        FOREIGN KEY (invoice_id) REFERENCES invoices (id) ON DELETE RESTRICT,
    CONSTRAINT fk_invoice_items_product
        FOREIGN KEY (product_id) REFERENCES products (id) ON DELETE RESTRICT,

    CONSTRAINT ck_invoice_items_quantity_positive CHECK (quantity > 0),
    CONSTRAINT ck_invoice_items_returned_within_quantity
        CHECK (returned_qty >= 0 AND returned_qty <= quantity),
    CONSTRAINT ck_invoice_items_discount_non_negative CHECK (line_discount >= 0)
) ENGINE = InnoDB
  DEFAULT CHARSET = utf8mb4
  COLLATE = utf8mb4_unicode_ci;
