using LazyDad.Data.Entities;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace LazyDad.Data;

public class LazyDadDbContext : DbContext, IDataProtectionKeyContext
{
    public LazyDadDbContext(DbContextOptions<LazyDadDbContext> options) : base(options) { }

    public DbSet<Joke> Jokes => Set<Joke>();
    public DbSet<SchedulerLock> SchedulerLocks => Set<SchedulerLock>();
    public DbSet<TopJoke> TopJokes => Set<TopJoke>();
    public DbSet<JokeProfile> JokeProfiles => Set<JokeProfile>();
    public DbSet<Vote> Votes => Set<Vote>();
    /// <summary>
    /// ASP.NET Core's key ring, which encrypts the sign-in cookies. Kept here so both production apps (and every restart)
    /// share it: a sign-in that starts in one region can finish in the other.
    /// </summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Joke>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Language).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Text).HasMaxLength(2000).IsRequired();
            entity.Property(e => e.Explanation).HasMaxLength(500);
            entity.Property(e => e.TextHash).HasMaxLength(JokeText.HashLength).IsFixedLength();
            // "Is this joke already here" and "this joke's copies": index lookups, however many jokes there are.
            entity.HasIndex(e => new { e.Language, e.TextHash });
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

        modelBuilder.Entity<Vote>(entity =>
        {
            // One vote per joke and voter: a repeated request finds its own row instead of adding one.
            entity.HasKey(e => new { e.JokeId, e.VoterKey });
            entity.Property(e => e.VoterKey).HasMaxLength(Vote.VoterKeyLength).IsFixedLength();
            // A voter's own votes: the ones on a feed page, or all of them to delete.
            entity.HasIndex(e => e.VoterKey);
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
