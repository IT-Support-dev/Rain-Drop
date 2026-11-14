using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using RainDrop.Shared;

namespace RainDrop.Agent;

/// <summary>Endpoint identity is owned by SYSTEM; the user-mode Agent receives it only through the PID-checked pipe.</summary>
internal sealed class AgentSecurity : IDisposable
{
    public X509Certificate2 Certificate { get; }
    public X509Certificate2 Ca { get; }
    public RevocationState Revocation { get; }
    public RunAllowlist Allowlist { get; }
    private readonly Func<AuditEvent, bool> _audit;
    private readonly Func<RevocationList, bool> _saveRevocation;

    public AgentSecurity(X509Certificate2 certificate, X509Certificate2 ca, RevocationList revocation,
        RunAllowlist allowlist, Func<AuditEvent, bool>? audit = null, Func<RevocationList, bool>? saveRevocation = null)
    {
        Certificate = certificate;
        Ca = ca;
        Revocation = new RevocationState(revocation);
        Allowlist = allowlist;
        _audit = audit ?? (e => ServiceClient.Audit(e.Event, e.Ip, e.Result, e.Detail, out _));
        _saveRevocation = saveRevocation ?? (r => ServiceClient.SaveRevocation(JsonSerializer.Serialize(r), out _));
    }

    public static AgentSecurity? LoadFromService(out string? error)
    {
        error = null;
        var response = ServiceClient.GetIdentity(out string? pipeError);
        if (response is null || !response.Ok)
        {
            error = pipeError ?? response?.Message ?? "Service denied identity";
            return null;
        }
        try
        {
            using var pub = new X509Certificate2(Convert.FromBase64String(response.CertificateBase64!));
            var key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(Convert.FromBase64String(response.PrivateKeyBase64!), out _);
            var certificate = pub.CopyWithPrivateKey(key);
            var ca = new X509Certificate2(Convert.FromBase64String(response.CaBase64!));
            var revocation = JsonSerializer.Deserialize<RevocationList>(response.RevocationJson!)!;
            var allowlist = JsonSerializer.Deserialize<RunAllowlist>(response.AllowlistJson!)!;
            return new AgentSecurity(certificate, ca, revocation, allowlist);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or JsonException or ArgumentNullException)
        {
            error = "Identity response invalid: " + ex.GetType().Name;
            return null;
        }
    }

    public bool Audit(AuditEvent entry) => _audit(entry);

    public bool ApplyRevocation(RevocationList incoming)
    {
        Revocation.Apply(incoming); // validates version/serials before persistent write
        return _saveRevocation(Revocation.Snapshot());
    }

    public void Dispose()
    {
        Certificate.Dispose();
        Ca.Dispose();
    }
}
