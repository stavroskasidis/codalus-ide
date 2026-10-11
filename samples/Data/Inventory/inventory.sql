-- The reverse-engineering source (M27): an inventory database, written for SQLite and kept portable.
-- inventory.db is this script applied to an empty SQLite database (samples/Data/README.md says how to regenerate it).

CREATE TABLE Categories (
    CategoryId INTEGER NOT NULL PRIMARY KEY,
    Name TEXT NOT NULL,
    Description TEXT NULL
);

CREATE TABLE Suppliers (
    SupplierId INTEGER NOT NULL PRIMARY KEY,
    CompanyName TEXT NOT NULL,
    ContactEmail TEXT NULL,
    Country TEXT NULL
);

CREATE TABLE Products (
    ProductId INTEGER NOT NULL PRIMARY KEY,
    Name TEXT NOT NULL,
    Sku TEXT NOT NULL UNIQUE,
    CategoryId INTEGER NOT NULL REFERENCES Categories (CategoryId) ON DELETE CASCADE,
    SupplierId INTEGER NULL REFERENCES Suppliers (SupplierId),
    UnitPrice NUMERIC NOT NULL DEFAULT 0,
    UnitsInStock INTEGER NOT NULL DEFAULT 0,
    Discontinued INTEGER NOT NULL DEFAULT 0
);

CREATE INDEX IX_Products_CategoryId ON Products (CategoryId);

CREATE TABLE StockMovements (
    MovementId INTEGER NOT NULL PRIMARY KEY,
    ProductId INTEGER NOT NULL REFERENCES Products (ProductId),
    Quantity INTEGER NOT NULL,
    MovedAt TEXT NOT NULL,
    Note TEXT NULL
);

CREATE VIEW ProductsInStock AS
    SELECT p.ProductId, p.Name, c.Name AS Category, p.UnitsInStock
    FROM Products p JOIN Categories c ON c.CategoryId = p.CategoryId
    WHERE p.UnitsInStock > 0 AND p.Discontinued = 0;

INSERT INTO Categories (CategoryId, Name, Description) VALUES
    (1, 'Beverages', 'Soft drinks, coffees, teas'),
    (2, 'Condiments', 'Sauces, relishes, spreads'),
    (3, 'Produce', 'Dried fruit and bean curd');

INSERT INTO Suppliers (SupplierId, CompanyName, ContactEmail, Country) VALUES
    (1, 'Exotic Liquids', 'orders@exotic.example', 'UK'),
    (2, 'Grandma Kelly''s Homestead', NULL, 'USA');

INSERT INTO Products (ProductId, Name, Sku, CategoryId, SupplierId, UnitPrice, UnitsInStock, Discontinued) VALUES
    (1, 'Chai', 'BEV-001', 1, 1, 18.00, 39, 0),
    (2, 'Chang', 'BEV-002', 1, 1, 19.00, 17, 0),
    (3, 'Aniseed Syrup', 'CON-001', 2, 1, 10.00, 13, 0),
    (4, 'Boysenberry Spread', 'CON-002', 2, 2, 25.00, 120, 0),
    (5, 'Dried Pears', 'PRO-001', 3, 2, 30.00, 0, 1);

INSERT INTO StockMovements (MovementId, ProductId, Quantity, MovedAt, Note) VALUES
    (1, 1, 40, '2026-10-01T09:00:00Z', 'Initial stock'),
    (2, 1, -1, '2026-10-02T14:30:00Z', NULL),
    (3, 4, 120, '2026-10-03T08:15:00Z', 'Initial stock');
