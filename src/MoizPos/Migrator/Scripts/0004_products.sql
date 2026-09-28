-- 0004 — Products (data-model.md §4, FR-001..006)
--
-- Each distinct variant is its own row. Five kinds of USB cable are five products, each with its
-- own image, cost, price and quantity — never one product with an options field (FR-001).

CREATE TABLE products (
    id                  BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    name                VARCHAR(150)    NOT NULL,
    category            VARCHAR(80)     NOT NULL,
    brand               VARCHAR(80)     NULL,
    model               VARCHAR(80)     NULL,
    barcode             VARCHAR(64)     NULL,
    image_path          VARCHAR(255)    NULL,

    -- The LATEST purchase cost. Overwritten on every purchase and applied to all stock on hand
    -- (FR-011a) — this is the shop owner's chosen costing rule, not a weighted average.
    -- Never served to a Staff principal (FR-040).
    cost_price          DECIMAL(12,4)   NOT NULL DEFAULT 0,

    wholesale_price     DECIMAL(12,2)   NOT NULL DEFAULT 0,
    retail_price        DECIMAL(12,2)   NOT NULL DEFAULT 0,
    -- The default price at the counter. Old stock sells at the current price (FR-011d).
    sale_price          DECIMAL(12,2)   NOT NULL DEFAULT 0,

    quantity_on_hand    INT             NOT NULL DEFAULT 0,
    min_stock_threshold INT             NOT NULL DEFAULT 0,
    supplier_id         BIGINT UNSIGNED NULL,
    is_active           BOOLEAN         NOT NULL DEFAULT TRUE,
    created_at_utc      DATETIME(6)     NOT NULL,
    updated_at_utc      DATETIME(6)     NULL,

    PRIMARY KEY (id),

    -- Barcodes are unique where present; many products legitimately have none.
    UNIQUE KEY uq_products_barcode (barcode),

    KEY ix_products_category (category),
    KEY ix_products_brand (brand),
    -- Covers the low-stock query without touching the table (FR-004).
    KEY ix_products_low_stock (is_active, quantity_on_hand, min_stock_threshold),

    -- Backs the single search box across name, brand, model and category (FR-003).
    FULLTEXT KEY ft_products_search (name, brand, model, category),

    CONSTRAINT fk_products_supplier
        FOREIGN KEY (supplier_id) REFERENCES suppliers (id)
        ON DELETE SET NULL,

    -- Defence in depth: the service refuses to oversell, and the database refuses to store the
    -- result if it ever gets through (FR-006).
    CONSTRAINT ck_products_quantity_non_negative CHECK (quantity_on_hand >= 0),
    CONSTRAINT ck_products_threshold_non_negative CHECK (min_stock_threshold >= 0),
    CONSTRAINT ck_products_prices_non_negative CHECK (
        cost_price >= 0 AND wholesale_price >= 0 AND retail_price >= 0 AND sale_price >= 0
    )
) ENGINE = InnoDB
  DEFAULT CHARSET = utf8mb4
  COLLATE = utf8mb4_unicode_ci;
