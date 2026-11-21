namespace Pasto.Server.Api;

internal sealed class HubAdmissionOptions
{
    public int GlobalConnections { get; set; } = 1500;
    public int SourceConnections { get; set; } = 30;
    public int RoomConnections { get; set; } = 100;
    public int ParticipantConnections { get; set; } = 3;
    public int NegotiateSourceCapacity { get; set; } = 5_000;
    public int ClientTimeoutSeconds { get; set; } = 30;

    internal bool IsValid() => GlobalConnections > 0 && SourceConnections > 0 && RoomConnections > 0 &&
        ParticipantConnections > 0 && NegotiateSourceCapacity is > 0 and <= 5_000 &&
        ClientTimeoutSeconds is >= 30 and <= 300;
}
