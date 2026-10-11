# Scaffolding fixture

For the scaffolding and service reference tests (M15c, ADR 0041).

- `Shop`: an ASP.NET Core app with EF Core on SQLite (`Data/ShopContext.cs`, `Models/Product.cs`) and `Microsoft.VisualStudio.Web.CodeGeneration.Design`, for `dotnet aspnet-codegenerator` (a local tool in `.config/dotnet-tools.json`; the tests restore it).
- `Client`: a console app that the tests add an OpenAPI reference and a gRPC reference to.
- `contracts/`: the documents they reference: `weather.openapi.json` (OpenAPI 3) and `greet.proto`.
