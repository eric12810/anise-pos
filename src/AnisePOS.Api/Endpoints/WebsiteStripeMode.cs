namespace AnisePOS.Api.Endpoints;

public sealed record WebsiteStripeMode(bool Live)
{
    public string SessionPrefix => Live ? "cs_live_" : "cs_test_";

    public static WebsiteStripeMode? From(IConfiguration config, IHostEnvironment environment)
    {
        var mode = config["WebsiteCheckout:StripeMode"];
        if (environment.IsProduction() && mode != "live" ||
            !environment.IsProduction() && mode != "test") return null;
        var key = config["Stripe:SecretKey"];
        var webhook = config["WebsiteCheckout:StripeWebhookSecret"];
        var allowedHosts = environment.IsProduction()
            ? new[] { "www.loveanise.co.uk", "loveanise.co.uk" }
            : new[] { "www.staging.loveanise.co.uk", "staging.loveanise.co.uk" };
        foreach (var setting in new[] { "WebsiteCheckout:StripeSuccessUrl", "WebsiteCheckout:StripeCancelUrl" })
            if (!Uri.TryCreate(config[setting], UriKind.Absolute, out var url) || url.Scheme != "https" ||
                !allowedHosts.Contains(url.Host, StringComparer.OrdinalIgnoreCase) || !url.IsDefaultPort ||
                url.UserInfo.Length != 0 || url.Fragment.Length != 0) return null;
        if (key is null || !key.StartsWith(mode == "live" ? "sk_live_" : "sk_test_", StringComparison.Ordinal) ||
            webhook is null || !webhook.StartsWith("whsec_", StringComparison.Ordinal)) return null;
        return new WebsiteStripeMode(mode == "live");
    }

    public bool MatchesSession(string? id) => id?.StartsWith(SessionPrefix, StringComparison.Ordinal) == true;
}
