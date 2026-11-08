using Microsoft.Extensions.Options;

namespace Pasto.Server.Application.Rooms;

public sealed class CreateRoom
{
    private readonly IRoomCreationStore store;
    private readonly IRoomSecrets secrets;
    private readonly ILogger<CreateRoom> logger;
    private readonly int maxCodeAttempts;

    public CreateRoom(IRoomCreationStore store, IRoomSecrets secrets,
        IOptions<CreateRoomOptions> options, ILogger<CreateRoom> logger)
    {
        if (options.Value.MaxCodeAttempts is < 1 or > 20)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxCodeAttempts must be between 1 and 20.");
        }

        this.store = store;
        this.secrets = secrets;
        this.logger = logger;
        maxCodeAttempts = options.Value.MaxCodeAttempts;
    }

    public async Task<CreatedRoom> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var credential = secrets.GenerateCredential();
        for (var attempt = 1; attempt <= maxCodeAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var room = await store.TryCreateAsync(secrets.GenerateCode(), credential.Hash, cancellationToken);
            if (room is not null)
            {
                // Store 已 commit 才能回傳。憑證只隨成功結果交給呼叫端，不傳進資料庫。
                return new CreatedRoom(room, credential.Value);
            }

            logger.LogWarning("Room code collision on attempt {Attempt} of {MaxAttempts}.", attempt, maxCodeAttempts);
        }

        // 這裡不記錄房碼、Credential 或 hash，也不對 commit 失敗／結果不確定做自動重試。
        throw new RoomCodeAllocationException();
    }
}
