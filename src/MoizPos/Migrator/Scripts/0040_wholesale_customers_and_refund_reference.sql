-- Two fixes the owner reported.
--
-- 1. The Customers screen's Wholesale filter was always empty: nothing ever set a customer's type,
--    so everyone stayed 'Retail' however they bought. From now on a wholesale sale marks its
--    customer Wholesale (InvoiceService, one way only). This brings the customers who ALREADY
--    bought on a wholesale sale — Ikram among them — up to date.
UPDATE customers c
SET c.sale_type = 'Wholesale'
WHERE c.sale_type = 'Retail'
  AND EXISTS (SELECT 1 FROM invoices i WHERE i.customer_id = c.id AND i.sale_type = 'Wholesale');

-- 2. A refund sent back by transfer: the customer's account it went to, and the transaction —
--    the same two facts a transfer sale records (0021). NULL for cash, which carries neither.
ALTER TABLE sale_returns
    ADD COLUMN refund_account_number VARCHAR(50) NULL AFTER refund_method,
    ADD COLUMN refund_transaction_id VARCHAR(64) NULL AFTER refund_account_number;
