-- 0018 — Payment proof on a non-cash sale (feature 008)
--
-- Cash is its own proof: the money is in the drawer. A bank transfer, JazzCash, EasyPaisa or
-- Raast payment is a claim, and until now the only record of it was a screenshot on somebody's
-- phone. This holds the path to that screenshot against the invoice it belongs to.
--
-- Nullable, and stays nullable: the picture is optional by design (FR-002). A sale must never be
-- blocked because a customer's screenshot is slow to arrive, so "no proof" is an ordinary,
-- permanent state for an invoice rather than a gap waiting to be filled.
--
-- The path only — never the bytes. Same reasoning as product images (research R7): blobs would
-- bloat every SELECT and, worse, the nightly mysqldump the shop recovers from.

ALTER TABLE invoices
    ADD COLUMN payment_proof_path VARCHAR(255) NULL AFTER payment_method;
