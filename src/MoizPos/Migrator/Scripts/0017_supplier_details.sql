-- 0017 — Fuller supplier records (feature 007)
--
-- Identification and payment details the shop keeps on paper today. Every column is nullable:
-- suppliers recorded before this script must stay valid and editable without being forced to
-- fill anything in.
--
-- NOT added here: an opening balance. suppliers.payable_balance is governed by an invariant the
-- integration suite asserts —
--   payable_balance = SUM(purchases.total) - SUM(supplier_payments.amount) - SUM(purchase_returns.total)
-- — and an opening figure belongs to none of those three terms. Adding one means a ledger entry
-- and a correction-by-difference rule, the same shape as the customer opening balance in feature
-- 002. That is its own feature, not a column bolted on here.

ALTER TABLE suppliers
    ADD COLUMN cnic            VARCHAR(30)  NULL AFTER address,
    ADD COLUMN email           VARCHAR(255) NULL AFTER cnic,
    -- Stored exactly as entered. The supplier's own paperwork is the authority on the format of
    -- a CNIC or an account number, not this software.
    ADD COLUMN bank_name       VARCHAR(150) NULL AFTER email,
    ADD COLUMN bank_account_title  VARCHAR(150) NULL AFTER bank_name,
    ADD COLUMN bank_account_number VARCHAR(50)  NULL AFTER bank_account_title,
    ADD COLUMN notes           VARCHAR(500) NULL AFTER bank_account_number;
