-- 0011 — Expenses (data-model.md §15, FR-029..030)
--
-- Expenses are what turn gross profit into net profit. They are Admin-only data: a salesman who
-- could see the rent and the salaries could work out the shop's margins (FR-040).

CREATE TABLE expense_categories (
    id             BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    name           VARCHAR(80)     NOT NULL,
    is_active      BOOLEAN         NOT NULL DEFAULT TRUE,
    created_at_utc DATETIME(6)     NOT NULL,

    PRIMARY KEY (id),
    UNIQUE KEY uq_expense_categories_name (name)
) ENGINE = InnoDB
  DEFAULT CHARSET = utf8mb4
  COLLATE = utf8mb4_unicode_ci;


-- The list the spec names, seeded once. It stays extensible — the owner can add more.
INSERT INTO expense_categories (name, is_active, created_at_utc) VALUES
    ('Rent',        TRUE, UTC_TIMESTAMP(6)),
    ('Electricity', TRUE, UTC_TIMESTAMP(6)),
    ('Internet',    TRUE, UTC_TIMESTAMP(6)),
    ('Transport',   TRUE, UTC_TIMESTAMP(6)),
    ('Salary',      TRUE, UTC_TIMESTAMP(6)),
    ('Other',       TRUE, UTC_TIMESTAMP(6));


CREATE TABLE expenses (
    id               BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    category_id      BIGINT UNSIGNED NOT NULL,
    amount           DECIMAL(12,2)   NOT NULL,
    expense_date_utc DATETIME(6)     NOT NULL,
    note             VARCHAR(255)    NULL,
    user_id          BIGINT UNSIGNED NOT NULL,
    created_at_utc   DATETIME(6)     NOT NULL,

    PRIMARY KEY (id),
    -- Serves the period sum the profit engine runs for every report (FR-030).
    KEY ix_expenses_date (expense_date_utc),
    KEY ix_expenses_category (category_id, expense_date_utc),

    CONSTRAINT fk_expenses_category
        FOREIGN KEY (category_id) REFERENCES expense_categories (id) ON DELETE RESTRICT,
    CONSTRAINT fk_expenses_user
        FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE RESTRICT,

    CONSTRAINT ck_expenses_amount_positive CHECK (amount > 0)
) ENGINE = InnoDB
  DEFAULT CHARSET = utf8mb4
  COLLATE = utf8mb4_unicode_ci;
