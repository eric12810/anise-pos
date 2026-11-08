using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AnisePOS.Api.Domain;

namespace AnisePOS.Api.Email;

// OAuth credentials must come from the host's secret configuration, never from the website.
public sealed class GmailConfirmationSender(IHttpClientFactory clients, IConfiguration configuration, GoogleMailTokenStore tokenStore)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(configuration["GoogleMail:ClientId"]) &&
        !string.IsNullOrWhiteSpace(configuration["GoogleMail:ClientSecret"]) &&
        (tokenStore.Exists || !string.IsNullOrWhiteSpace(configuration["GoogleMail:RefreshToken"]));

    public async Task SendAsync(string destination, long orderNumber, string trackingUrl, string kind, OrderType orderType, DateTimeOffset? estimatedAtUtc, CancellationToken ct)
    {
        if (!IsConfigured) throw new InvalidOperationException("Google Mail is not configured.");
        if (!ValidAddress(destination)) throw new ArgumentException("Invalid email address.", nameof(destination));
        if (!Uri.TryCreate(trackingUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.Host is not ("staging.anise.dotnetcloud.co.uk" or "www.loveanise.co.uk" or "loveanise.co.uk") ||
            !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            uri.AbsolutePath != "/order-status.html" || !Guid.TryParse(uri.Fragment.TrimStart('#'), out _))
            throw new ArgumentException("Invalid tracking link.", nameof(trackingUrl));

        var client = clients.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);
        using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, "https://oauth2.googleapis.com/token") {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> {
                ["client_id"] = configuration["GoogleMail:ClientId"]!,
                ["client_secret"] = configuration["GoogleMail:ClientSecret"]!,
                ["refresh_token"] = tokenStore.Exists ? tokenStore.Read() : configuration["GoogleMail:RefreshToken"]!,
                ["grant_type"] = "refresh_token"
            })
        };
        using var tokenResponse = await client.SendAsync(tokenRequest, ct);
        tokenResponse.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync(ct));
        var token = json.RootElement.GetProperty("access_token").GetString();
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("Google did not return an access token.");
        var (subject, body) = Compose(orderNumber, trackingUrl, kind, orderType, estimatedAtUtc);
        var message = $"From: Anise <anise@loveanise.co.uk>\r\nTo: {destination}\r\nSubject: {subject}\r\nMIME-Version: 1.0\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: base64\r\n\r\n" +
            Convert.ToBase64String(Encoding.UTF8.GetBytes(body), Base64FormattingOptions.InsertLineBreaks);
        var raw = Convert.ToBase64String(Encoding.UTF8.GetBytes(message)).TrimEnd('=').Replace('+','-').Replace('/','_');
        using var sendRequest = new HttpRequestMessage(HttpMethod.Post, "https://gmail.googleapis.com/gmail/v1/users/me/messages/send") {
            Content = JsonContent.Create(new { raw })
        };
        sendRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var sendResponse = await client.SendAsync(sendRequest, ct);
        sendResponse.EnsureSuccessStatusCode();
    }

    public static (string Subject, string Body) Compose(long orderNumber, string trackingUrl, string kind, OrderType orderType, DateTimeOffset? estimatedAtUtc)
    {
        string subject, introduction;
        if (kind == "Accepted")
        {
            if (estimatedAtUtc is null || orderType is not (OrderType.Collection or OrderType.Delivery))
                throw new InvalidOperationException("An accepted order needs an estimated time and fulfilment type.");
            var local = TimeZoneInfo.ConvertTime(estimatedAtUtc.Value, TimeZoneInfo.FindSystemTimeZoneById("Europe/London"));
            var fulfilment = orderType == OrderType.Collection ? "collection" : "delivery";
            subject = $"Anise order #{orderNumber} accepted";
            introduction = $"Your order #{orderNumber} has been accepted.\r\n\r\nEstimated {fulfilment}: {local.ToString("d MMMM yyyy 'at' HH:mm", System.Globalization.CultureInfo.GetCultureInfo("en-GB"))} (UK time).";
        }
        else if (kind == "Received")
        {
            subject = $"Anise order #{orderNumber} received";
            introduction = $"Thank you for your order.\r\n\r\nOrder #{orderNumber} has been received and is awaiting acceptance by the restaurant.";
        }
        else throw new InvalidOperationException("Unknown confirmation type.");
        return (subject, $"{introduction}\r\n\r\nTrack your order using this private link:\r\n{trackingUrl}\r\n\r\nPlease keep this link private. It expires seven days after ordering.\r\n\r\nQuestions? Call 020 7916 4976.\r\nAnise");
    }

    public static bool ValidAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 254 || value.Any(char.IsControl)) return false;
        return System.Net.Mail.MailAddress.TryCreate(value, out var address) && address.Address == value && address.Host.Contains('.');
    }
}
