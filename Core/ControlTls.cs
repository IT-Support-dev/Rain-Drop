using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace RainDrop.Shared;

public enum ControlRole { MasterClient, AgentServer }

/// <summary>Application messages carried by the existing Protocol framing after mutual TLS + legacy auth.</summary>
public static class ControlMessages
{
    // New message IDs only; the 5-byte header, length limit and ECDSA signing in Protocol.cs are unchanged.
    public const MsgType RevocationList = (MsgType)64;
    public const MsgType RevocationAck = (MsgType)65;
}

public sealed class ControlTlsException : AuthenticationException
{
    public string Reason { get; }
    public ControlTlsException(string reason, Exception? inner = null)
        : base("Control-channel TLS rejected: " + reason + (inner is null ? "" : " (" + inner.GetType().Name + ": " + inner.Message + ")"), inner) => Reason = reason;
}

public static class ControlTls
{
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

    public static async Task<SslStream> AuthenticateServerAsync(Stream inner, X509Certificate2 certificate,
        ControlTlsPolicy policy, CancellationToken ct)
    {
        var ssl = new SslStream(inner, false);
        string reason = "handshake-failed";
        try
        {
            var options = new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                ClientCertificateRequired = true,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                CertificateChainPolicy = policy.ChainPolicy(),
                EnabledSslProtocols = SslProtocols.None, // OS default; negotiated version checked below.
                AllowRenegotiation = false,
                AllowTlsResume = false,
                RemoteCertificateValidationCallback = (_, cert, chain, errors) =>
                {
                    bool ok = policy.Validate(cert, chain, errors, out reason);
                    return ok;
                },
            };
            await ssl.AuthenticateAsServerAsync(options, ct).ConfigureAwait(false);
            ControlTlsPolicy.RequireModernTls(ssl);
            if (!policy.Validate(ssl.RemoteCertificate, null, SslPolicyErrors.None, out reason))
                throw new ControlTlsException(reason);
            return ssl;
        }
        catch (Exception ex) when (ex is AuthenticationException or IOException or OperationCanceledException)
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw new ControlTlsException(ex is ControlTlsException tex ? tex.Reason : reason, ex);
        }
    }

    public static async Task<SslStream> AuthenticateClientAsync(Stream inner, string targetHost,
        X509Certificate2 certificate, ControlTlsPolicy policy, CancellationToken ct)
    {
        var ssl = new SslStream(inner, false);
        string reason = "handshake-failed";
        try
        {
            var options = new SslClientAuthenticationOptions
            {
                TargetHost = targetHost,
                ClientCertificates = new X509CertificateCollection { certificate },
                LocalCertificateSelectionCallback = (_, _, _, _, _) => certificate,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                CertificateChainPolicy = policy.ChainPolicy(),
                EnabledSslProtocols = SslProtocols.None,
                AllowRenegotiation = false,
                AllowTlsResume = false,
                RemoteCertificateValidationCallback = (_, cert, chain, errors) =>
                {
                    if ((errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0)
                    { reason = "hostname-mismatch"; return false; }
                    return policy.Validate(cert, chain, errors, out reason);
                },
            };
            await ssl.AuthenticateAsClientAsync(options, ct).ConfigureAwait(false);
            ControlTlsPolicy.RequireModernTls(ssl);
            if (!policy.Validate(ssl.RemoteCertificate, null, SslPolicyErrors.None, out reason))
                throw new ControlTlsException(reason);
            return ssl;
        }
        catch (Exception ex) when (ex is AuthenticationException or IOException or OperationCanceledException)
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw new ControlTlsException(ex is ControlTlsException tex ? tex.Reason : reason, ex);
        }
    }
}

/// <summary>
/// Fail-closed TLS policy for the control channel. The only trust anchor is the supplied
/// repository CA; Windows root stores, AIA downloads and public CAs are never consulted.
/// Revocation is an authenticated local snapshot, not an unavailable HTTP CDP on a LAN.
/// </summary>
public sealed class ControlTlsPolicy
{
    private readonly X509Certificate2 _ca;
    private readonly RevocationState _revoked;
    private readonly ControlRole _remoteRole;

    public ControlTlsPolicy(X509Certificate2 ca, RevocationState revoked, ControlRole remoteRole)
    {
        _ca = ca;
        _revoked = revoked;
        _remoteRole = remoteRole;
        if (ca.HasPrivateKey) throw new ArgumentException("A TLS trust anchor must not contain a private key");
        using var key = ca.GetECDsaPublicKey() ?? throw new System.Security.Cryptography.CryptographicException("CA must use ECDSA");
        if (key.KeySize != 256) throw new System.Security.Cryptography.CryptographicException("CA must use P-256");
        var constraints = ca.Extensions.OfType<X509BasicConstraintsExtension>().SingleOrDefault();
        if (constraints is not { CertificateAuthority: true, Critical: true })
            throw new System.Security.Cryptography.CryptographicException("Pinned certificate is not a critical CA");
    }

    public X509ChainPolicy ChainPolicy() => new()
    {
        TrustMode = X509ChainTrustMode.CustomRootTrust,
        CustomTrustStore = { _ca },
        RevocationMode = X509RevocationMode.NoCheck, // Local snapshot below is authoritative; never fetch a CDP.
        RevocationFlag = X509RevocationFlag.ExcludeRoot,
        VerificationTime = DateTime.UtcNow,
        ApplicationPolicy = { new Oid(ExpectedEku(_remoteRole)) },
        DisableCertificateDownloads = true,
        UrlRetrievalTimeout = TimeSpan.Zero,
    };

    public bool Validate(X509Certificate? certificate, X509Chain? suppliedChain, SslPolicyErrors errors, out string reason)
    {
        reason = "certificate-rejected";
        if (certificate is null) { reason = "missing-certificate"; return false; }
        using var leaf = certificate as X509Certificate2 ?? new X509Certificate2(certificate);
        if (_revoked.IsRevoked(RepoCertificates.Serial(leaf))) { reason = "revoked-certificate"; return false; }
        var now = DateTime.UtcNow;
        if (leaf.NotBefore.ToUniversalTime() > now || leaf.NotAfter.ToUniversalTime() <= now)
        { reason = "expired-certificate"; return false; }
        if (leaf.Extensions.OfType<X509BasicConstraintsExtension>().SingleOrDefault() is not { CertificateAuthority: false, Critical: true })
        { reason = "invalid-leaf"; return false; }
        var eku = leaf.Extensions.OfType<X509EnhancedKeyUsageExtension>().SingleOrDefault();
        if (eku is null || !eku.Critical || !eku.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == ExpectedEku(_remoteRole)))
        { reason = "wrong-role"; return false; }
        if (leaf.Extensions.OfType<X509KeyUsageExtension>().SingleOrDefault() is not { } usage ||
            !usage.Critical || (usage.KeyUsages & X509KeyUsageFlags.DigitalSignature) == 0)
        { reason = "invalid-key-usage"; return false; }
        using (var publicKey = leaf.GetECDsaPublicKey())
        {
            if (publicKey is null || publicKey.KeySize != 256) { reason = "invalid-key"; return false; }
        }
        if (leaf.SignatureAlgorithm.Value != "1.2.840.10045.4.3.2") // ecdsa-with-SHA256
        { reason = "invalid-signature-algorithm"; return false; }

        // Build our own chain instead of accepting a Schannel-supplied chain or its policy errors.
        // An unknown Windows root is expected and is NOT a reason to bypass validation.
        using var chain = new X509Chain();
        chain.ChainPolicy = ChainPolicy();
        if (suppliedChain is not null)
        {
            foreach (var element in suppliedChain.ChainElements)
                chain.ChainPolicy.ExtraStore.Add(element.Certificate);
        }
        if (!chain.Build(leaf) || chain.ChainStatus.Any(s => s.Status != X509ChainStatusFlags.NoError))
        { reason = "untrusted-ca"; return false; }
        if (chain.ChainElements.Count != 2 ||
            !chain.ChainElements[1].Certificate.RawDataMemory.Span.SequenceEqual(_ca.RawDataMemory.Span))
        { reason = "untrusted-chain"; return false; }
        reason = "ok";
        return true;
    }

    public static string ExpectedEku(ControlRole role) => role == ControlRole.MasterClient
        ? RepoCertificates.ClientAuthOid : RepoCertificates.ServerAuthOid;

    public static void RequireModernTls(SslStream stream)
    {
        // EnabledSslProtocols=None negotiates OS defaults. A successful handshake alone is insufficient:
        // an OS permitting TLS 1.0/1.1 must still be rejected before any application data is read/written.
        if (stream.SslProtocol is not (SslProtocols.Tls12 or SslProtocols.Tls13))
            throw new AuthenticationException("Control channel requires TLS 1.2 or newer");
        if (!stream.IsEncrypted || !stream.IsMutuallyAuthenticated || stream.RemoteCertificate is null)
            throw new AuthenticationException("Mutual TLS authentication is required");
    }
}
