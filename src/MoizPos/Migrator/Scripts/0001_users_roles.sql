-- 0001 — Users, roles and refresh tokens (data-model.md §1–2)
-- Serves FR-038 (authenticate before any business data) and FR-039 (Admin / Staff).

CREATE TABLE users (
    id              BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    username        VARCHAR(50)     NOT NULL,
    full_name       VARCHAR(100)    NOT NULL,
    password_hash   VARCHAR(255)    NOT NULL,
    role            ENUM('Admin', 'Staff') NOT NULL,
    is_active       BOOLEAN         NOT NULL DEFAULT TRUE,
    created_at_utc  DATETIME(6)     NOT NULL,
    updated_at_utc  DATETIME(6)     NULL,

    PRIMARY KEY (id),
    -- Usernames are compared case-insensitively by the column collation.
    UNIQUE KEY uq_users_username (username)
) ENGINE = InnoDB
  DEFAULT CHARSET = utf8mb4
  COLLATE = utf8mb4_unicode_ci;


-- Refresh tokens are persisted (rather than left stateless) so a departed employee can be
-- revoked immediately instead of staying valid until expiry (research.md R9).
CREATE TABLE refresh_tokens (
    id              BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    user_id         BIGINT UNSIGNED NOT NULL,
    -- Only the SHA-256 hash is stored; the raw token exists solely in the client.
    token_hash      CHAR(64)        NOT NULL,
    expires_at_utc  DATETIME(6)     NOT NULL,
    revoked_at_utc  DATETIME(6)     NULL,
    created_at_utc  DATETIME(6)     NOT NULL,

    PRIMARY KEY (id),
    UNIQUE KEY uq_refresh_tokens_hash (token_hash),
    KEY ix_refresh_tokens_user (user_id, expires_at_utc),

    CONSTRAINT fk_refresh_tokens_user
        FOREIGN KEY (user_id) REFERENCES users (id)
        ON DELETE CASCADE
) ENGINE = InnoDB
  DEFAULT CHARSET = utf8mb4
  COLLATE = utf8mb4_unicode_ci;
