-- Udhaar customers: registered by the owner with name, phone and both sides of their ID card
-- before they may be sold to on udhaar.
--
-- The photos live in the private proof directory (id-cards/), never in wwwroot, never on the
-- public receipt link, and only the owner can open them. Only their paths are stored here.
--
-- customers.credit_allowed (0034) stays the mark itself. Customers marked by 0034's backfill have
-- no photos yet: they stay eligible, and the owner's list flags them "ID card missing".

ALTER TABLE customers
    ADD COLUMN id_card_front_path VARCHAR(255) NULL AFTER credit_allowed,
    ADD COLUMN id_card_back_path  VARCHAR(255) NULL AFTER id_card_front_path;
