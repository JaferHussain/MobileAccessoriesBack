-- 0026 — Cash paid to suppliers belongs in the drawer count.
--
-- A gap in 0023, found by asking the obvious question the feature invites: "I paid my supplier
-- today — where does that show?"
--
-- There are two ways notes leave the till, and the first version only knew about one of them:
--
--   * an expense taken from the drawer   (expenses.payment_source = 'Till')   — was counted
--   * a supplier bill settled in cash    (supplier_payments.payment_method = 'Cash') — was NOT
--
-- The second moves by far the larger amounts. Hand a supplier Rs 5,000 from the till and the
-- evening's count reported a Rs 5,000 short, with nothing to explain it — precisely the false
-- accusation this feature exists to avoid. The owner would go looking for money that had been
-- paid out perfectly legitimately.
--
-- Kept as its own column rather than folded into cash_paid_out: buying stock and paying the
-- electricity bill are different questions, and a shopkeeper reading a short wants to see which
-- of the two moved.
--
-- Existing closings keep 0. They were counted before this was measured, and rewriting a closing
-- after the fact would make it stop being evidence of that evening.

ALTER TABLE day_closings
    ADD COLUMN cash_to_suppliers DECIMAL(12,2) NOT NULL DEFAULT 0 AFTER cash_paid_out;
