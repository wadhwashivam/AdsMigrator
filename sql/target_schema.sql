-- MySQL target schema for the Advantage -> MySQL migration.
-- Re-runnable: drops children first, then parents, then recreates in dependency order.

DROP TABLE IF EXISTS order_items;
DROP TABLE IF EXISTS orders;
DROP TABLE IF EXISTS products;
DROP TABLE IF EXISTS customers;

-- utf8mb4_0900_ai_ci is case-insensitive, which preserves Advantage CICharacter behaviour
CREATE TABLE customers (
  cust_id      INT            NOT NULL AUTO_INCREMENT,  -- original AutoInc IDs are kept
  cust_code    VARCHAR(10)    NOT NULL,                 -- CHAR(10) padded -> trimmed
  name         VARCHAR(50)    NOT NULL,
  email        VARCHAR(255)   NULL,
  phone        VARCHAR(20)    NULL,
  is_active    TINYINT(1)     NOT NULL DEFAULT 1,       -- Logical 'T'/'F' -> 1/0
  created_on   DATE           NULL,                     -- invalid legacy dates -> NULL (logged)
  credit_limit DECIMAL(19,4)  NOT NULL DEFAULT 0,       -- money: never a float
  notes        TEXT           NULL,                     -- Memo
  PRIMARY KEY (cust_id),
  UNIQUE KEY uq_customers_code (cust_code)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE products (
  prod_id      INT            NOT NULL AUTO_INCREMENT,
  sku          VARCHAR(12)    NOT NULL,
  description  VARCHAR(60)    NOT NULL,
  unit_price   DECIMAL(19,4)  NOT NULL,
  discontinued TINYINT(1)     NOT NULL DEFAULT 0,
  PRIMARY KEY (prod_id),
  UNIQUE KEY uq_products_sku (sku),
  CONSTRAINT chk_products_price CHECK (unit_price >= 0)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE orders (
  order_id     INT            NOT NULL AUTO_INCREMENT,
  cust_id      INT            NOT NULL,
  order_date   DATE           NOT NULL,
  ship_date    DATE           NULL,
  status       CHAR(1)        NOT NULL,                 -- 'O' open, 'S' shipped, 'C' cancelled
  total        DECIMAL(19,4)  NOT NULL DEFAULT 0,
  PRIMARY KEY (order_id),
  CONSTRAINT fk_orders_customer FOREIGN KEY (cust_id) REFERENCES customers (cust_id),
  CONSTRAINT chk_orders_status CHECK (status IN ('O', 'S', 'C'))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE order_items (
  item_id      INT            NOT NULL AUTO_INCREMENT,
  order_id     INT            NOT NULL,
  prod_id      INT            NOT NULL,
  qty          INT            NOT NULL,
  unit_price   DECIMAL(19,4)  NOT NULL,
  line_total   DECIMAL(19,4)  NOT NULL,
  PRIMARY KEY (item_id),
  CONSTRAINT fk_items_order   FOREIGN KEY (order_id) REFERENCES orders (order_id),
  CONSTRAINT fk_items_product FOREIGN KEY (prod_id)  REFERENCES products (prod_id),
  CONSTRAINT chk_items_qty CHECK (qty > 0)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- InnoDB automatically indexes foreign key columns (cust_id, order_id, prod_id),
-- so no extra indexes are needed for the joins used in verification.
