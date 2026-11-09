using AnisePOS.Api.Data;
using AnisePOS.Api.Endpoints;
using AnisePOS.Api.Printing;
using AnisePOS.Api.Email;
using AnisePOS.Api.Notifications;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.RateLimiting;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
var dataProtectionPath = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "data", "data-protection-keys"));
Directory.CreateDirectory(dataProtectionPath);

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionPath))
    .SetApplicationName("AnisePOS");
builder.Services.AddDbContext<AniseDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("AniseDatabase"), sql =>
    {
        sql.UseCompatibilityLevel(150);
        sql.CommandTimeout(15);
    }));
builder.Services.AddCors(options => options.AddPolicy("PublicWebsiteMenu", policy => policy.WithOrigins("https://www.staging.loveanise.co.uk", "https://staging.anise.dotnetcloud.co.uk").WithMethods("GET")));
builder.Services.AddCors(options => options.AddPolicy("PublicWebsiteCheckout", policy => policy.WithOrigins("https://www.staging.loveanise.co.uk", "https://staging.anise.dotnetcloud.co.uk").WithMethods("POST").WithHeaders("Content-Type")));
builder.Services.AddProblemDetails();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<GoogleMailTokenStore>();
builder.Services.AddSingleton<GmailConfirmationSender>();
builder.Services.AddHostedService<WebsiteConfirmationWorker>();
builder.Services.AddSingleton<PushKeyStore>();
builder.Services.AddHostedService<StaffPushWorker>();
builder.Services.AddSingleton<ReceiptPrinter>();
builder.Services.AddRateLimiter(options => options.AddFixedWindowLimiter("website-checkout", limiter =>
{
    limiter.PermitLimit = 20;
    limiter.Window = TimeSpan.FromMinutes(1);
    limiter.QueueLimit = 0;
    limiter.AutoReplenishment = true;
}));
builder.Services.AddRateLimiter(options => options.AddPolicy("website-offers", context =>
    RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ =>
        new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true })));
builder.Services.AddRateLimiter(options => options.AddPolicy("website-status", context =>
    RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ =>
        new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true })));
builder.Services.AddRateLimiter(options => options.AddPolicy("website-availability", context =>
    RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ =>
        new FixedWindowRateLimiterOptions { PermitLimit = 600, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true })));
builder.Services.AddRateLimiter(options => options.AddFixedWindowLimiter("staff-login", limiter =>
{
    limiter.PermitLimit = 10;
    limiter.Window = TimeSpan.FromMinutes(1);
    limiter.QueueLimit = 0;
    limiter.AutoReplenishment = true;
}));
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "anise.staff";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("ManageMenu", policy => policy.RequireRole("Owner", "Manager"))
    .AddPolicy("ViewReports", policy => policy.RequireRole("Owner", "Manager"))
    .AddPolicy("ManageStaff", policy => policy.RequireRole("Owner"));

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AniseDbContext>();
    await db.Database.ExecuteSqlRawAsync("""
        IF OBJECT_ID('dbo.StaffPushSubscriptions', 'U') IS NULL
        BEGIN
            CREATE TABLE dbo.StaffPushSubscriptions (Id uniqueidentifier NOT NULL PRIMARY KEY, EndpointHash nvarchar(64) NOT NULL, ProtectedSubscription nvarchar(max) NOT NULL, StaffId nvarchar(100) NOT NULL, CreatedAtUtc datetimeoffset(0) NOT NULL, UpdatedAtUtc datetimeoffset(0) NULL);
            CREATE UNIQUE INDEX UX_StaffPushSubscriptions_EndpointHash ON dbo.StaffPushSubscriptions(EndpointHash);
        END;
        IF OBJECT_ID('dbo.StaffPushDeliveries', 'U') IS NULL
        BEGIN
            CREATE TABLE dbo.StaffPushDeliveries (Id uniqueidentifier NOT NULL PRIMARY KEY, SubscriptionId uniqueidentifier NOT NULL, OrderId uniqueidentifier NOT NULL, State nvarchar(16) NOT NULL, Attempts int NOT NULL, NextAttemptAtUtc datetimeoffset(0) NULL, CreatedAtUtc datetimeoffset(0) NOT NULL, UpdatedAtUtc datetimeoffset(0) NULL);
            CREATE UNIQUE INDEX UX_StaffPushDeliveries_Subscription_Order ON dbo.StaffPushDeliveries(SubscriptionId, OrderId);
        END;
        IF OBJECT_ID('dbo.DeliverySettings', 'U') IS NULL
            CREATE TABLE dbo.DeliverySettings (Id int NOT NULL PRIMARY KEY CHECK (Id = 1), RadiusMiles decimal(6,2) NOT NULL CHECK (RadiusMiles >= 0 AND RadiusMiles <= 100), Fee decimal(10,2) NOT NULL CHECK (Fee >= 0 AND Fee <= 1000));
        IF COL_LENGTH('dbo.DeliverySettings', 'Mode') IS NULL
            ALTER TABLE dbo.DeliverySettings ADD Mode nvarchar(16) NOT NULL CONSTRAINT DF_DeliverySettings_Mode DEFAULT 'radius';
        IF COL_LENGTH('dbo.DeliverySettings', 'SectorsJson') IS NULL
            ALTER TABLE dbo.DeliverySettings ADD SectorsJson nvarchar(max) NOT NULL CONSTRAINT DF_DeliverySettings_Sectors DEFAULT '[]';
        IF COL_LENGTH('dbo.DeliverySettings', 'RadiusBandsJson') IS NULL
            ALTER TABLE dbo.DeliverySettings ADD RadiusBandsJson nvarchar(max) NOT NULL CONSTRAINT DF_DeliverySettings_RadiusBands DEFAULT '[]';
        IF COL_LENGTH('dbo.DeliverySettings', 'OrderingAvailabilityJson') IS NULL
            ALTER TABLE dbo.DeliverySettings ADD OrderingAvailabilityJson nvarchar(max) NULL;
        IF COL_LENGTH('dbo.DeliverySettings', 'OrderSound') IS NULL
            ALTER TABLE dbo.DeliverySettings ADD OrderSound nvarchar(20) NULL;
        IF COL_LENGTH('dbo.DeliverySettings', 'PrinterIpAddress') IS NULL
            ALTER TABLE dbo.DeliverySettings ADD PrinterIpAddress nvarchar(15) NULL;
        IF COL_LENGTH('dbo.DeliverySettings', 'PromoCodesJson') IS NULL
            ALTER TABLE dbo.DeliverySettings ADD PromoCodesJson nvarchar(max) NULL;
        IF OBJECT_ID('dbo.Promotions', 'U') IS NULL
            CREATE TABLE dbo.Promotions (Id uniqueidentifier NOT NULL PRIMARY KEY, SettingsJson nvarchar(max) NOT NULL, ImageData varbinary(max) NULL, ImageContentType nvarchar(20) NULL, CreatedAtUtc datetimeoffset(0) NOT NULL, UpdatedAtUtc datetimeoffset(0) NULL);
        IF OBJECT_ID('dbo.PromoRedemptions', 'U') IS NULL
        BEGIN
            CREATE TABLE dbo.PromoRedemptions (Id uniqueidentifier NOT NULL PRIMARY KEY, Code nvarchar(40) NOT NULL, Phone nvarchar(20) NOT NULL, OrderId uniqueidentifier NOT NULL, CreatedAtUtc datetimeoffset(0) NOT NULL, UpdatedAtUtc datetimeoffset(0) NULL);
            CREATE INDEX IX_PromoRedemptions_Code_Phone ON dbo.PromoRedemptions(Code, Phone);
        END;
        IF OBJECT_ID('dbo.WebsiteCheckouts', 'U') IS NULL
        BEGIN
            CREATE TABLE dbo.WebsiteCheckouts (Id uniqueidentifier NOT NULL PRIMARY KEY, RequestId uniqueidentifier NOT NULL, RequestHash nvarchar(64) NOT NULL, SnapshotJson nvarchar(max) NOT NULL, Total decimal(10,2) NOT NULL, StripeSessionId nvarchar(255) NULL, StripeSessionUrl nvarchar(2048) NULL, CompletedOrderId uniqueidentifier NULL, CreatedAtUtc datetimeoffset(0) NOT NULL, UpdatedAtUtc datetimeoffset(0) NULL);
            CREATE UNIQUE INDEX UX_WebsiteCheckouts_RequestId ON dbo.WebsiteCheckouts(RequestId);
        END;
        IF OBJECT_ID('dbo.WebsiteConfirmations', 'U') IS NULL
        BEGIN
            CREATE TABLE dbo.WebsiteConfirmations (Id uniqueidentifier NOT NULL PRIMARY KEY, OrderId uniqueidentifier NOT NULL REFERENCES dbo.Orders(Id), ProtectedPayload nvarchar(max) NOT NULL, Kind nvarchar(20) NOT NULL CONSTRAINT DF_WebsiteConfirmations_Kind DEFAULT 'Received', State nvarchar(20) NOT NULL, SentAtUtc datetimeoffset(0) NULL, CreatedAtUtc datetimeoffset(0) NOT NULL, UpdatedAtUtc datetimeoffset(0) NULL);
        END;
        IF COL_LENGTH('dbo.WebsiteConfirmations', 'Kind') IS NULL
            ALTER TABLE dbo.WebsiteConfirmations ADD Kind nvarchar(20) NOT NULL CONSTRAINT DF_WebsiteConfirmations_Kind DEFAULT 'Received';
        IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_WebsiteConfirmations_OrderId' AND object_id = OBJECT_ID('dbo.WebsiteConfirmations'))
            DROP INDEX UX_WebsiteConfirmations_OrderId ON dbo.WebsiteConfirmations;
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_WebsiteConfirmations_OrderId_Kind' AND object_id = OBJECT_ID('dbo.WebsiteConfirmations'))
            EXEC(N'CREATE UNIQUE INDEX UX_WebsiteConfirmations_OrderId_Kind ON dbo.WebsiteConfirmations(OrderId, Kind)');
        IF COL_LENGTH('dbo.WebsiteCheckouts', 'CompletedOrderId') IS NULL
            ALTER TABLE dbo.WebsiteCheckouts ADD CompletedOrderId uniqueidentifier NULL;
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_WebsiteCheckouts_StripeSessionId' AND object_id = OBJECT_ID('dbo.WebsiteCheckouts'))
            CREATE UNIQUE INDEX UX_WebsiteCheckouts_StripeSessionId ON dbo.WebsiteCheckouts(StripeSessionId) WHERE StripeSessionId IS NOT NULL;
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_Payments_WebsiteCash_Reference'  AND object_id = OBJECT_ID('dbo.Payments'))
            CREATE UNIQUE INDEX UX_Payments_WebsiteCash_Reference ON dbo.Payments(Provider, ProviderReference) WHERE Provider = 'WebsiteCash' AND ProviderReference IS NOT NULL;
        IF COL_LENGTH('dbo.Orders', 'EstimatedAtUtc') IS NULL
            ALTER TABLE dbo.Orders ADD EstimatedAtUtc datetimeoffset(0) NULL;
        IF COL_LENGTH('dbo.OrderLines', 'Notes') IS NULL
            ALTER TABLE dbo.OrderLines ADD Notes nvarchar(500) NULL;
        IF COL_LENGTH('dbo.ProductCategories', 'DisplayOrder') IS NULL
            ALTER TABLE dbo.ProductCategories ADD DisplayOrder int NULL;
        IF OBJECT_ID('dbo.ProductImages', 'U') IS NULL
            CREATE TABLE dbo.ProductImages (ProductId uniqueidentifier NOT NULL PRIMARY KEY REFERENCES dbo.Products(Id) ON DELETE CASCADE, Data varbinary(max) NOT NULL, ContentType nvarchar(20) NOT NULL);
        IF COL_LENGTH('dbo.Products', 'IsVegan') IS NULL
            ALTER TABLE dbo.Products ADD IsVegan bit NOT NULL CONSTRAINT DF_Products_IsVegan DEFAULT 0;
        IF COL_LENGTH('dbo.Orders', 'CustomFeeDescription') IS NULL
            ALTER TABLE dbo.Orders ADD CustomFeeDescription nvarchar(160) NULL;
        IF COL_LENGTH('dbo.Orders', 'CustomFee') IS NULL
            ALTER TABLE dbo.Orders ADD CustomFee decimal(10,2) NOT NULL CONSTRAINT DF_Orders_CustomFee DEFAULT 0;
        """);
}

if (app.Environment.IsDevelopment())
    app.UseDeveloperExceptionPage();
else
{
    app.UseHsts();
    app.UseExceptionHandler();
}
app.Use(async (context, next) =>
{
    if (HttpMethods.IsPost(context.Request.Method) && context.Request.Path.StartsWithSegments("/api") &&
        context.Request.Headers["X-HTTP-Method-Override"] is var overrideMethod &&
        (overrideMethod == "PUT" || overrideMethod == "DELETE"))
        context.Request.Method = overrideMethod.ToString();
    await next();
});
app.UseRouting();
app.UseHttpsRedirection();
app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.XFrameOptions = "DENY";
    context.Response.Headers["Referrer-Policy"] = "same-origin";
    await next();
});
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        if (context.File.Name is "pos.html" or "orders.html" or "orders.js")
            context.Context.Response.Headers.CacheControl = "no-cache";
    }
});
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/", () => Results.Redirect("/staff-login.html"));
app.MapGet("/health", async (AniseDbContext db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct) ? Results.Ok(new { status = "ok", database = "ok" }) : Results.Problem("Database unavailable.", statusCode: 503));
app.MapMenuEndpoints();
app.MapOrderEndpoints();
app.MapWebsiteOrderEndpoints();
app.MapGoogleMailOAuthEndpoints();
app.MapWebsiteStripeWebhook();
app.MapOrderQueueEndpoints();
app.MapStaffPushEndpoints();

app.MapReportEndpoints();
app.MapAdminEndpoints();
app.MapPromotionEndpoints();
app.MapStaffAuthEndpoints();

app.Run();
