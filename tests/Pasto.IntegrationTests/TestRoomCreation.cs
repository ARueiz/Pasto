using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pasto.Server.Application.Rooms;
using Pasto.Server.Infrastructure.Persistence;
using Pasto.Server.Infrastructure.Security;

namespace Pasto.IntegrationTests;

internal static class TestRoomCreation
{
    // 只組裝正式 Application／Store；這裡不另外實作交易或建房 SQL。
    public static CreateRoom Create(PostgresTestDatabase database, IRoomSecrets secrets,
        int maxAttempts = 5, params IInterceptor[] interceptors)
        => new(new RoomCreationStore(database.CreateContextFactory(interceptors)), secrets,
            Options.Create(new CreateRoomOptions { MaxCodeAttempts = maxAttempts }), NullLogger<CreateRoom>.Instance);
}

internal sealed class SequenceRoomSecrets(params string[] codes) : IRoomSecrets
{
    private readonly RoomSecrets realSecrets = new();
    public int CodeCalls { get; private set; }
    public ParticipantCredential? FixedCredential { get; init; }

    // 耗盡序列後重複最後一碼，讓測試可以明確驗證重試上限。
    public string GenerateCode() => codes[Math.Min(CodeCalls++, codes.Length - 1)];
    public ParticipantCredential GenerateCredential() => FixedCredential ?? realSecrets.GenerateCredential();
}
