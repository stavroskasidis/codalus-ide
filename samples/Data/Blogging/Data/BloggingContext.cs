using Microsoft.EntityFrameworkCore;

namespace Blogging.Data;

/// <summary>The fixture's model: a blog with an owned owner, posts, and tags on posts (many-to-many).</summary>
public sealed class BloggingContext(DbContextOptions<BloggingContext> options) : DbContext(options)
{
    public DbSet<Blog> Blogs => Set<Blog>();

    public DbSet<Post> Posts => Set<Post>();

    public DbSet<Tag> Tags => Set<Tag>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Blog>(blog =>
        {
            blog.Property(b => b.Url).IsRequired().HasMaxLength(200);
            blog.HasIndex(b => b.Url).IsUnique();
            blog.OwnsOne(b => b.Owner);
        });

        modelBuilder.Entity<Post>(post =>
        {
            post.Property(p => p.Title).IsRequired().HasMaxLength(120);
            post.HasOne(p => p.Blog).WithMany(b => b.Posts).HasForeignKey(p => p.BlogId).OnDelete(DeleteBehavior.Cascade);
            post.HasMany(p => p.Tags).WithMany(t => t.Posts);
        });
    }
}

public sealed class Blog
{
    public int Id { get; set; }

    public string Url { get; set; } = string.Empty;

    public string? Name { get; set; }

    public Person Owner { get; set; } = new();

    public List<Post> Posts { get; } = [];
}

/// <summary>An owned type: stored in the Blogs table.</summary>
public sealed class Person
{
    public string Name { get; set; } = string.Empty;

    public string? Email { get; set; }
}

public sealed class Post
{
    public int Id { get; set; }

    public string Title { get; set; } = string.Empty;

    public string? Content { get; set; }

    public DateTime Published { get; set; }

    public int BlogId { get; set; }

    public Blog Blog { get; set; } = null!;

    public List<Tag> Tags { get; } = [];
}

public sealed class Tag
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public List<Post> Posts { get; } = [];
}
