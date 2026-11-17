namespace Pasto.Server.Application.Rooms;

// Infrastructure 將取得房間鎖的逾時轉成此例外；HTTP 層不需要辨識 provider 或 SQLSTATE。
public sealed class RoomBusyException(Exception innerException) : Exception("Room is busy.", innerException);
