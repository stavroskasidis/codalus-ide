# Data fixture

The data and Entity Framework Core fixture (M27, ADRs 0036–0038).

- **`Blogging/`**: an ASP.NET Core app with an EF Core context (`BloggingContext`) on SQLite, configured in `Program.cs` with `UseSqlite(GetConnectionString("Blogging"))`. The model has a blog with an owned `Person`, posts with a cascade delete, and tags on posts (many-to-many through a join entity). `Migrations/` holds the `InitialCreate` migration. `appsettings.json` and `appsettings.Development.json` hold the connection strings (`Data Source=blogging.db` and `blogging-test.db`); the project has a `UserSecretsId` for the user-secrets tests.
- **`.config/dotnet-tools.json`**: `dotnet-ef` 10.0.12 as a local tool. Run `dotnet tool restore` in this folder first.
- **`Inventory/`**: the reverse-engineering source, `inventory.db` (SQLite), made from `inventory.sql` (categories, suppliers, products with foreign keys and a unique index, stock movements, and a view). To regenerate it: `python -I -c "import sqlite3; c = sqlite3.connect('inventory.db'); c.executescript(open('inventory.sql').read()); c.commit()"` in `Inventory/` after deleting the old file.

The tests copy the fixture to a temporary folder before changing anything.
