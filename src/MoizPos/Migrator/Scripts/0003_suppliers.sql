-- 0003 — Suppliers (data-model.md §3, FR-007)
--
-- payable_balance is a running total maintained ONLY inside purchase, supplier-payment and
-- purchase-return transactions. It must always equal
--   SUM(purchases.total) - SUM(supplier_payments.amount) - SUM(purchase_returns.total)
-- which an integration test asserts (invariant 3).

CREATE TABLE suppliers (
    id              BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    name            VARCHAR(150)    NOT NULL,
    contact_number  VARCHAR(20)     NULL,
    address         VARCHAR(255)    NULL,
    -- May go negative only when an overpayment was explicitly confirmed (FR-009).
    payable_balance DECIMAL(12,2)   NOT NULL DEFAULT 0,
    is_active       BOOLEAN         NOT NULL DEFAULT TRUE,
    created_at_utc  DATETIME(6)     NOT NULL,
    updated_at_utc  DATETIME(6)     NULL,

    PRIMARY KEY (id),
    KEY ix_suppliers_name (name)
) ENGINE = InnoDB
  DEFAULT CHARSET = utf8mb4
  COLLATE = utf8mb4_unicode_ci;
