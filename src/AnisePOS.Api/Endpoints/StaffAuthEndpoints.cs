using System.Security.Claims;
using AnisePOS.Api.Data;
using AnisePOS.Api.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AnisePOS.Api.Endpoints;

public static class StaffAuthEndpoints
{
    public static void MapStaffAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/staff");

        auth.MapPost("/initial-owner", async (InitialOwnerRequest request, AniseDbContext db) =>
        {
            if (await db.StaffUsers.AnyAsync()) return Results.Conflict(new { message = "An initial owner has already been created." });
            if (!IsValid(request.Email, request.DisplayName, request.Password)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["staff"] = ["A display name, valid email, and password of at least 12 characters are required."] });
            var owner = new StaffUser { Email = request.Email.Trim().ToLowerInvariant(), DisplayName = request.DisplayName.Trim(), Role = StaffRole.Owner };
            owner.PasswordHash = new PasswordHasher<StaffUser>().HashPassword(owner, request.Password);
            db.StaffUsers.Add(owner);
            await db.SaveChangesAsync();
            return Results.Created($"/api/staff/{owner.Id}", ToResponse(owner));
        });

        auth.MapPost("/login", async (LoginRequest request, HttpContext context, AniseDbContext db) =>
        {
            var email = request.Email.Trim().ToLowerInvariant();
            var user = await db.StaffUsers.SingleOrDefaultAsync(x => x.Email == email);
            if (user is null || !user.IsActive || new PasswordHasher<StaffUser>().VerifyHashedPassword(user, user.PasswordHash, request.Password) == PasswordVerificationResult.Failed)
                return Results.Unauthorized();
            user.LastSignedInAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, PrincipalFor(user),
                new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30), AllowRefresh = true });
            return Results.Ok(ToResponse(user));
        }).RequireRateLimiting("staff-login");

        auth.MapPost("/logout", async (HttpContext context) => { await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme); return Results.NoContent(); }).RequireAuthorization();
        auth.MapGet("/me", (ClaimsPrincipal user) => Results.Ok(new { id = user.FindFirstValue(ClaimTypes.NameIdentifier), email = user.FindFirstValue(ClaimTypes.Email), name = user.Identity?.Name, role = user.FindFirstValue(ClaimTypes.Role) })).RequireAuthorization();
        auth.MapGet("", async (AniseDbContext db) => await db.StaffUsers.AsNoTracking().OrderBy(x => x.DisplayName)
            .Select(x => new StaffResponse(x.Id, x.Email, x.DisplayName, x.Role, x.IsActive, x.LastSignedInAtUtc)).ToListAsync()).RequireAuthorization("ManageStaff");

        auth.MapPost("", async (CreateStaffRequest request, AniseDbContext db) =>
        {
            var email = request.Email.Trim().ToLowerInvariant();
            if (!IsValid(email, request.DisplayName, request.Password) || await db.StaffUsers.AnyAsync(x => x.Email == email))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["staff"] = ["Use a unique valid email, display name, and password of at least 12 characters."] });
            var staff = new StaffUser { Email = email, DisplayName = request.DisplayName.Trim(), Role = request.Role };
            staff.PasswordHash = new PasswordHasher<StaffUser>().HashPassword(staff, request.Password);
            db.StaffUsers.Add(staff);
            await db.SaveChangesAsync();
            return Results.Created($"/api/staff/{staff.Id}", ToResponse(staff));
        }).RequireAuthorization("ManageStaff");

        auth.MapPut("/{staffId:guid}", async (Guid staffId, UpdateStaffRequest request, ClaimsPrincipal current, AniseDbContext db) =>
        {
            var staff = await db.StaffUsers.FindAsync(staffId);
            if (staff is null) return Results.NotFound();
            var email = request.Email.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(request.DisplayName) || !email.Contains('@') || await db.StaffUsers.AnyAsync(x => x.Id != staffId && x.Email == email))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["staff"] = ["Use a unique valid email and display name."] });
            var currentId = current.FindFirstValue(ClaimTypes.NameIdentifier);
            if (staff.Id.ToString() == currentId && (!request.IsActive || request.Role != staff.Role))
                return Results.BadRequest(new { message = "You cannot deactivate your own account or change your own role." });
            if (staff.Role == StaffRole.Owner && (request.Role != StaffRole.Owner || !request.IsActive) &&
                await db.StaffUsers.CountAsync(x => x.Role == StaffRole.Owner && x.IsActive) <= 1)
                return Results.BadRequest(new { message = "At least one active owner account is required." });
            staff.Email = email; staff.DisplayName = request.DisplayName.Trim(); staff.Role = request.Role;
            staff.IsActive = request.IsActive; staff.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(); return Results.Ok(ToResponse(staff));
        }).RequireAuthorization("ManageStaff");

        auth.MapPut("/{staffId:guid}/password", async (Guid staffId, ResetStaffPasswordRequest request, AniseDbContext db) =>
        {
            var staff = await db.StaffUsers.FindAsync(staffId);
            if (staff is null) return Results.NotFound();
            if (request.Password.Length < 12)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["password"] = ["The new password must be at least 12 characters."] });
            staff.PasswordHash = new PasswordHasher<StaffUser>().HashPassword(staff, request.Password);
            staff.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(); return Results.NoContent();
        }).RequireAuthorization("ManageStaff");

        auth.MapGet("/audit", async (int limit, AniseDbContext db) =>
        {
            limit = Math.Clamp(limit, 1, 500);
            var entries = await db.OrderStatusHistory.AsNoTracking().OrderByDescending(x => x.CreatedAtUtc).Take(limit)
                .Select(x => new { x.Order.OrderNumber, x.CreatedAtUtc, x.Status, x.Note, x.ChangedBy }).ToListAsync();
            var staff = await db.StaffUsers.AsNoTracking().ToDictionaryAsync(x => x.Id.ToString(), x => x.DisplayName);
            return entries.Select(x => new StaffAuditResponse(x.OrderNumber, x.CreatedAtUtc, x.Status, x.Note,
                x.ChangedBy is not null && staff.TryGetValue(x.ChangedBy, out var name) ? name : x.ChangedBy ?? "System")).ToList();
        }).RequireAuthorization("ManageStaff");
    }

    private static bool IsValid(string email, string displayName, string password) => !string.IsNullOrWhiteSpace(displayName) && email.Contains('@') && password.Length >= 12;
    private static StaffResponse ToResponse(StaffUser user) => new(user.Id, user.Email, user.DisplayName, user.Role, user.IsActive, user.LastSignedInAtUtc);
    private static ClaimsPrincipal PrincipalFor(StaffUser user) => new(new ClaimsIdentity([
        new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Email, user.Email),
        new Claim(ClaimTypes.Name, user.DisplayName), new Claim(ClaimTypes.Role, user.Role.ToString())
    ], CookieAuthenticationDefaults.AuthenticationScheme));
}

public sealed record InitialOwnerRequest(string Email, string DisplayName, string Password);
public sealed record LoginRequest(string Email, string Password);
public sealed record CreateStaffRequest(string Email, string DisplayName, string Password, StaffRole Role);
public sealed record UpdateStaffRequest(string Email, string DisplayName, StaffRole Role, bool IsActive);
public sealed record ResetStaffPasswordRequest(string Password);
public sealed record StaffResponse(Guid Id, string Email, string DisplayName, StaffRole Role, bool IsActive, DateTimeOffset? LastSignedInAtUtc);
public sealed record StaffAuditResponse(long OrderNumber, DateTimeOffset CreatedAtUtc, OrderStatus Status, string? Note, string ChangedBy);
