using System.Security.Cryptography;

namespace Pasto.Server.Api;

internal static class ParticipantAuthorCode
{
    public static string From(Guid roomId, Guid participantId)
    {
        // 固定使用標準 UUID 的大端位元組序，先放房間 ID，再放 Participant ID。
        Span<byte> input = stackalloc byte[32];
        roomId.TryWriteBytes(input[..16], bigEndian: true, out _);
        participantId.TryWriteBytes(input[16..], bigEndian: true, out _);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);
        return Convert.ToHexString(hash[..3])[..5];
    }
}
