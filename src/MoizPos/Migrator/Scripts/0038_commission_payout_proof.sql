-- A screenshot for commission paid to a salesman by transfer — the owner's rule that every
-- transaction except cash is kept with its proof. A cash payout needs none: it left the drawer.
-- Stored like every other proof, in the private proof directory; only the path lives here.

ALTER TABLE commission_payouts
    ADD COLUMN payment_proof_path VARCHAR(255) NULL AFTER note;
