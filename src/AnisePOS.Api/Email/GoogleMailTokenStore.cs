using Microsoft.AspNetCore.DataProtection;

namespace AnisePOS.Api.Email;

public sealed class GoogleMailTokenStore(IHostEnvironment environment, IDataProtectionProvider protection)
{
    private string Pathname => Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", "data", "google-mail-refresh-token"));
    public bool Exists => File.Exists(Pathname);
    public string Read() => protection.CreateProtector("Anise.GoogleMail.RefreshToken.v1").Unprotect(File.ReadAllText(Pathname));
    public async Task WriteAsync(string token, CancellationToken ct)
    {
        var target = Pathname;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            await File.WriteAllTextAsync(temporary, protection.CreateProtector("Anise.GoogleMail.RefreshToken.v1").Protect(token), ct);
            File.Move(temporary, target, true);
        } finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
