using Microsoft.EntityFrameworkCore;
using Pasto.Server.Domain;

namespace Pasto.Server.Infrastructure.Persistence;

// 正式模型與 Migration snapshot 共用此 mapping；PostgreSQL 的延後 FK 檢查另由 Migration 設定。
public sealed class PastoDbContext(DbContextOptions<PastoDbContext> options)
    : DbContext(options)
{
    public DbSet<Room> Rooms => Set<Room>();
    public DbSet<RoomParticipant> Participants => Set<RoomParticipant>();
    public DbSet<ClipboardItem> Items => Set<ClipboardItem>();
    public DbSet<RoomCodeReservation> CodeReservations => Set<RoomCodeReservation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var reservation = modelBuilder.Entity<RoomCodeReservation>();
        reservation.ToTable("room_code_reservations");
        reservation.HasKey(x => x.Code);
        reservation.Property(x => x.Code).HasColumnName("code").HasMaxLength(6);
        reservation.Property(x => x.ReusableAfter).HasColumnName("reusable_after");
        reservation.HasIndex(x => new { x.ReusableAfter, x.Code }).HasDatabaseName("ix_code_reuse");
        reservation.ToTable(t => t.HasCheckConstraint("ck_code_format", "code ~ '^[A-Z0-9]{6}$'"));

        var room = modelBuilder.Entity<Room>();
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
        // 模型記錄 CHECK／部分索引，讓後續 Migration diff 能追蹤它們。
        // 初始 Migration 以明確 SQL 建立；TotalBytes 僅需非負，不設總量硬上限。
        room.Property(x => x.Version).HasDefaultValue(0L);
        room.Property(x => x.ItemCount).HasDefaultValue(0);
        room.Property(x => x.TotalBytes).HasDefaultValue(0L);
        room.ToTable(t =>
        {
            t.HasCheckConstraint("ck_rooms_expiry", "expires_at > created_at");
            t.HasCheckConstraint("ck_rooms_version", "version >= 0");
            t.HasCheckConstraint("ck_rooms_count", "item_count BETWEEN 0 AND 300");
            t.HasCheckConstraint("ck_rooms_bytes", "total_bytes >= 0");
        });
        room.HasIndex(x => new { x.ExpiresAt, x.Id }).HasFilter("deleted_at IS NULL")
            .HasDatabaseName("ix_rooms_expiry");
        room.HasIndex(x => new { x.DeletedAt, x.Id }).HasFilter("deleted_at IS NOT NULL")
            .HasDatabaseName("ix_rooms_deleted");
        room.HasOne<RoomCodeReservation>().WithMany().HasForeignKey(x => x.Code)
            .OnDelete(DeleteBehavior.NoAction);

        var participant = modelBuilder.Entity<RoomParticipant>();
        participant.ToTable("room_participants");
        participant.HasKey(x => x.Id);
        participant.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        participant.Property(x => x.RoomId).HasColumnName("room_id");
        participant.Property(x => x.CredentialHash).HasColumnName("credential_hash").IsRequired();
        participant.Property(x => x.CreatedAt).HasColumnName("created_at");
        participant.ToTable(t => t.HasCheckConstraint("ck_participants_hash", "octet_length(credential_hash) = 32"));
        participant.HasAlternateKey(x => new { x.RoomId, x.Id });
        participant.HasIndex(x => x.CredentialHash).IsUnique();
        participant.HasOne<Room>().WithMany().HasForeignKey(x => x.RoomId)
            .OnDelete(DeleteBehavior.Cascade);

        // 複合鍵保證 Creator 同房；必須保持 NO ACTION，Migration 再加 deferred 檢查。
        room.HasOne<RoomParticipant>().WithMany()
            .HasForeignKey(x => new { x.Id, x.CreatorParticipantId })
            .HasPrincipalKey(x => new { x.RoomId, x.Id })
            .HasConstraintName("fk_rooms_creator")
            .OnDelete(DeleteBehavior.NoAction);

        var item = modelBuilder.Entity<ClipboardItem>();
        item.ToTable("clipboard_items");
        item.HasKey(x => x.Id);
        item.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        item.Property(x => x.RoomId).HasColumnName("room_id");
        item.Property(x => x.OwnerParticipantId).HasColumnName("owner_participant_id");
        item.Property(x => x.Content).HasColumnName("content").IsRequired();
        item.Property(x => x.ContentBytes).HasColumnName("content_bytes");
        item.Property(x => x.CreatedAt).HasColumnName("created_at");
        item.Property(x => x.CreatedVersion).HasColumnName("created_version");
        item.ToTable(t =>
        {
            t.HasCheckConstraint("ck_items_bytes", "content_bytes BETWEEN 1 AND 8192 AND content_bytes = octet_length(content)");
            t.HasCheckConstraint("ck_items_version", "created_version > 0");
        });
        item.HasOne<Room>().WithMany().HasForeignKey(x => x.RoomId)
            .OnDelete(DeleteBehavior.Cascade);
        item.HasOne<RoomParticipant>().WithMany()
            .HasForeignKey(x => new { x.RoomId, x.OwnerParticipantId })
            .HasPrincipalKey(x => new { x.RoomId, x.Id })
            .HasConstraintName("fk_items_owner")
            .OnDelete(DeleteBehavior.NoAction);
        item.HasIndex(x => new { x.RoomId, x.CreatedVersion, x.Id })
            .IsDescending(false, true, true).HasDatabaseName("ix_items_order");
        item.HasIndex(x => new { x.RoomId, x.OwnerParticipantId }).HasDatabaseName("ix_items_owner");
    }
}
