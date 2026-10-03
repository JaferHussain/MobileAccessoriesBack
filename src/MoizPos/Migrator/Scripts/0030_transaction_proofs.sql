-- 0030 — Every non-cash transaction can carry its proof.
--
-- The owner's rule: any money received or sent other than in cash is kept with a screenshot of
-- the transfer. Sales have carried one since 0018. This extends the same column to every other
-- place money moves:
--
--     customer_payments   udhaar recovered from a customer         (money in)
--     supplier_payments   a supplier paid                          (money out)
--     sale_returns        a refund handed back after a return      (money out)
--     expenses            rent, bills, salary paid from the bank   (money out)
--
-- Optional, like the sale's: attached after the transaction is saved, never blocking it. The
-- "Proof missing" report is how the owner makes sure none is forgotten.
--
-- Every new column is NULL-able, so an INSERT that leaves one out stores NULL — never an ENUM's
-- first member (see the ENUM trap in CLAUDE.md, and 0025).

-- ------------------------------------------------------------------------ recovery and payments

ALTER TABLE customer_payments
    ADD COLUMN payment_proof_path VARCHAR(255) NULL AFTER payment_method;

ALTER TABLE supplier_payments
    ADD COLUMN payment_proof_path VARCHAR(255) NULL AFTER payment_method;

-- --------------------------------------------------------------------------------------- refunds

-- A refund never said how it was paid, and day close counted every one as cash leaving the
-- drawer — so a refund sent by JazzCash made the drawer look OVER by that amount. refund_method
-- is NULL when nothing was refunded (the return only reduced what the customer owed).
ALTER TABLE sale_returns
    ADD COLUMN refund_method ENUM('Cash', 'BankTransfer', 'JazzCash', 'EasyPaisa', 'Raast') NULL
        AFTER refund_due,
    ADD COLUMN refund_proof_path VARCHAR(255) NULL AFTER refund_method;

-- Every refund recorded so far was counted as cash by day close, and a closing is evidence of one
-- evening. Recording them as Cash keeps every past figure exactly as it was counted.
UPDATE sale_returns SET refund_method = 'Cash' WHERE refund_due > 0;

-- -------------------------------------------------------------------------------------- expenses

-- payment_source already says Till or Bank. For a Bank expense this says WHICH way it went —
-- the thing a proof is a screenshot of. NULL for Till, and for Bank expenses recorded before now.
ALTER TABLE expenses
    ADD COLUMN payment_method ENUM('BankTransfer', 'JazzCash', 'EasyPaisa', 'Raast') NULL
        AFTER payment_source,
    ADD COLUMN payment_proof_path VARCHAR(255) NULL AFTER payment_method;
