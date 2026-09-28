-- 0012 — Share tokens for invoices and receipts (data-model.md §17, FR-044)
--
-- These back the ONE unauthenticated endpoint in the system. A shop customer holds no
-- credential and never will, and wa.me cannot carry a file attachment (research.md R2), so a
-- receipt reaches them as a link to a tokenized URL.
--
-- Containment, per the justified exception in plan.md:
--   * the token is >= 128 bits of cryptographic randomness
--   * only its SHA-256 hash is stored, so a database leak does not hand over live links
--   * it resolves to exactly one document — no listing, no lookup by invoice number
--   * it expires, and can be revoked
--   * every access is counted and timestamped

CREATE TABLE document_tokens (
    id                BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    token_hash        CHAR(64)        NOT NULL,
    document_type     ENUM('Invoice', 'PaymentReceipt') NOT NULL,
    reference_id      BIGINT UNSIGNED NOT NULL,
    expires_at_utc    DATETIME(6)     NOT NULL,
    revoked_at_utc    DATETIME(6)     NULL,
    last_accessed_utc DATETIME(6)     NULL,
    access_count      INT             NOT NULL DEFAULT 0,
    created_by_user_id BIGINT UNSIGNED NOT NULL,
    created_at_utc    DATETIME(6)     NOT NULL,

    PRIMARY KEY (id),
    UNIQUE KEY uq_document_tokens_hash (token_hash),
    -- Lets a re-share reuse a live token instead of minting a new one every time.
    KEY ix_document_tokens_reference (document_type, reference_id, expires_at_utc),

    CONSTRAINT fk_document_tokens_user
        FOREIGN KEY (created_by_user_id) REFERENCES users (id) ON DELETE RESTRICT
) ENGINE = InnoDB
  DEFAULT CHARSET = utf8mb4
  COLLATE = utf8mb4_unicode_ci;
