-- 0021 — Where a non-cash payment came from (Sale module, checkout).
--
-- An invoice already records WHICH method was used and can carry a screenshot of it
-- (0018). Neither answers the question asked in a dispute: which account did the money come
-- from, and what was the reference? A JazzCash sale that records only "JazzCash" is a note, not
-- evidence.
--
--   payment_account_number  the CUSTOMER's account — the money came FROM there
--   payment_transaction_id  their reference for it
--
-- Both NULL by design, permanently. They are prompted for, never required: a customer is
-- standing at the counter and the sale must not wait while somebody hunts for a transaction id.
-- They can be filled in afterwards, exactly as the proof screenshot can.
--
-- A CASH sale is refused both, the same rule payment_proof_path follows: money counted into the
-- drawer came from no account, and a "reference" on it would be evidence of nothing.

ALTER TABLE invoices
    ADD COLUMN payment_account_number VARCHAR(50) NULL AFTER payment_method,
    ADD COLUMN payment_transaction_id VARCHAR(64) NULL AFTER payment_account_number;
