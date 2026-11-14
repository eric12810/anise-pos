namespace AnisePOS.Api.Diagnostics;

public sealed class GoogleRouteDiagnosticState
{
    private GoogleRouteDiagnosticSnapshot? latest;

    public GoogleRouteDiagnosticSnapshot? Latest => Volatile.Read(ref latest);

    public void Record(GoogleRouteDiagnosticSnapshot snapshot) => Volatile.Write(ref latest, snapshot);
}

public sealed record GoogleRouteDiagnosticSnapshot(
    DateTimeOffset ObservedAtUtc,
    int HttpStatus,
    string GoogleStatus,
    string Category,
    string SanitisedMessage,
    bool MentionsOutboundIp);
