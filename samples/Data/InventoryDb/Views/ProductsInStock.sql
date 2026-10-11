CREATE VIEW [dbo].[ProductsInStock]
AS
SELECT p.[ProductId], p.[Name], c.[Name] AS [Category], p.[UnitsInStock]
FROM [dbo].[Products] p
JOIN [dbo].[Categories] c ON c.[CategoryId] = p.[CategoryId]
WHERE p.[UnitsInStock] > 0;
