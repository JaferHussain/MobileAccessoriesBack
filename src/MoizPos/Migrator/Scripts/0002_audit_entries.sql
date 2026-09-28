-- 0002 — Audit trail (data-model.md §16, FR-041)
--
-- Append-only: every stock and ledger/balance change records who changed what, from what value
-- to what value, and when. Rows are written INSIDE the same transaction as the mutation they
-- describe, so a rolled-back change can never leave an audit row claiming it happened
-- (data-model.md invariant 5).

CREATE TABLE audit_entries (
    id              BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    entity_type     VARCHAR(50)     NOT NULL,
    entity_id       BIGINT UNSIGNED NOT NULL,
    field_name      VARCHAR(50)     NOT NULL,
    old_value       VARCHAR(100)    NULL,
    new_value       VARCHAR(100)    NULL,
    -- What the user was doing: Sale, Purchase, Payment, SaleReturn, Adjustment, ...
    action          VARCHAR(50)     NOT NULL,
    user_id         BIGINT UNSIGNED NOT NULL,
    occurred_at_utc DATETIME(6)     NOT NULL,

    PRIMARY KEY (id),
    KEY ix_audit_occurred (occurred_at_utc),
    KEY ix_audit_entity (entity_type, entity_id, occurred_at_utc),
    KEY ix_audit_user (user_id, occurred_at_utc),

    -- RESTRICT, not CASCADE: deleting a user must never erase the record of what they did.
    CONSTRAINT fk_audit_user
        FOREIGN KEY (user_id) REFERENCES users (id)
        ON DELETE RESTRICT
) ENGINE = InnoDB
  DEFAULT CHARSET = utf8mb4
  COLLATE = utf8mb4_unicode_ci;
