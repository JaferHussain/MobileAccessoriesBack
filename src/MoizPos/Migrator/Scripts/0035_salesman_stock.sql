-- Stock a field salesman carries out of the shop, and brings back.
--
-- products.quantity_on_hand stays what the SHOP OWNS — goods in the salesman's bag are still the
-- shop's, so stock value, purchases and reports are untouched. salesman_stock is where those owned
-- units physically are: what the counter can sell is quantity_on_hand less everything salesmen hold.
--
-- Every change to salesman_stock happens under the product row's lock (SELECT ... FOR UPDATE on
-- products, ordered by id), the same lock every sale, purchase and return already takes.

CREATE TABLE salesman_stock (
    user_id        BIGINT UNSIGNED NOT NULL,
    product_id     BIGINT UNSIGNED NOT NULL,
    quantity       INT             NOT NULL DEFAULT 0,
    updated_at_utc DATETIME(6)     NOT NULL,

    PRIMARY KEY (user_id, product_id),
    KEY ix_salesman_stock_product (product_id),

    CONSTRAINT fk_salesman_stock_user    FOREIGN KEY (user_id)    REFERENCES users (id)    ON DELETE RESTRICT,
    CONSTRAINT fk_salesman_stock_product FOREIGN KEY (product_id) REFERENCES products (id) ON DELETE RESTRICT,
    CONSTRAINT ck_salesman_stock_non_negative CHECK (quantity >= 0)
);

-- Each unit in and out of his bag: issued by the owner, returned to the shop, sold, or brought
-- back to him by a customer. The holding above is always the sum of these.
CREATE TABLE salesman_stock_movements (
    id                  BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    user_id             BIGINT UNSIGNED NOT NULL,
    product_id          BIGINT UNSIGNED NOT NULL,
    reason              ENUM('Issued', 'Returned', 'Sold', 'CustomerReturn') NOT NULL,
    change_qty          INT             NOT NULL,
    resulting_qty       INT             NOT NULL,
    -- The invoice for Sold, the sale return for CustomerReturn; NULL for Issued and Returned.
    reference_id        BIGINT UNSIGNED NULL,
    note                VARCHAR(255)    NULL,
    recorded_by_user_id BIGINT UNSIGNED NOT NULL,
    created_at_utc      DATETIME(6)     NOT NULL,

    PRIMARY KEY (id),
    KEY ix_salesman_stock_movements_user (user_id, created_at_utc, id),

    CONSTRAINT fk_ssm_user        FOREIGN KEY (user_id)             REFERENCES users (id)    ON DELETE RESTRICT,
    CONSTRAINT fk_ssm_product     FOREIGN KEY (product_id)          REFERENCES products (id) ON DELETE RESTRICT,
    CONSTRAINT fk_ssm_recorded_by FOREIGN KEY (recorded_by_user_id) REFERENCES users (id)    ON DELETE RESTRICT,
    CONSTRAINT ck_ssm_change_non_zero       CHECK (change_qty <> 0),
    CONSTRAINT ck_ssm_resulting_non_negative CHECK (resulting_qty >= 0)
);
