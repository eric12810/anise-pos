using System.Net;
using System.Text.Json;
using AnisePOS.Api.Diagnostics;

namespace AnisePOS.Api.Endpoints;

public static class InternalDiagnosticsEndpoints
{
    private const string StagingHost = "pos-staging.loveanise.co.uk";

    public static void MapInternalDiagnosticsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/internal/google-routes-diagnostic", async (HttpContext context,
            IConfiguration configuration, IHttpClientFactory clients, GoogleRouteDiagnosticState routeState, CancellationToken ct) =>
        {
            if (!configuration.GetValue<bool>("Diagnostics:GoogleRoutes:Enabled") ||
                !string.Equals(context.Request.Host.Host, StagingHost, StringComparison.OrdinalIgnoreCase))
                return Results.NotFound();

            string? outboundIp = null;
            string? networkError = null;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                using var response = await clients.CreateClient().GetAsync(
                    "https://api.ipify.org?format=json", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (response.IsSuccessStatusCode)
                {
                    using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
                    if (json.RootElement.TryGetProperty("ip", out var ip) && IPAddress.TryParse(ip.GetString(), out var address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        outboundIp = address.ToString();
                    else networkError = "The IP service returned an invalid IPv4 address.";
                }
                else networkError = "The IP service returned an error.";
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { networkError = "The outbound IP check timed out."; }
            catch (HttpRequestException) { networkError = "The outbound IP service could not be reached."; }

            var latest = routeState.Latest;
            return Results.Ok(new
            {
                outboundIp,
                outboundIpError = networkError,
                googleRoutesServerKeyConfigured = !string.IsNullOrWhiteSpace(configuration["GoogleMaps:RoutesServerKey"]),
                lastGoogleRoutesFailure = latest is null ? null : new
                {
                    latest.ObservedAtUtc,
                    latest.HttpStatus,
                    latest.GoogleStatus,
                    latest.Category,
                    latest.SanitisedMessage,
                    latest.MentionsOutboundIp
                }
            });
        }).RequireAuthorization("GoogleRoutesDiagnosticOwner");
    }
}
