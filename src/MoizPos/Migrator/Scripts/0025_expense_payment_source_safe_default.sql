-- 0025 — Make the value MySQL falls back to the harmless one.
--
-- MEASURED, NOT ASSUMED. On MySQL 8.0.40 with STRICT_ALL_TABLES enabled, against
-- `payment_source ENUM('Till','Bank') NOT NULL` with no DEFAULT, an INSERT that OMITS the column
-- does not error. It silently stores the ENUM's first value. Strict mode does not help: MySQL
-- treats an ENUM's first entry as an implicit default.
--
-- So 0024's NOT NULL stops an explicit NULL and nothing else. A future insert path — or a
-- hand-run script — that forgets this column books the expense as 'Till', which day close
-- subtracts from the cash drawer. The drawer then reads short by money that never left it, and
-- the shopkeeper hunts for cash that was never missing.
--
-- We cannot stop the coercion, so we choose what it lands on. 'Bank' is the harmless answer:
-- money paid from the bank never entered the drawer, so a wrongly-defaulted row is EXCLUDED from
-- the reconciliation rather than deducted from it — the same treatment NULL used to get.
--
-- It is still a wrong value, and the validator remains the thing that prevents it. This only
-- ensures that when something does go wrong, it goes wrong in the direction that does not
-- silently accuse the salesman of a short drawer.
--
-- ALGORITHM=COPY is explicit: reordering ENUM members must rebuild the table so existing rows are
-- re-mapped by their STRING value. An in-place change could reinterpret the stored index and turn
-- every 'Till' into 'Bank'.

ALTER TABLE expenses
    MODIFY COLUMN payment_source ENUM('Bank', 'Till') NOT NULL,
    ALGORITHM=COPY;
