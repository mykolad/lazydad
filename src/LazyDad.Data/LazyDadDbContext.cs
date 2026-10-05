using LazyDad.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace LazyDad.Data;

public class LazyDadDbContext : DbContext
{
    public LazyDadDbContext(DbContextOptions<LazyDadDbContext> options) : base(options) { }

    public DbSet<Joke> Jokes => Set<Joke>();
    public DbSet<SchedulerLock> SchedulerLocks => Set<SchedulerLock>();
    public DbSet<TopJoke> TopJokes => Set<TopJoke>();
    public DbSet<JokeProfile> JokeProfiles => Set<JokeProfile>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Joke>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Language).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Text).HasMaxLength(2000).IsRequired();
            entity.Property(e => e.Explanation).HasMaxLength(500);
            // Index speeds up the common query: get jokes by language
            entity.HasIndex(e => e.Language);
            // The feed's default order and its keyset cursor (newest first, ties by id).
            // "Top voted" sorts on Up - Down without an index: every vote would have to maintain
            // one, and the table (a dozen jokes a day) is cheap to sort.
            entity.HasIndex(e => new { e.GeneratedAt, e.Id });
        });

        modelBuilder.Entity<TopJoke>(entity =>
        {
            // One row per (language, rank) slot — a leaderboard can't have two #1s.
            entity.HasKey(e => new { e.Language, e.Rank });
            entity.Property(e => e.Language).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Reason).HasMaxLength(500).IsRequired();
            entity.Property(e => e.JudgeModel).HasMaxLength(100).IsRequired();
            // A joke can hold at most one slot.
            entity.HasIndex(e => e.JokeId).IsUnique();
            entity.HasOne(e => e.Joke)
                .WithMany()
                .HasForeignKey(e => e.JokeId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<JokeProfile>(entity =>
        {
            // One profile per joke and method; a new version replaces the old row.
            entity.HasKey(e => new { e.JokeId, e.Kind });
            entity.Property(e => e.Kind).HasMaxLength(20).IsRequired();
            entity.Property(e => e.Version).HasMaxLength(100).IsRequired();
            // A Jev profile is ~30 floats, an embedding 512 (2 KB).
            entity.Property(e => e.Vector).HasMaxLength(8000).IsRequired();
            entity.HasOne(e => e.Joke)
                .WithMany()
                .HasForeignKey(e => e.JokeId)
                .OnDelete(DeleteBehavior.Cascade);
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
