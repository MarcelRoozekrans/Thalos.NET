using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Thalos.Sandbox.Host;

/// <summary>
/// Refuses every request, on every route, that does not carry <c>Authorization: Bearer &lt;THALOS_SANDBOX_TOKEN&gt;</c>,
/// with 401 and an empty body. Sandboxes on the shared internal network can reach each other, so the token is what
/// stops one run's code from calling another run's sandbox.
/// </summary>
/// <remarks>
/// The comparison is <see cref="CryptographicOperations.FixedTimeEquals"/> over SHA-256 digests of the presented and
/// the expected token, so neither the content nor the length of the token shows in the time a refusal takes.
/// </remarks>
/// <param name="next">The rest of the pipeline.</param>
/// <param name="settings">Holds the expected token.</param>
internal sealed class BearerTokenMiddleware(RequestDelegate next, SandboxSettings settings)
{
    private const string Scheme = "Bearer ";
    private readonly byte[] _expected = SHA256.HashData(Encoding.UTF8.GetBytes(settings.Token));

    /// <summary>Passes an authorized request on; answers any other with 401.</summary>
    /// <param name="context">The request.</param>
    public Task InvokeAsync(HttpContext context)
    {
        if (IsAuthorized(context.Request.Headers.Authorization))
        {
            return next(context);
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Bearer";
        return Task.CompletedTask;
    }

    private bool IsAuthorized(StringValues header)
    {
        if (header.Count != 1 || header[0] is not { } value || !value.StartsWith(Scheme, StringComparison.Ordinal))
        {
            return false;
        }

        var presented = SHA256.HashData(Encoding.UTF8.GetBytes(value[Scheme.Length..]));
        return CryptographicOperations.FixedTimeEquals(presented, _expected);
    }
}
