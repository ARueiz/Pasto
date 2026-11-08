using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pasto.Server.Application.Rooms;
using Pasto.Server.Domain;

namespace Pasto.Server.Infrastructure.Persistence;

public sealed class RoomCreationStore(IDbContextFactory<PastoDbContext> contextFactory) : IRoomCreationStore
{
    public async Task<Room?> TryCreateAsync(string code, byte[] credentialHash, CancellationToken cancellationToken)
    {
        // 每次嘗試各自建立 context／transaction。碰撞後不重用被 abort 的交易或 tracking 狀態。
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var createdAt = await context.Database.SqlQuery<DateTime>($"""
            SELECT clock_timestamp() AS "Value"
            """).SingleAsync(cancellationToken);
        var room = new Room
        {
            Id = Guid.NewGuid(),
            Code = code,
            CreatorParticipantId = Guid.NewGuid(),
            CreatedAt = createdAt,
            ExpiresAt = createdAt.AddHours(5)
        };

        context.CodeReservations.Add(new RoomCodeReservation
        {
            Code = code,
            ReusableAfter = room.ExpiresAt.AddHours(24)
        });
        try
        {
            // 用唯一約束仲裁，而非先 SELECT 再 INSERT；仍在冷卻的碼也不能取得。
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "PK_room_code_reservations",
            TableName: "room_code_reservations"
        })
        {
            // await using 在離開前回滾並釋放資源，下一次嘗試才會開始。
            return null;
        }

        // 分階段 SaveChanges 避免 EF tracked graph 的循環排序問題；仍是同一筆 DB 交易。
        context.Rooms.Add(room);
        await context.SaveChangesAsync(cancellationToken);
        context.Participants.Add(new RoomParticipant
        {
            Id = room.CreatorParticipantId,
            RoomId = room.Id,
            CredentialHash = credentialHash,
            CreatedAt = createdAt
        });
        await context.SaveChangesAsync(cancellationToken);

        // Creator 的 deferred FK 到這裡才驗證；任何中途失敗皆不回傳半成品。
        // 保留呼叫端取消；但 commit 期間的取消／連線錯誤不代表一定回滾。
        // 上層不可自動重送；即使不傳取消 token，也無法保證 Client 能收到憑證。
        await transaction.CommitAsync(cancellationToken);
        return room;
    }
}
