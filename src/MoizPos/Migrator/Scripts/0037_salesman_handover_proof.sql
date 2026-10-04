-- A screenshot for money a salesman hands over by transfer (JazzCash, EasyPaisa, bank, Raast) —
-- the owner's rule that every transaction except cash is kept with its proof. A cash handover
-- needs none: it was counted into the drawer. Stored like every other proof, in the private
-- proof directory; only the path lives here.

ALTER TABLE salesman_handovers
    ADD COLUMN payment_proof_path VARCHAR(255) NULL AFTER note;
