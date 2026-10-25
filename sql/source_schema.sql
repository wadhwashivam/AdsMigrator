CREATE TABLE customers (
  cust_id      INTEGER PRIMARY KEY,  -- AutoInc
  cust_code    TEXT,                 -- CHAR(10), space-padded
  name         TEXT,                 -- CHAR(50), space-padded
  email        TEXT,
  phone        TEXT,
  is_active    TEXT,                 -- Logical: 'T'/'F'
  created_on   TEXT,                 -- Date as text, some invalid
  credit_limit REAL,                 -- Money stored as float
  notes        TEXT                  -- Memo, often NULL
);

CREATE TABLE products (
  prod_id      INTEGER PRIMARY KEY,
  sku          TEXT,                 -- CHAR(12), padded
  description  TEXT,                 -- CHAR(60), padded
  unit_price   REAL,
  discontinued TEXT                  -- 'T'/'F'
);

CREATE TABLE orders (
  order_id   INTEGER PRIMARY KEY,
  cust_id    INTEGER,                -- a few point to missing customers
  order_date TEXT,
  ship_date  TEXT,                   -- NULL, empty, or invalid sometimes
  status     TEXT,                   -- 'O' open, 'S' shipped, 'C' cancelled
  total      REAL
);

CREATE TABLE order_items (
  item_id    INTEGER PRIMARY KEY,
  order_id   INTEGER,
  prod_id    INTEGER,
  qty        INTEGER,
  unit_price REAL,
  line_total REAL
);