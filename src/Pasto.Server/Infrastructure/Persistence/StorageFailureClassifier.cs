using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pasto.Server.Application.Rooms;

namespace Pasto.Server.Infrastructure.Persistence;

public sealed class StorageFailureClassifier : IStorageFailureClassifier
{
    public bool IsStorageFailure(Exception exception)
        => exception is NpgsqlException or DbUpdateException
            || (exception is AggregateException aggregate && aggregate.InnerExceptions.Any(IsStorageFailure))
            || (exception.InnerException is not null && IsStorageFailure(exception.InnerException));
}
