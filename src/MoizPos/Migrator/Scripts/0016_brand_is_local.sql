-- 0016 — Local brands (FR-087, FR-087a)
--
-- The shop stocks imported brands alongside locally made ones, and the owner wants to see local
-- stock on its own. "Local" is a property of the maker, so it lives on the brand, set by the owner
-- (clarification answer A, 2026-09-17).

ALTER TABLE brands
    -- NOT NULL: every brand is exactly Local or Imported. A nullable flag would add a third
    --   "unknown" state the product filter would then have to interpret.
    -- DEFAULT FALSE: every existing brand starts Imported. Guessing which of today's brands are
    --   local would misreport stock; nothing appears under Local until the owner marks it.
    ADD COLUMN is_local BOOLEAN NOT NULL DEFAULT FALSE AFTER description;
