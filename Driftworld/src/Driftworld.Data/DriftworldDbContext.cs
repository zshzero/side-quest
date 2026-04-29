using Driftworld.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Driftworld.Data;

public sealed class DriftworldDbContext : DbContext
{
    public DriftworldDbContext(DbContextOptions<DriftworldDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<Cycle> Cycles => Set<Cycle>();
    public DbSet<Decision> Decisions => Set<Decision>();
    public DbSet<WorldState> WorldStates => Set<WorldState>();
    public DbSet<Event> Events => Set<Event>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        var cycleStatusConverter = new ValueConverter<CycleStatus, string>(
            v => v == CycleStatus.Open ? "open" : "closed",
            v => v == "open" ? CycleStatus.Open : CycleStatus.Closed);

        b.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Handle).HasColumnName("handle").HasMaxLength(32);
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone");
            e.Property(x => x.LastSeenAt).HasColumnName("last_seen_at").HasColumnType("timestamp with time zone");
            e.HasIndex(x => x.Handle)
                .IsUnique()
                .HasDatabaseName("ix_users_handle")
                .HasFilter("handle IS NOT NULL");
        });

        b.Entity<Cycle>(e =>
        {
            e.ToTable("cycles", t => t.HasCheckConstraint("ck_cycles_status", "status IN ('open','closed')"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
            e.Property(x => x.StartsAt).HasColumnName("starts_at").HasColumnType("timestamp with time zone");
            e.Property(x => x.EndsAt).HasColumnName("ends_at").HasColumnType("timestamp with time zone");
            e.Property(x => x.Status)
                .HasColumnName("status")
                .HasConversion(cycleStatusConverter)
                .HasColumnType("text");
            e.Property(x => x.ClosedAt).HasColumnName("closed_at").HasColumnType("timestamp with time zone");

            e.HasIndex(x => x.Status)
                .IsUnique()
                .HasDatabaseName("ix_one_open_cycle")
                .HasFilter("status = 'open'");
        });

        b.Entity<Decision>(e =>
        {
            e.ToTable("decisions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.CycleId).HasColumnName("cycle_id");
            e.Property(x => x.Choice).HasColumnName("choice").HasMaxLength(32).IsRequired();
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone");

            e.HasOne(x => x.User)
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(x => x.Cycle)
                .WithMany()
                .HasForeignKey(x => x.CycleId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasIndex(x => new { x.UserId, x.CycleId })
                .IsUnique()
                .HasDatabaseName("ux_decisions_user_cycle");

            e.HasIndex(x => x.CycleId).HasDatabaseName("ix_decisions_cycle");
        });

        b.Entity<WorldState>(e =>
        {
            e.ToTable("world_states", t =>
            {
                t.HasCheckConstraint("ck_world_states_economy_range", "economy BETWEEN 0 AND 100");
                t.HasCheckConstraint("ck_world_states_environment_range", "environment BETWEEN 0 AND 100");
                t.HasCheckConstraint("ck_world_states_stability_range", "stability BETWEEN 0 AND 100");
                t.HasCheckConstraint("ck_world_states_participants_nonneg", "participants >= 0");
            });
            e.HasKey(x => x.CycleId);
            e.Property(x => x.CycleId).HasColumnName("cycle_id").ValueGeneratedNever();
            e.Property(x => x.Economy).HasColumnName("economy").HasColumnType("smallint");
            e.Property(x => x.Environment).HasColumnName("environment").HasColumnType("smallint");
            e.Property(x => x.Stability).HasColumnName("stability").HasColumnType("smallint");
            e.Property(x => x.Participants).HasColumnName("participants");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone");

            e.HasOne(x => x.Cycle)
                .WithOne()
                .HasForeignKey<WorldState>(x => x.CycleId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Event>(e =>
        {
            e.ToTable("events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.CycleId).HasColumnName("cycle_id");
            e.Property(x => x.Type).HasColumnName("type").HasMaxLength(64);
            e.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone");

            e.HasOne(x => x.Cycle)
                .WithMany()
                .HasForeignKey(x => x.CycleId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasIndex(x => new { x.CycleId, x.Type })
                .IsUnique()
                .HasDatabaseName("ux_events_cycle_type");
        });
    }
}
