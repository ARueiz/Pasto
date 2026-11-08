using System.Security.Cryptography;
using System.Text;
using Pasto.Server.Application.Rooms;

namespace Pasto.Server.Infrastructure.Security;

public sealed class RoomSecrets : IRoomSecrets
{
    // GetString 使用 CSPRNG 均勻選字元，避免自己做 modulo 而產生分布偏差。
    public string GenerateCode() => RandomNumberGenerator.GetString("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789", 6);

    public ParticipantCredential GenerateCredential()
    {
        // 16 個隨機 bytes = 128 bits；Base64URL 無 padding，適合後續 Bearer 傳輸。
        var value = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        // 對傳給 Client 的原始字串之 UTF-8 bytes 取 SHA-256；後續驗證必須採相同編碼。
        return new ParticipantCredential(value, SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}
