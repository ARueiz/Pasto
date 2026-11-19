using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Pasto.Server.Application.Rooms;

namespace Pasto.Server.Api;

public sealed class ParticipantAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder, IParticipantVerifier verifier)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Participant";
    private const string RoomClaim = "pasto_room";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // 不讓公開／不存在的 endpoint 因多餘的 Authorization header 查 DB。
        var endpoint = Context.GetEndpoint();
        if (endpoint is null || endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null
            || endpoint.Metadata.GetMetadata<IAuthorizeData>() is null) return AuthenticateResult.NoResult();
        var credential = ReadCredential();
        if (string.IsNullOrWhiteSpace(credential)) return AuthenticateResult.NoResult();
        var identity = await verifier.VerifyAsync(credential, Context.RequestAborted);
        if (identity is null) return AuthenticateResult.Fail("Invalid participant credential.");
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, identity.ParticipantId.ToString()),
            new Claim(RoomClaim, identity.RoomId.ToString())], SchemeName));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    private string? ReadCredential()
    {
        var authorization = Request.Headers.Authorization;
        if (authorization.Count != 0)
        {
            return authorization.Count == 1
                && AuthenticationHeaderValue.TryParse(authorization.ToString(), out var header)
                && string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
                ? header.Parameter : null;
        }

        if (Request.Path == RoomHub.Path && (Context.WebSockets.IsWebSocketRequest
                || (HttpMethods.IsGet(Request.Method) && Request.Headers.Accept == "text/event-stream"))
            && Request.Query.TryGetValue("access_token", out var token) && token.Count == 1)
            return token[0];

        return null;
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = "Bearer";
        return ApiProblem.WriteAsync(Context, 401, "ParticipantUnauthorized");
    }

    internal static ParticipantIdentity Identity(HttpContext context, Guid roomId) => Identity(context.User, roomId);

    internal static ParticipantIdentity Identity(ClaimsPrincipal? user, Guid roomId)
    {
        if (user is null || !Guid.TryParse(user.FindFirstValue(RoomClaim), out var actualRoom) || actualRoom != roomId
            || !Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var participant))
            throw new RoomParticipantMismatchException();
        return new ParticipantIdentity(participant, actualRoom);
    }
}
