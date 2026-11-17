namespace Pasto.Server.Domain;

public static class RoomRules
{
    public const int MaxItemCount = 300;
    public const int MaxContentBytes = 8192;

    public static bool IsAvailable(Room room, DateTime now)
        => room.DeletedAt is null && now < room.ExpiresAt;
}
