namespace Pasto.Server.Domain;

// 僅包含資料模型，不依賴 EF／HTTP。交易、授權及容量規則將由後續 use case 實作。
public sealed class Room
{
    public Guid Id { get; set; }
    public required string Code { get; set; }
    public Guid CreatorParticipantId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? DeletedAt { get; set; }
    public long Version { get; set; }
    public int ItemCount { get; set; }
    // 僅作統計，不能用來實作舊版 1.5 MiB 的房間總量拒絕條件。
    public long TotalBytes { get; set; }
}

public sealed class RoomParticipant
{
    public Guid Id { get; set; }
    public Guid RoomId { get; set; }
    // 僅存 SHA-256 雜湊；明文 Credential 不屬於持久化模型。
    public required byte[] CredentialHash { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class ClipboardItem
{
    public Guid Id { get; set; }
    public Guid RoomId { get; set; }
    public Guid OwnerParticipantId { get; set; }
    public required string Content { get; set; }
    public int ContentBytes { get; set; }
    public DateTime CreatedAt { get; set; }
    public long CreatedVersion { get; set; }
}

public sealed class RoomCodeReservation
{
    public required string Code { get; set; }
    public DateTime ReusableAfter { get; set; }
}
