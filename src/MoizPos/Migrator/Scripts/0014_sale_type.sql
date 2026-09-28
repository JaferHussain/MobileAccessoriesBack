-- 0014 — Retail and wholesale sales
--
-- The shop sells both over the counter and in bulk to other shopkeepers, and the owner needs the
-- day's takings split between the two. Recording it on the invoice is the only way that split can
-- be reconstructed later: deriving it from the price charged would guess, and a discounted retail
-- sale would be miscounted as wholesale.
--
-- Existing invoices are marked Retail. That is the honest default for a shop whose counter sales
-- are the norm, and it is what every invoice written before this column existed actually was.

ALTER TABLE invoices
    ADD COLUMN sale_type VARCHAR(20) NOT NULL DEFAULT 'Retail' AFTER invoice_date_utc;

-- Stored as a string rather than an integer so a row read straight from the database says
-- "Wholesale", and so adding a third type later cannot renumber the existing two.
ALTER TABLE invoices
    ADD CONSTRAINT ck_invoices_sale_type CHECK (sale_type IN ('Retail', 'Wholesale'));

-- The day-end split is "this date, this type", and the drill-down is the same query without the
-- aggregate, so both are served by one index.
ALTER TABLE invoices
    ADD KEY ix_invoices_date_sale_type (invoice_date_utc, sale_type);
