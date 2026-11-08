using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;

namespace AnisePOS.Api.Email;

public static class GoogleMailOAuthEndpoints
{
    private const string Redirect = "https://www.anise.dotnetcloud.co.uk/api/admin/google-mail/callback";
    private const string Cookie = "anise.googlemail.state";
    public static void MapGoogleMailOAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/google-mail").RequireAuthorization("ManageStaff");
        group.MapGet("/start", (HttpContext context, IConfiguration config) => {
            var clientId = config["GoogleMail:ClientId"];
            if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(config["GoogleMail:ClientSecret"]))
                return Results.Problem("Google Mail client credentials have not been configured.", statusCode: 503);
            var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            context.Response.Cookies.Append(Cookie, state, new CookieOptions { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, MaxAge = TimeSpan.FromMinutes(10), Path = "/api/admin/google-mail" });
            context.Response.Headers.CacheControl = "no-store";
            return Results.Redirect(QueryHelpers.AddQueryString("https://accounts.google.com/o/oauth2/v2/auth", new Dictionary<string, string?> {
                ["client_id"] = clientId, ["redirect_uri"] = Redirect, ["response_type"] = "code", ["state"] = state,
                ["scope"] = "openid email https://www.googleapis.com/auth/gmail.send", ["access_type"] = "offline", ["prompt"] = "consent", ["login_hint"] = "anise@loveanise.co.uk"
            }));
        });
        group.MapGet("/callback", async (HttpContext context, IConfiguration config, IHttpClientFactory clients, GoogleMailTokenStore store, CancellationToken ct) => {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            var cookie = context.Request.Cookies[Cookie];
            context.Response.Cookies.Delete(Cookie, new CookieOptions { Secure = true, HttpOnly = true, SameSite = SameSiteMode.Lax, Path = "/api/admin/google-mail" });
            var state = context.Request.Query["state"].ToString();
            var code = context.Request.Query["code"].ToString();
            if (cookie is null || state.Length != 64 || cookie.Length != 64 || !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(cookie), System.Text.Encoding.UTF8.GetBytes(state)) || string.IsNullOrWhiteSpace(code) || code.Length > 2048)
                return Results.BadRequest("Google authorisation could not be verified. Start again.");
            var id = config["GoogleMail:ClientId"]; var secret = config["GoogleMail:ClientSecret"];
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(secret)) return Results.StatusCode(503);
            try {
                var client = clients.CreateClient(); client.Timeout = TimeSpan.FromSeconds(15);
                using var request = new HttpRequestMessage(HttpMethod.Post, "https://oauth2.googleapis.com/token") { Content = new FormUrlEncodedContent(new Dictionary<string,string> {
                    ["client_id"] = id, ["client_secret"] = secret, ["code"] = code, ["redirect_uri"] = Redirect, ["grant_type"] = "authorization_code"
                }) };
                using var response = await client.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode) return Results.Problem("Google authorisation failed. Start again.", statusCode: 502);
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                var access = json.RootElement.GetProperty("access_token").GetString();
                var refresh = json.RootElement.TryGetProperty("refresh_token", out var refreshElement) ? refreshElement.GetString() : null;
                if (string.IsNullOrWhiteSpace(access) || string.IsNullOrWhiteSpace(refresh)) return Results.Problem("Google did not provide offline access. Start again.", statusCode: 502);
                using var profile = new HttpRequestMessage(HttpMethod.Get, "https://openidconnect.googleapis.com/v1/userinfo");
                profile.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
                using var info = await client.SendAsync(profile, ct);
                if (!info.IsSuccessStatusCode) return Results.Problem("Could not verify the Google account.", statusCode: 502);
                using var identity = JsonDocument.Parse(await info.Content.ReadAsStringAsync(ct));
                if (!identity.RootElement.TryGetProperty("email", out var email) || email.GetString() != "anise@loveanise.co.uk" || !identity.RootElement.TryGetProperty("email_verified", out var verified) || verified.ValueKind != JsonValueKind.True)
                    return Results.BadRequest("Please connect the anise@loveanise.co.uk account.");
                await store.WriteAsync(refresh, ct);
                return Results.Content("Google Mail connected. You can close this page and return to the POS.", "text/plain");
            } catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException) {
                return Results.Problem("Google authorisation could not be completed. Please try again.", statusCode: 502);
            }
        });
    }
}
