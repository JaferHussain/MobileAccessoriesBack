-- 0020 — Record the discount a return adjusts for (Returns module).
--
-- A charger listed at 600 and sold for 575 after a 25 discount comes back worth 575, not 600:
-- the shop refunds what the customer paid. But the 25 is not noise to be thrown away — the
-- shopkeeper tells the customer why they are handed 575 for a 600 item, and the owner needs to
-- see, months later, exactly how the figure was reached. So the return records all three:
--
--   unit_sale_price   what the item was BILLED at        600
--   unit_refund_price what one unit is worth BACK        575
--   discount_total    the adjustment, for the whole line  25
--
-- Deriving the discount from the invoice at read time would re-price history the moment anyone
-- edits the sale. Recorded once, at the values in force when the goods came back.

ALTER TABLE sale_return_items
    ADD COLUMN unit_refund_price DECIMAL(12,2) NOT NULL DEFAULT 0 AFTER unit_sale_price,
    ADD COLUMN discount_total    DECIMAL(12,2) NOT NULL DEFAULT 0 AFTER unit_cost_price;

-- Existing returns carry no discount: every one was recorded at the price it was billed at,
-- so the refund price IS the sale price and the adjustment is zero. This states that
-- explicitly rather than leaving a column of zeroes that reads like missing data.
UPDATE sale_return_items
SET unit_refund_price = unit_sale_price
WHERE unit_refund_price = 0;

ALTER TABLE sale_return_items
    ADD CONSTRAINT ck_sale_return_items_refund_non_negative
        CHECK (unit_refund_price >= 0 AND discount_total >= 0);
