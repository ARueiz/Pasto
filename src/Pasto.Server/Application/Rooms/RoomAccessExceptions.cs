namespace Pasto.Server.Application.Rooms;

public sealed class RoomUnavailableException() : Exception("Room is unavailable.");
public sealed class RoomParticipantMismatchException() : Exception("Participant does not belong to this room.");
