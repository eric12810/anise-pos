using System.Text.Json;

namespace AnisePOS.Api.Endpoints;

public static class InternalDiagnosticsEndpoints
{
    private const string StagingHost = "pos-staging.loveanise.co.uk";

    public static void MapInternalDiagnosticsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/internal/outbound-ip-diagnostic", async (HttpContext context,
            IConfiguration configuration, IHttpClientFactory clients, CancellationToken ct) =>
        {
            if (!configuration.GetValue<bool>("Diagnostics:OutboundIp:Enabled") ||
                !string.Equals(context.Request.Host.Host, StagingHost, StringComparison.OrdinalIgnoreCase))
                return Results.NotFound();

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                using var response = await clients.CreateClient().GetAsync(
                    "https://api.ipify.org?format=json", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (!response.IsSuccessStatusCode)
                    return Results.Problem("The outbound IP service returned an error.", statusCode: 502);

                using var document = await JsonDocument.ParseAsync(
                    await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
                if (!document.RootElement.TryGetProperty("ip", out var ip) ||
                    !System.Net.IPAddress.TryParse(ip.GetString(), out var address) ||
                    address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                    return Results.Problem("The outbound IP service returned an invalid IPv4 address.", statusCode: 502);

                return Results.Ok(new { ip = address.ToString() });
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return Results.Problem("The outbound IP check timed out.", statusCode: 504);
            }
            catch (HttpRequestException)
            {
                return Results.Problem("The outbound IP service could not be reached.", statusCode: 502);
            }
        })
        .RequireAuthorization("OutboundIpDiagnosticOwner");
    }
}
