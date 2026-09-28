-- 0024 — An expense must say where its money came from.
--
-- Migration 0022 added payment_source as nullable, because every row that existed then predated
-- the question and marking them 'Till' or 'Bank' would have invented a fact. The expense form
-- and the validator now require an answer, and the rows that had none have been removed, so the
-- column can carry the rule itself.
--
-- WHY THE COLUMN AND NOT JUST THE VALIDATOR:
--
-- The validator is the rule; this is the floor under it. An expense with no source is invisible
-- to the day's drawer count, so the drawer reads short by exactly the amount that legitimately
-- left the till — a silent wrong answer to the one question day close exists to ask. Belt and
-- braces is the right amount of care for that.
--
-- WHAT THIS DOES NOT BUY, measured rather than assumed:
--
--   * It does not stop a WRONG answer. 'Bank' on a till expense saves perfectly well.
--   * It does not stop an OMITTED column. Under a non-strict sql_mode MySQL coerces a missing
--     value to the ENUM's first entry — 'Till' — so a script that forgets this column silently
--     books the expense as cash and subtracts it from the drawer. That is worse than the NULL it
--     replaced, which was simply excluded. Verified on the test server, which is not strict.
--
-- Hence the SET below, and hence the validator remaining the actual rule. The column is a floor
-- under it, not a substitute for it.

-- Make the failure loud, whatever the server is configured to do.
--
-- Under a non-strict sql_mode MySQL would "helpfully" coerce existing NULLs into the ENUM's first
-- value instead of refusing — silently marking unknown expenses as Till and feeding invented
-- figures into the drawer reconciliation. Strict mode turns that into error 1138, "Invalid use of
-- NULL value", which stops this script and leaves the schema untouched.
SET SESSION sql_mode = CONCAT(@@sql_mode, ',STRICT_ALL_TABLES');

ALTER TABLE expenses
    MODIFY COLUMN payment_source ENUM('Till', 'Bank') NOT NULL;
