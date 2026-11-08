using System.Security.Cryptography;
using System.Text;
using Pasto.Server.Infrastructure.Security;
using Xunit;

namespace Pasto.IntegrationTests;

public sealed class RoomSecretsTests
{
    [Fact]
    public void GeneratedSecrets_HaveExpectedEncodingAndHash()
    {
        var secrets = new RoomSecrets();
        // 只驗格式／長度／雜湊一致性，不用抽樣碰撞率宣稱密碼學強度。
        for (var i = 0; i < 100; i++)
        {
            Assert.Matches("^[A-Z0-9]{6}$", secrets.GenerateCode());
            var credential = secrets.GenerateCredential();
            Assert.Matches("^[A-Za-z0-9_-]{22}$", credential.Value);
            var decoded = Convert.FromBase64String(credential.Value.Replace('-', '+').Replace('_', '/') + "==");
            Assert.Equal(16, decoded.Length);
            Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(credential.Value)), credential.Hash);
        }
    }
}
