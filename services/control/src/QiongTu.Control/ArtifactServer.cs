using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using QiongTu.Contracts;

namespace QiongTu.Control;

public sealed class ArtifactServer : IAsyncDisposable
{
    private readonly ArtifactRootRegistry _roots;
    private readonly string _accessToken;
    private WebApplication? _application;

    public ArtifactServer(ArtifactRootRegistry roots)
    {
        _roots = roots;
        _accessToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public string BaseUrl { get; private set; } = string.Empty;

    public ArtifactSession CreateSession()
    {
        if (string.IsNullOrEmpty(BaseUrl))
        {
            throw new InvalidOperationException("The artifact server has not started.");
        }

        return new ArtifactSession(BaseUrl, _accessToken);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_application is not null)
        {
            throw new InvalidOperationException("The artifact server has already started.");
        }

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));

        var application = builder.Build();
        application.MapGet(
            "/artifacts/{rootId}/{**relativePath}",
            (HttpContext context, string rootId, string relativePath) =>
            {
                if (!HasValidBearerToken(context.Request.Headers.Authorization.ToString()))
                {
                    return Results.Unauthorized();
                }

                if (!_roots.TryOpenRead(rootId, relativePath, out var stream) || stream is null)
                {
                    return Results.NotFound();
                }

                return Results.File(stream, enableRangeProcessing: true);
            });

        await application.StartAsync(cancellationToken);
        var addresses = application.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()
            ?.Addresses;
        BaseUrl = addresses?.SingleOrDefault(address => address.StartsWith("http://127.0.0.1:", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("The artifact server did not bind an IPv4 loopback address.");
        _application = application;
    }

    public async ValueTask DisposeAsync()
    {
        if (_application is null)
        {
            return;
        }

        await _application.StopAsync();
        await _application.DisposeAsync();
        _application = null;
        BaseUrl = string.Empty;
    }

    private bool HasValidBearerToken(string authorization)
    {
        const string prefix = "Bearer ";
        if (!authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var supplied = Encoding.UTF8.GetBytes(authorization[prefix.Length..]);
        var expected = Encoding.UTF8.GetBytes(_accessToken);
        return supplied.Length == expected.Length && CryptographicOperations.FixedTimeEquals(supplied, expected);
    }
}
