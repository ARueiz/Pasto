namespace Pasto.Server.Application.Rooms;

// API 透過此邊界分類儲存故障，不引用 EF／Npgsql 的例外型別。
public interface IStorageFailureClassifier
{
    bool IsStorageFailure(Exception exception);
}

public sealed class ParticipantLimitReachedException() : Exception("Room participant limit was reached.");
