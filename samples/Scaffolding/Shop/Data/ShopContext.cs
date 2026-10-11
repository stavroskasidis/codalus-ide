using Microsoft.EntityFrameworkCore;
using Shop.Models;

namespace Shop.Data;

public class ShopContext(DbContextOptions<ShopContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
}
