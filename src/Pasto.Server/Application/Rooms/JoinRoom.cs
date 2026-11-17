using Pasto.Server.Domain;

namespace Pasto.Server.Application.Rooms;

public interface IRoomJoinStore
{
    Task<(Room Room, Guid ParticipantId)> JoinAsync(string code, byte[] credentialHash, CancellationToken cancellationToken);
}
public sealed class InvalidRoomCodeException() : Exception("Room code is invalid.");
public sealed class JoinRoom(IRoomJoinStore store, IRoomSecrets secrets)
{
    public async Task<JoinedRoom> ExecuteAsync(string? code, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = code?.Trim().ToUpperInvariant();
        if (normalized is null || normalized.Length != 6 || normalized.Any(c => c is not (>= 'A' and <= 'Z' or >= '0' and <= '9')))
            throw new InvalidRoomCodeException();
        var credential = secrets.GenerateCredential();
        var joined = await store.JoinAsync(normalized, credential.Hash, cancellationToken);
        return new JoinedRoom(joined.Room, joined.ParticipantId, credential.Value);
    }
}
// 含憑證的結果不使用 record，避免 ToString 洩漏。
public sealed class JoinedRoom(Room room, Guid participantId, string credential)
{
    public Guid RoomId { get; } = room.Id;
    public Guid ParticipantId { get; } = participantId;
    public string Code { get; } = room.Code;
    public DateTime ExpiresAt { get; } = room.ExpiresAt;
    public long Version { get; } = room.Version;
    public string Credential { get; } = credential;
}
