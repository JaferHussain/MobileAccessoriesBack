-- 0015 — Customer opening balances (FR-065..FR-073)
--
-- The shop kept udhaar on paper for years before this software existed. Without somewhere to put
-- what a customer already owed, every long-standing customer reads as owing nothing and the
-- receivables total understates what the shop is owed.
--
-- Two pieces: the figure itself on the customer, and an entry in their ledger explaining it.
-- Both are needed. The column is what lets a second recording be recognised as a CORRECTION
-- rather than a second debt (FR-070); the ledger entry is what keeps the register's own
-- invariant true — that reading it top to bottom reproduces the balance.

ALTER TABLE customers
    -- Nullable on purpose. "Never recorded" and "recorded as zero" are different facts about a
    -- customer, and the service decides correction-versus-first-recording by asking which this is.
    -- A DEFAULT 0 would make every customer look like they had been through the paper register.
    ADD COLUMN opening_balance DECIMAL(12,2) NULL DEFAULT NULL AFTER outstanding_balance;

ALTER TABLE customers
    -- A negative figure would mean the shop owes the customer, which is a different thing
    -- entirely and out of scope (FR-069). The service refuses it too; this is the backstop.
    ADD CONSTRAINT ck_customers_opening_balance_non_negative
        CHECK (opening_balance IS NULL OR opening_balance >= 0);

-- Appended to the END of the ENUM so every existing value keeps its ordinal and no stored row
-- changes meaning. Adding in the middle would silently reinterpret existing ledger entries.
ALTER TABLE ledger_entries
    MODIFY COLUMN entry_type
        ENUM('Invoice', 'Payment', 'SaleReturn', 'Adjustment', 'OpeningBalance') NOT NULL;

ALTER TABLE ledger_entries
    -- Why this entry exists. Mandatory when correcting an opening balance (FR-071): the reason
    -- has to be visible to whoever reads the customer's ledger, and the audit trail alone would
    -- hide it from exactly that person.
    ADD COLUMN note VARCHAR(255) NULL AFTER balance_after;
