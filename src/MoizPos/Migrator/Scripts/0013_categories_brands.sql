-- 0013 — Categories and Brands as their own modules
--
-- Until now a product carried its category and brand as free text, so "Samsung", "samsung" and
-- "Samsng" were three different brands and nothing could be renamed in one place. Both become
-- real tables the owner maintains, and a product now points at them.
--
-- Existing text values are migrated, never discarded: every distinct category and brand already
-- in the catalogue becomes a row, and each product is reconnected to it.

CREATE TABLE categories (
    id             BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    name           VARCHAR(80)     NOT NULL,
    description    VARCHAR(255)    NULL,
    is_active      BOOLEAN         NOT NULL DEFAULT TRUE,
    created_at_utc DATETIME(6)     NOT NULL,
    updated_at_utc DATETIME(6)     NULL,

    PRIMARY KEY (id),
    -- The collation is accent- and case-insensitive, so this already refuses "Cables"/"cables".
    UNIQUE KEY uq_categories_name (name)
) ENGINE = InnoDB
  DEFAULT CHARSET = utf8mb4
  COLLATE = utf8mb4_unicode_ci;

CREATE TABLE brands (
    id             BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    name           VARCHAR(80)     NOT NULL,
    description    VARCHAR(255)    NULL,
    is_active      BOOLEAN         NOT NULL DEFAULT TRUE,
    created_at_utc DATETIME(6)     NOT NULL,
    updated_at_utc DATETIME(6)     NULL,

    PRIMARY KEY (id),
    UNIQUE KEY uq_brands_name (name)
) ENGINE = InnoDB
  DEFAULT CHARSET = utf8mb4
  COLLATE = utf8mb4_unicode_ci;

-- ---------------------------------------------------------------- migrate existing values

-- A product's category is required, so anything blank is parked under a visible placeholder
-- rather than silently dropped — the owner can rename it afterwards.
INSERT INTO categories (name, created_at_utc)
SELECT DISTINCT COALESCE(NULLIF(TRIM(category), ''), 'Uncategorised'), UTC_TIMESTAMP(6)
FROM products;

INSERT INTO brands (name, created_at_utc)
SELECT DISTINCT TRIM(brand), UTC_TIMESTAMP(6)
FROM products
WHERE brand IS NOT NULL AND TRIM(brand) <> '';

-- ---------------------------------------------------------------- repoint products

-- Added nullable, populated, then tightened: a NOT NULL column cannot be added to a table that
-- already has rows without inventing a value for them.
ALTER TABLE products
    ADD COLUMN category_id BIGINT UNSIGNED NULL AFTER name,
    ADD COLUMN brand_id    BIGINT UNSIGNED NULL AFTER category_id;

UPDATE products p
JOIN categories c
  ON c.name = COALESCE(NULLIF(TRIM(p.category), ''), 'Uncategorised')
SET p.category_id = c.id;

UPDATE products p
JOIN brands b ON b.name = TRIM(p.brand)
SET p.brand_id = b.id
WHERE p.brand IS NOT NULL AND TRIM(p.brand) <> '';

ALTER TABLE products
    MODIFY COLUMN category_id BIGINT UNSIGNED NOT NULL;

-- The old text columns back these indexes, so the indexes go first.
ALTER TABLE products
    DROP INDEX ft_products_search,
    DROP INDEX ix_products_category,
    DROP INDEX ix_products_brand;

ALTER TABLE products
    DROP COLUMN category,
    DROP COLUMN brand;

ALTER TABLE products
    ADD KEY ix_products_category (category_id),
    ADD KEY ix_products_brand (brand_id),
    -- RESTRICT, not CASCADE: deleting a category must never delete the shop's stock. The service
    -- refuses to remove a category that is still in use and asks the owner to reassign first.
    ADD CONSTRAINT fk_products_category
        FOREIGN KEY (category_id) REFERENCES categories (id) ON DELETE RESTRICT,
    ADD CONSTRAINT fk_products_brand
        FOREIGN KEY (brand_id) REFERENCES brands (id) ON DELETE RESTRICT;
