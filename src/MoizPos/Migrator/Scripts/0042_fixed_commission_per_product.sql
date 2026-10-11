-- 0042 — A fixed commission per product, set by the owner when he gives the salesman the goods.
--
-- The owner's rule for a field salesman was, and still is, half of whatever he sells above the
-- owner's price (0033). On top of it the owner can now promise a fixed amount per unit on any product
-- he issues — "Rs 50 on every charger you sell". It is earned the same way: once the customer has paid.
--
-- salesman_stock.commission_per_unit — what is promised on the units he carries now. 0 = none.
-- invoice_items.fixed_commission_per_unit — snapshotted at the sale, exactly like base_unit_price,
-- so changing the promise tomorrow never rewrites what last week earned. NULL on every line that is
-- not a field salesman's, and on every line sold before now.
ALTER TABLE salesman_stock
    ADD COLUMN commission_per_unit DECIMAL(12,2) NOT NULL DEFAULT 0 AFTER quantity;

ALTER TABLE invoice_items
    ADD COLUMN fixed_commission_per_unit DECIMAL(12,2) NULL AFTER commission_rate;
