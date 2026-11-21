namespace Pasto.Server.Application.Rooms;

public sealed record ParticipantIdentity(Guid ParticipantId, Guid RoomId);
public interface IParticipantVerifier
{
    // 只辨識身分，不查房間有效性。REST 與 S5 Hub 共用。
    Task<ParticipantIdentity?> VerifyAsync(byte[] credentialHash, CancellationToken cancellationToken);
}
public interface IParticipantRecoveryStore
{
    Task<RecoveredParticipant> GetAsync(Guid roomId, Guid participantId, CancellationToken cancellationToken);
}
public sealed record RecoveredParticipant(Guid RoomId, Guid ParticipantId, bool IsCreator);
public sealed class GetParticipant(IParticipantRecoveryStore store)
{
    public Task<RecoveredParticipant> ExecuteAsync(Guid roomId, Guid participantId, CancellationToken cancellationToken)
        => store.GetAsync(roomId, participantId, cancellationToken);
}
