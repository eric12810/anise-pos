using System.Security.Cryptography;
using System.Text;

namespace AnisePOS.Api.Endpoints;

// Shared validation for the future public checkout endpoints. No endpoint is
// exposed until order persistence and Stripe webhook handling are complete.
public static class WebsiteCheckoutValidation
{
    public static bool ValidName(string? value) => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= 160 && value.Any(char.IsLetter) && !value.Any(char.IsControl);
    public static bool ValidPhone(string? value) => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= 30 &&
        value.All(c => char.IsAsciiDigit(c) || c is ' ' or '+' or '-' or '(' or ')') &&
        value.Count(char.IsAsciiDigit) is >= 7 and <= 15 &&
        value.Count(c => c == '+') <= 1 && (!value.Contains('+') || value.TrimStart().StartsWith('+'));
    public static bool ValidAddressLine(string? value, bool required) => string.IsNullOrWhiteSpace(value)
        ? !required : value.Trim().Length <= 250 && value.Any(char.IsLetterOrDigit) && !value.Any(char.IsControl);
    public static bool ValidPostcode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > 10 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c != ' ')) return false;
        var compact = value.Replace(" ", "");
        return compact.Length is >= 5 and <= 7 && compact.Any(char.IsAsciiLetter) && compact.Any(char.IsAsciiDigit);
    }

    public static bool IsInDeliveryArea(double shopLatitude, double shopLongitude,
        double customerLatitude, double customerLongitude, double radiusMiles)
    {
        if (!double.IsFinite(shopLatitude) || !double.IsFinite(shopLongitude) ||
            !double.IsFinite(customerLatitude) || !double.IsFinite(customerLongitude) ||
            !double.IsFinite(radiusMiles) || radiusMiles < 0 ||
            Math.Abs(shopLatitude) > 90 || Math.Abs(customerLatitude) > 90 ||
            Math.Abs(shopLongitude) > 180 || Math.Abs(customerLongitude) > 180)
            return false;
        static double Radians(double degrees) => degrees * Math.PI / 180;
        var dLat = Radians(customerLatitude - shopLatitude);
        var dLon = Radians(customerLongitude - shopLongitude);
        var a = Math.Pow(Math.Sin(dLat / 2), 2) + Math.Cos(Radians(shopLatitude)) *
            Math.Cos(Radians(customerLatitude)) * Math.Pow(Math.Sin(dLon / 2), 2);
        var miles = 3958.7613 * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(Math.Max(0, 1 - a)));
        return miles <= radiusMiles;
    }

    public static bool VerifyStripeSignature(string payload, string signatureHeader,
        string secret, DateTimeOffset now, TimeSpan tolerance)
    {
        if (string.IsNullOrWhiteSpace(secret) || tolerance < TimeSpan.Zero) return false;
        var parts = signatureHeader.Split(',').Select(part => part.Trim().Split('=', 2))
            .Where(part => part.Length == 2).ToList();
        var timestamps = parts.Where(part => part[0] == "t").Select(part => part[1]).ToList();
        if (timestamps.Count != 1) return false;
        var timestamp = timestamps[0];
        if (!long.TryParse(timestamp, out var seconds)) return false;
        DateTimeOffset signedAt;
        try { signedAt = DateTimeOffset.FromUnixTimeSeconds(seconds); }
        catch (ArgumentOutOfRangeException) { return false; }
        if ((now - signedAt).Duration() > tolerance) return false;
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{payload}"));
        foreach (var signature in parts.Where(part => part[0] == "v1"))
        {
            byte[] provided;
            try { provided = Convert.FromHexString(signature[1]); }
            catch (FormatException) { continue; }
            if (provided.Length == expected.Length && CryptographicOperations.FixedTimeEquals(provided, expected)) return true;
        }
        return false;
    }
}
