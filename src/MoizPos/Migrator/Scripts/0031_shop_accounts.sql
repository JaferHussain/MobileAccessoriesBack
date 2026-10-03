-- 0031 — The shop's own accounts, and which one a payment went out of.
--
-- The owner registers the shop's bank and wallet accounts once — "HBL Current", "JazzCash 0300…"
-- — and every non-cash payment the shop MAKES can then say which account it came from, picked from
-- a list rather than typed. Used by expenses and supplier payments to start; the same column can
-- later be added to sales and recoveries to record which account a customer paid into.
--
-- Nothing is hard-deleted: an account that is no longer used is retired (is_active = FALSE), so a
-- past payment still shows where its money went.
--
-- account_type says which payment methods an account can carry:
--     Bank       bank transfer and Raast (Raast moves money between bank accounts)
--     JazzCash   JazzCash
--     EasyPaisa  EasyPaisa

CREATE TABLE shop_accounts (
    id             BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    name           VARCHAR(80)     NOT NULL,
    account_type   ENUM('Bank', 'JazzCash', 'EasyPaisa') NOT NULL,
    account_number VARCHAR(50)     NULL,
    account_title  VARCHAR(100)    NULL,
    is_active      BOOLEAN         NOT NULL DEFAULT TRUE,
    created_at_utc DATETIME(6)     NOT NULL,
    updated_at_utc DATETIME(6)     NULL,

    PRIMARY KEY (id),
    UNIQUE KEY uq_shop_accounts_name (name)
) ENGINE = InnoDB
  DEFAULT CHARSET = utf8mb4
  COLLATE = utf8mb4_unicode_ci;

-- Which account paid, and the bank's or app's reference for it. Both optional and NULL-able: a cash
-- expense has neither, and every expense recorded before now keeps NULL rather than a guess.
ALTER TABLE expenses
    ADD COLUMN shop_account_id BIGINT UNSIGNED NULL AFTER payment_method,
    ADD COLUMN transaction_id  VARCHAR(50)     NULL AFTER shop_account_id,
    ADD CONSTRAINT fk_expenses_shop_account
        FOREIGN KEY (shop_account_id) REFERENCES shop_accounts (id) ON DELETE RESTRICT;

-- A supplier payment already carries its reference in `note`; this records the account it left.
ALTER TABLE supplier_payments
    ADD COLUMN shop_account_id BIGINT UNSIGNED NULL AFTER payment_method,
    ADD CONSTRAINT fk_supplier_payments_shop_account
        FOREIGN KEY (shop_account_id) REFERENCES shop_accounts (id) ON DELETE RESTRICT;
