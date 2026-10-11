-- 0041 — Which of the shop's own accounts a refund was paid from.
--
-- "Refunded by" on a sale return now lists the shop's registered accounts (Settings → Shop
-- accounts) beside cash, the same way an expense and a supplier payment already name the account
-- they left (0031). NULL for a cash refund, for a return that refunded nothing, and for every
-- refund recorded before now — never a guess.
ALTER TABLE sale_returns
    ADD COLUMN refund_shop_account_id BIGINT UNSIGNED NULL AFTER refund_method,
    ADD CONSTRAINT fk_sale_returns_refund_shop_account
        FOREIGN KEY (refund_shop_account_id) REFERENCES shop_accounts (id) ON DELETE RESTRICT;
