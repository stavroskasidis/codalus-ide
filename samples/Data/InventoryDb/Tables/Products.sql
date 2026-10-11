CREATE TABLE [dbo].[Products]
(
    [ProductId] INT IDENTITY NOT NULL PRIMARY KEY,
    [Name] NVARCHAR(100) NOT NULL,
    [Sku] NVARCHAR(20) NOT NULL UNIQUE,
    [CategoryId] INT NOT NULL REFERENCES [dbo].[Categories] ([CategoryId]),
    [UnitPrice] DECIMAL(10, 2) NOT NULL DEFAULT 0,
    [UnitsInStock] INT NOT NULL DEFAULT 0
);
