using Microsoft.AspNetCore.Cors.Infrastructure;

namespace AnisePOS.Api;

public static class WebsiteCors
{
    public static readonly string[] ApprovedOrigins =
    [
        "https://www.loveanise.co.uk",
        "https://loveanise.co.uk",
        "https://www.staging.loveanise.co.uk",
        "https://staging.loveanise.co.uk",
        "http://staging.anise.dotnetcloud.co.uk",
        "https://staging.anise.dotnetcloud.co.uk"
    ];

    public static void Configure(CorsOptions options)
    {
        options.AddPolicy("PublicWebsiteMenu", policy => policy
            .WithOrigins(ApprovedOrigins)
            .WithMethods("GET"));
        options.AddPolicy("PublicWebsiteCheckout", policy => policy
            .WithOrigins(ApprovedOrigins)
            .WithMethods("POST")
            .WithHeaders("Content-Type"));
    }
}
