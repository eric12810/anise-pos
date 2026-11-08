using System.Text.Json;
using AnisePOS.Api.Data;
using AnisePOS.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace AnisePOS.Api.Endpoints;
public sealed record OpeningDay(int Day, bool Closed, string Open, string Close);
public sealed record ClosurePeriod(DateTimeOffset StartsAt, DateTimeOffset EndsAt, List<string> Reasons, string? OtherReason);
public sealed record OrderingSettings(bool HoursEnabled, List<OpeningDay> Hours, bool Paused, DateTimeOffset? PauseUntil, List<string> PauseReasons, string? PauseOtherReason, List<ClosurePeriod> Closures);
public sealed record OrderingStatus(bool Accepting, string? Reason, DateTimeOffset? NextOpenAt);
public static class OrderingAvailability
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    public static readonly string[] Reasons = ["We are busy", "Staff shortage", "Kitchen maintenance", "Holiday", "Private event", "Unexpected closure"];
    public static OrderingSettings Default() => new(false, Enumerable.Range(0, 7).Select(d => new OpeningDay(d, false, "09:00", "22:00")).ToList(), false, null, [], null, []);
    static bool Time(string? s, out TimeOnly t) => TimeOnly.TryParseExact(s, "HH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out t);
    static bool ValidReasons(List<string>? reasons, string? other) => reasons is not null && reasons.Count <= Reasons.Length && reasons.Distinct().Count() == reasons.Count && reasons.All(Reasons.Contains) && (other?.Length ?? 0) <= 200;
    public static bool Valid(OrderingSettings? s) => s is not null && s.Hours is { Count: 7 } && s.Hours.All(x => x is not null && x.Day is >= 0 and <= 6 && Time(x.Open, out _) && Time(x.Close, out _) && (x.Closed || x.Open != x.Close)) && s.Hours.Select(x => x.Day).Distinct().Count() == 7 && ValidReasons(s.PauseReasons, s.PauseOtherReason) && s.Closures is not null && s.Closures.Count <= 50 && s.Closures.All(c => c is not null && c.StartsAt < c.EndsAt && ValidReasons(c.Reasons, c.OtherReason));
    public static OrderingSettings? Parse(string? json) { if (string.IsNullOrWhiteSpace(json)) return Default(); try { var s = JsonSerializer.Deserialize<OrderingSettings>(json, Json); return Valid(s) ? s : null; } catch (JsonException) { return null; } }
    public static string Serialize(OrderingSettings s) => JsonSerializer.Serialize(s, Json);
    public static async Task<OrderingSettings?> Get(AniseDbContext db, CancellationToken ct) => Parse(await db.DeliverySettings.AsNoTracking().Select(x => x.OrderingAvailabilityJson).SingleOrDefaultAsync(ct));
    static string Reason(List<string> reasons, string? other, string fallback) { var parts = reasons.ToList(); if (!string.IsNullOrWhiteSpace(other)) parts.Add(other.Trim()); return parts.Count == 0 ? fallback : string.Join("; ", parts); }
    static bool WithinHours(OrderingSettings s, DateTimeOffset now)
    {
        if (!s.HoursEnabled) return true;
        var local = TimeZoneInfo.ConvertTime(now, London); var t = TimeOnly.FromDateTime(local.DateTime);
        var today = s.Hours.Single(x => x.Day == (int)local.DayOfWeek); var yesterday = s.Hours.Single(x => x.Day == ((int)local.DayOfWeek + 6) % 7);
        Time(today.Open, out var open); Time(today.Close, out var close); Time(yesterday.Open, out var prevOpen); Time(yesterday.Close, out var prevClose);
        return (!today.Closed && (open < close ? t >= open && t < close : t >= open)) || (!yesterday.Closed && prevOpen > prevClose && t < prevClose);
    }
    static string? ClosedReason(OrderingSettings s, DateTimeOffset now)
    {
        if (s.Paused && (!s.PauseUntil.HasValue || now < s.PauseUntil.Value)) return Reason(s.PauseReasons, s.PauseOtherReason, "We are temporarily unable to accept orders.");
        var closure = s.Closures.FirstOrDefault(c => now >= c.StartsAt && now < c.EndsAt);
        if (closure is not null) return Reason(closure.Reasons, closure.OtherReason, "We are temporarily closed.");
        return WithinHours(s, now) ? null : "We are currently closed.";
    }
    public static OrderingStatus Evaluate(OrderingSettings? s, DateTimeOffset now)
    {
        if (s is null) return new(false, "Ordering is temporarily unavailable.", null);
        var reason = ClosedReason(s, now); if (reason is null) return new(true, null, null);
        if (s.Paused && !s.PauseUntil.HasValue) return new(false, reason, null);
        if (s.HoursEnabled && s.Hours.All(x => x.Closed)) return new(false, reason, null);
        var next = new DateTimeOffset(now.UtcDateTime.AddTicks(-(now.UtcTicks % TimeSpan.TicksPerMinute)), TimeSpan.Zero).AddMinutes(1);
        var end = now.AddYears(10);
        while (next <= end)
        {
            DateTimeOffset? blockedUntil = s.Paused && s.PauseUntil > next ? s.PauseUntil : null;
            foreach (var closure in s.Closures.Where(c => next >= c.StartsAt && next < c.EndsAt))
                if (blockedUntil is null || closure.EndsAt > blockedUntil) blockedUntil = closure.EndsAt;
            if (blockedUntil is not null)
            {
                var utc = blockedUntil.Value.ToUniversalTime();
                next = new DateTimeOffset(utc.UtcDateTime.AddTicks(-(utc.UtcTicks % TimeSpan.TicksPerMinute)), TimeSpan.Zero);
                if (next < utc) next = next.AddMinutes(1);
                continue;
            }
            if (WithinHours(s, next)) return new(false, reason, next);
            next = next.AddMinutes(1);
        }
        return new(false, reason, null);
    }
}
