-- 0032 — The team: what each person's job is, and when they signed in.
--
-- The shop has one owner (Admin) and staff who do different jobs: a shopkeeper at the counter
-- beside the owner, and a salesman who sells in the market. Both keep the Staff role — the same
-- protections, never a sight of cost or profit — and `job` says which work they do, so the Team
-- screen, and later the salesman's commission and the stock he carries, can tell them apart.
-- A new job later is a new ENUM member, not a new role.

ALTER TABLE users
    ADD COLUMN job ENUM('Counter', 'FieldSales') NULL AFTER role;

-- Every member of staff so far worked at the counter. The owner has no job: the owner is the owner.
UPDATE users SET job = 'Counter' WHERE role = 'Staff';

-- When each person signed in, from where. Written on every successful sign-in, never changed.
-- A token refresh is not a sign-in and is not recorded, or one working day would read as hundreds.
CREATE TABLE user_logins (
    id               BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    user_id          BIGINT UNSIGNED NOT NULL,
    logged_in_at_utc DATETIME(6)     NOT NULL,
    ip_address       VARCHAR(45)     NULL,
    user_agent       VARCHAR(255)    NULL,

    PRIMARY KEY (id),
    KEY ix_user_logins_user (user_id, logged_in_at_utc),

    CONSTRAINT fk_user_logins_user
        FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE RESTRICT
) ENGINE = InnoDB
  DEFAULT CHARSET = utf8mb4
  COLLATE = utf8mb4_unicode_ci;
