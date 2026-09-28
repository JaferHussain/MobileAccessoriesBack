-- 0029 — Three prices, not four. Drop sale_price; retail_price becomes the real one.
--
-- The shop has three prices and only ever had three:
--
--     cost_price       what we paid the supplier
--     wholesale_price  what a bulk buyer pays
--     retail_price     what a walk-in pays
--
-- The table carried a fourth, sale_price, and the confusion ran the wrong way round: sale_price
-- was the column that actually quoted a retail sale, while retail_price — the one named for the
-- job — was written, displayed, and never read by any pricing code. Anyone reading the schema
-- would reasonably assume the opposite, and price something from the wrong column.
--
-- There is no way to make two columns for one price safe. One of them has to go, and the one
-- that goes is the one whose name does not say what it is.
--
-- AFTER THIS:
--     a retail sale    quotes retail_price
--     a wholesale sale quotes wholesale_price, falling back to retail_price when unset
--
-- The API still returns a field called `salePrice`. That is not this column: it is the RESOLVED
-- price for the sale being made — retail_price or wholesale_price, whichever applies — and it is
-- computed per request. It is deliberately not a stored value.

-- ---------------------------------------------------------------- carry the live figure across

-- sale_price is the column pricing has actually used, so it holds the truth. retail_price has
-- been kept equal to it since the stocking change, but any row priced before that could still
-- differ — and if the two disagree, the one that has been quoting sales is the right one.
UPDATE products
SET retail_price = sale_price
WHERE retail_price <> sale_price;

-- ---------------------------------------------------------------- drop it

-- The CHECK names sale_price, so it must go first — a constraint cannot outlive the column it
-- references.
--
-- DROP CONSTRAINT, not DROP CHECK. The shop's server is MariaDB 10.5 and the test database is
-- MySQL 8.0; `DROP CHECK` is MySQL-only syntax and fails outright on MariaDB, which is exactly
-- how this script failed on its first run against the live server. `DROP CONSTRAINT` is
-- understood by both (MariaDB 10.2+, MySQL 8.0.19+), so it is the form every future migration
-- touching a constraint must use.
ALTER TABLE products
    DROP CONSTRAINT ck_products_prices_non_negative;

ALTER TABLE products
    DROP COLUMN sale_price;

ALTER TABLE products
    ADD CONSTRAINT ck_products_prices_non_negative CHECK (
        cost_price >= 0 AND wholesale_price >= 0 AND retail_price >= 0
    );
