using LazyDad.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace LazyDad.Data;

public class LazyDadDbContext : DbContext
{
    public LazyDadDbContext(DbContextOptions<LazyDadDbContext> options) : base(options) { }

    public DbSet<Joke> Jokes => Set<Joke>();
    public DbSet<SchedulerLock> SchedulerLocks => Set<SchedulerLock>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Joke>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Language).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Text).HasMaxLength(2000).IsRequired();
            // Index speeds up the common query: get jokes by language
            entity.HasIndex(e => e.Language);
        });

        modelBuilder.Entity<SchedulerLock>(entity =>
        {
            // LockKey is the primary key — inserting a duplicate throws,
            // which is exactly how we detect that another pod won the race.
            entity.HasKey(e => e.LockKey);
            entity.Property(e => e.LockKey).HasMaxLength(100).IsRequired();
            entity.Property(e => e.HolderInstanceId).HasMaxLength(100).IsRequired();
        });
    }
}
