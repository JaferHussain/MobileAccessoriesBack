-- 0027 — Which categories each brand carries, and a brand on every product.
--
-- The owner's setup order is now Brands → Categories → Products: you pick Vivo, and you are
-- offered the categories Vivo actually sells (Mobile, Handsfree), not all twenty.
--
-- WHY A LINK TABLE, AND NOT categories.brand_id
--
-- Hanging a category off a single brand was the obvious reading of the request, and it is the
-- wrong shape. "Handsfree" would become one row under Vivo and a DIFFERENT row under Oppo, and
-- with ten brands and eight categories the eight rows the shop has today become eighty. Every
-- question of the form "what did I sell in Handsfree this month" would then have to be answered
-- by adding up ten separate Handsfree figures — which is precisely the duplication that
-- migration 0013 existed to remove when it turned free-text categories into a real list.
--
-- A link table gives the owner the guided Brand → Category → Product entry they asked for while
-- keeping ONE Handsfree row, so the reports still answer in one number.
--
-- WHY EVERY PRODUCT NOW HAS A BRAND
--
-- The owner's rule: anything without a well-known brand is sold under a general local brand.
-- That makes brand_id required, which in turn is what lets the product form ask for the brand
-- FIRST and filter the categories by it — a nullable brand would leave the form with no way to
-- narrow the list for exactly the products most likely to be mis-filed.

-- ---------------------------------------------------------------- the link

CREATE TABLE brand_categories (
    brand_id       BIGINT UNSIGNED NOT NULL,
    category_id    BIGINT UNSIGNED NOT NULL,
    created_at_utc DATETIME(6)     NOT NULL,

    PRIMARY KEY (brand_id, category_id),
    KEY ix_brand_categories_category (category_id),

    -- CASCADE, unlike the RESTRICT used everywhere else in this schema. A link row is a
    -- statement about what a brand carries TODAY — it is configuration, not history. Nothing
    -- reads it to reproduce a past figure, so it may follow its brand or category out. Every
    -- row that IS history (a sale, a stock movement, a ledger entry) still restricts.
    CONSTRAINT fk_brand_categories_brand
        FOREIGN KEY (brand_id) REFERENCES brands (id) ON DELETE CASCADE,
    CONSTRAINT fk_brand_categories_category
        FOREIGN KEY (category_id) REFERENCES categories (id) ON DELETE CASCADE
) ENGINE = InnoDB
  DEFAULT CHARSET = utf8mb4
  COLLATE = utf8mb4_unicode_ci;

-- ---------------------------------------------------------------- a brand for unbranded stock

-- Created only when something actually needs it, so a shop that already brands everything does
-- not acquire an empty "Local" brand it never asked for.
--
-- is_local = TRUE because that is what this brand means. It also makes the existing "local only"
-- filter on the Products screen start reporting these goods, which it could not do before:
-- that filter requires a brand marked local, and these products had no brand at all.
INSERT INTO brands (name, description, is_local, is_active, created_at_utc)
SELECT 'Local',
       'Goods with no well-known brand, sold under the shop''s own local label.',
       TRUE,
       TRUE,
       UTC_TIMESTAMP(6)
WHERE EXISTS (SELECT 1 FROM products WHERE brand_id IS NULL)
  AND NOT EXISTS (SELECT 1 FROM brands WHERE name = 'Local');

UPDATE products
SET brand_id = (SELECT id FROM brands WHERE name = 'Local'),
    updated_at_utc = UTC_TIMESTAMP(6)
WHERE brand_id IS NULL;

-- ---------------------------------------------------------------- backfill from real data

-- Derived from the catalogue rather than invented: every brand/category pairing the shop has
-- actually used becomes a link, so nothing that was sellable yesterday is unsellable today.
-- Run AFTER the Local assignment above so those products bring their categories with them.
INSERT INTO brand_categories (brand_id, category_id, created_at_utc)
SELECT DISTINCT p.brand_id, p.category_id, UTC_TIMESTAMP(6)
FROM products p
WHERE p.brand_id IS NOT NULL;

-- ---------------------------------------------------------------- require it

-- Safe only because the UPDATE above left no NULLs. The type is repeated exactly; changing it
-- would force MySQL to rebuild the foreign key that already points at brands.
ALTER TABLE products
    MODIFY COLUMN brand_id BIGINT UNSIGNED NOT NULL;
