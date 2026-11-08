using Pasto.Server.Domain;

namespace Pasto.Server.Application.Rooms;

// 此介面只描述一次建房的原子寫入，不暴露 EF／SQL，也不提供通用 CRUD。
public interface IRoomCreationStore
{
    // null 僅代表保留碼已被使用；其他失敗一律拋出，避免誤當碰撞而重送。
    Task<Room?> TryCreateAsync(string code, byte[] credentialHash, CancellationToken cancellationToken);
}

public interface IRoomSecrets
{
    string GenerateCode();
    ParticipantCredential GenerateCredential();
}

// 含明文憑證的型別不用 record，避免自動產生的 ToString 將秘密寫進日誌。
public sealed class ParticipantCredential(string value, byte[] hash)
{
    public string Value { get; } = value;
    public byte[] Hash { get; } = hash;
}

public sealed class CreatedRoom(Room room, string credential)
{
    public Guid RoomId { get; } = room.Id;
    public Guid ParticipantId { get; } = room.CreatorParticipantId;
    public string Code { get; } = room.Code;
    public DateTime CreatedAt { get; } = room.CreatedAt;
    public DateTime ExpiresAt { get; } = room.ExpiresAt;
    public long Version { get; } = room.Version;
    public string Credential { get; } = credential;
}

public sealed class CreateRoomOptions
{
    // 只計算房碼嘗試，預設 5 次；上限 20 避免錯誤設定造成大量重試。
    public int MaxCodeAttempts { get; set; } = 5;
}

// S2 的 HTTP 層再對應為 503；Application 不依賴 HTTP 狀態碼。
public sealed class RoomCodeAllocationException()
    : Exception("Room code allocation attempts were exhausted.");
