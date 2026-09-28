-- 0022 — Where an expense's money came from.
--
-- The expenses table has always recorded an amount and a category but never WHERE the money came
-- from. That is a gap on its own — "was this paid from the till or the bank?" is an ordinary
-- question about an expense — and it is the gap that blocks reconciling the cash drawer: the day
-- close cannot know which expenses took notes out of the till.
--
-- NULL on purpose, and permanently:
--
--   * Every existing row predates the question. Marking them 'Till' would invent a fact, and
--     marking them 'Bank' would invent a different one. "Not recorded" is the truth about them —
--     the same reasoning that keeps customers.opening_balance nullable, where "never recorded"
--     and "recorded as zero" are different facts.
--   * A row with no source is simply absent from any cash calculation, which is correct: a drawer
--     cannot be reconciled for a day before the shop was tracking where cash went.
--
-- Only 'Till' is subtracted at day close. 'Bank' never touched the drawer.

ALTER TABLE expenses
    ADD COLUMN payment_source ENUM('Till', 'Bank') NULL AFTER amount;
