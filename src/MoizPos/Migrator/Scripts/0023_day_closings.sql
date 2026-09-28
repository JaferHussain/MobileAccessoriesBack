-- 0023 — Counting the drawer at the end of a trading day.
--
-- The only control the shop has over physical cash. Every other figure reconciles against itself:
-- a salesman can take a cash sale, hand over the goods, record it perfectly and pocket the notes,
-- and no report will ever disagree — because the sale WAS recorded correctly. Counting the drawer
-- against what the day took is what makes that visible.
--
-- THE FIGURES ARE SNAPSHOTTED, NOT RECOMPUTED.
--
-- Every "+" and "−" line is stored as it stood at the moment of closing, not derived later. A
-- return taken tomorrow against a sale made today must not quietly rewrite what was counted last
-- night: the closing is evidence of one evening, and evidence that changes afterwards is not
-- evidence. This is the same reasoning that snapshots invoice_items.unit_cost_price.
--
-- One closing per trading day, enforced by the unique key. A day that could be closed twice would
-- let a short be closed away and reopened at a better figure.

CREATE TABLE day_closings (
    id                BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    -- The shop's own day, resolved in Asia/Karachi, not a UTC date.
    closing_date      DATE            NOT NULL,

    opening_float     DECIMAL(12,2)   NOT NULL,

    -- What the day recorded, snapshotted. Cash only: a bank transfer never entered the drawer.
    cash_sales        DECIMAL(12,2)   NOT NULL,
    cash_recovery     DECIMAL(12,2)   NOT NULL,
    cash_refunds      DECIMAL(12,2)   NOT NULL,
    cash_paid_out     DECIMAL(12,2)   NOT NULL,

    expected_cash     DECIMAL(12,2)   NOT NULL,
    counted_cash      DECIMAL(12,2)   NOT NULL,
    -- Counted less expected. Negative is short. Stored rather than derived so a closing reads
    -- the same in a year as it did on the night.
    difference        DECIMAL(12,2)   NOT NULL,

    -- A difference is SHOWN, never accused. This is where "Rs 300 to the delivery boy, not
    -- entered" goes — and whether those notes stop appearing over a few weeks is how the owner
    -- learns whether the recording discipline has taken hold.
    note              VARCHAR(500)    NULL,

    closed_by_user_id BIGINT UNSIGNED NOT NULL,
    closed_at_utc     DATETIME(6)     NOT NULL,

    PRIMARY KEY (id),
    UNIQUE KEY uq_day_closings_date (closing_date),
    KEY ix_day_closings_user (closed_by_user_id),

    CONSTRAINT fk_day_closings_user
        FOREIGN KEY (closed_by_user_id) REFERENCES users (id) ON DELETE RESTRICT,

    CONSTRAINT ck_day_closings_float_non_negative CHECK (opening_float >= 0),
    CONSTRAINT ck_day_closings_counted_non_negative CHECK (counted_cash >= 0)
) ENGINE = InnoDB
  DEFAULT CHARSET = utf8mb4
  COLLATE = utf8mb4_unicode_ci;
