using Microsoft.EntityFrameworkCore;

namespace Pasto.IntegrationTests.Persistence;

// A test-only mapping: production persistence and migrations are outside S1-01.
internal sealed class RoomTestDbContext(DbContextOptions<RoomTestDbContext> options)
    : DbContext(options)
{
    public DbSet<RoomRecord> Rooms => Set<RoomRecord>();
    public DbSet<ParticipantRecord> Participants => Set<ParticipantRecord>();
    public DbSet<ItemRecord> Items => Set<ItemRecord>();
    public DbSet<CodeReservationRecord> CodeReservations => Set<CodeReservationRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var reservation = modelBuilder.Entity<CodeReservationRecord>();
        reservation.ToTable("room_code_reservations");
        reservation.HasKey(x => x.Code);
        reservation.Property(x => x.Code).HasColumnName("code").HasMaxLength(6);
        reservation.Property(x => x.ReusableAfter).HasColumnName("reusable_after");

        var room = modelBuilder.Entity<RoomRecord>();
        room.ToTable("rooms");
        room.HasKey(x => x.Id);
        room.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        room.Property(x => x.Code).HasColumnName("code").HasMaxLength(6).IsRequired();
        room.HasIndex(x => x.Code).IsUnique();
        room.Property(x => x.CreatorParticipantId).HasColumnName("creator_participant_id");
        room.Property(x => x.CreatedAt).HasColumnName("created_at");
        room.Property(x => x.ExpiresAt).HasColumnName("expires_at");
        room.Property(x => x.DeletedAt).HasColumnName("deleted_at");
        room.Property(x => x.Version).HasColumnName("version");
        room.Property(x => x.ItemCount).HasColumnName("item_count");
        room.Property(x => x.TotalBytes).HasColumnName("total_bytes");
        room.HasOne<CodeReservationRecord>().WithMany().HasForeignKey(x => x.Code)
            .OnDelete(DeleteBehavior.NoAction);

        var participant = modelBuilder.Entity<ParticipantRecord>();
        participant.ToTable("room_participants");
        participant.HasKey(x => x.Id);
        participant.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        participant.Property(x => x.RoomId).HasColumnName("room_id");
        participant.Property(x => x.CredentialHash).HasColumnName("credential_hash").IsRequired();
        participant.Property(x => x.CreatedAt).HasColumnName("created_at");
        participant.HasAlternateKey(x => new { x.RoomId, x.Id });
        participant.HasIndex(x => x.CredentialHash).IsUnique();
        participant.HasOne<RoomRecord>().WithMany().HasForeignKey(x => x.RoomId)
            .OnDelete(DeleteBehavior.Cascade);

        room.HasOne<ParticipantRecord>().WithMany()
            .HasForeignKey(x => new { x.Id, x.CreatorParticipantId })
            .HasPrincipalKey(x => new { x.RoomId, x.Id })
            .HasConstraintName("fk_rooms_creator")
            .OnDelete(DeleteBehavior.NoAction);

        var item = modelBuilder.Entity<ItemRecord>();
        item.ToTable("clipboard_items");
        item.HasKey(x => x.Id);
        item.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        item.Property(x => x.RoomId).HasColumnName("room_id");
        item.Property(x => x.OwnerParticipantId).HasColumnName("owner_participant_id");
        item.Property(x => x.Content).HasColumnName("content").IsRequired();
        item.Property(x => x.ContentBytes).HasColumnName("content_bytes");
        item.Property(x => x.CreatedAt).HasColumnName("created_at");
        item.Property(x => x.CreatedVersion).HasColumnName("created_version");
        item.HasOne<RoomRecord>().WithMany().HasForeignKey(x => x.RoomId)
            .OnDelete(DeleteBehavior.Cascade);
        item.HasOne<ParticipantRecord>().WithMany()
            .HasForeignKey(x => new { x.RoomId, x.OwnerParticipantId })
            .HasPrincipalKey(x => new { x.RoomId, x.Id })
            .HasConstraintName("fk_items_owner")
            .OnDelete(DeleteBehavior.NoAction);
        item.HasIndex(x => new { x.RoomId, x.CreatedVersion, x.Id })
            .IsDescending(false, true, true);
        item.HasIndex(x => new { x.RoomId, x.OwnerParticipantId });
    }
}

internal sealed class RoomRecord
{
    public Guid Id { get; set; }
    public required string Code { get; set; }
    public Guid CreatorParticipantId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? DeletedAt { get; set; }
    public long Version { get; set; }
    public int ItemCount { get; set; }
    public long TotalBytes { get; set; }
}

internal sealed class ParticipantRecord
{
    public Guid Id { get; set; }
    public Guid RoomId { get; set; }
    public required byte[] CredentialHash { get; set; }
    public DateTime CreatedAt { get; set; }
}

internal sealed class ItemRecord
{
    public Guid Id { get; set; }
    public Guid RoomId { get; set; }
    public Guid OwnerParticipantId { get; set; }
    public required string Content { get; set; }
    public int ContentBytes { get; set; }
    public DateTime CreatedAt { get; set; }
    public long CreatedVersion { get; set; }
}

internal sealed class CodeReservationRecord
{
    public required string Code { get; set; }
    public DateTime ReusableAfter { get; set; }
}
