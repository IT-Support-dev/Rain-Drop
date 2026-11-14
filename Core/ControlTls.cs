using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace RainDrop.Shared;

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
    public ControlTlsException(string reason) : base("Control-channel TLS rejected: " + reason) => Reason = reason;
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
            throw new ControlTlsException(ex is ControlTlsException tex ? tex.Reason : reason);
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
            throw new ControlTlsException(ex is ControlTlsException tex ? tex.Reason : reason);
        }
    }
}
