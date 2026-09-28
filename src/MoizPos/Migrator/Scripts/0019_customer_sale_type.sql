-- 0019 — Retail/wholesale filtering on the Customers screen (feature 004, FR-091..107)
--
-- A standing label the owner sets, not derived from invoices (FR-101): a wholesale party who
-- buys one item at the counter must not be silently reclassified, and a customer with no sales
-- yet must not have a type invented for them.

ALTER TABLE customers
    ADD COLUMN sale_type VARCHAR(20) NOT NULL DEFAULT 'Retail' AFTER address,
    ADD CONSTRAINT ck_customers_sale_type CHECK (sale_type IN ('Retail', 'Wholesale'));

CREATE INDEX ix_customers_sale_type ON customers (sale_type);
