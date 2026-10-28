using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using RemoteHub.Models;
using RemoteHub.Remoting;

namespace RemoteHub.Tests;

/// <summary>
/// WinRM over HTTPS through a jump host (review S-03): WinRM only sees 127.0.0.1 and skips the name check, so the
/// certificate the target presents through the jump host is checked first – the one trusted for the connection signs in
/// without asking, any other is asked about and refused unless the user trusts it.
/// </summary>
public class WinRmJumpHostCertificateTests
{
    private static X509Certificate2 SelfSigned()
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=" + Guid.NewGuid(), key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
    }

    /// <summary>
    /// What the jump host's far end answers with: a TLS server on 127.0.0.1 presenting <paramref name="cert"/> to every
    /// connection; <c>Received</c> counts the bytes a client sent after the handshake.
    /// </summary>
    private sealed class TlsServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        public int Received;

        public TlsServer(X509Certificate2 cert)
        {
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    TcpClient client;
                    try { client = await _listener.AcceptTcpClientAsync(); }
                    catch { return; }
                    _ = Task.Run(async () =>
                    {
                        using (client)
                        {
                            await using var tls = new SslStream(client.GetStream());
                            try
                            {
                                await tls.AuthenticateAsServerAsync(cert);
                                var buffer = new byte[256];
                                int n;
                                while ((n = await tls.ReadAsync(buffer)) > 0) Interlocked.Add(ref Received, n);
                            }
                            catch { /* the check only looks */ }
                        }
                    });
                }
            });
        }

        public Func<CancellationToken, Task<Stream>> Open => async ct =>
        {
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)_listener.LocalEndpoint).Port, ct);
            return client.GetStream();
        };

        public void Dispose() => _listener.Stop();
    }

    private static PsTarget Target(string? trusted) =>
        new("sql01.contoso.local", UseSsl: true, new CredentialSet(), TrustedCertificate: trusted, ConnectionId: Guid.NewGuid());

    [Fact]
    public void WinRM_through_a_jump_host_refuses_a_certificate_other_than_the_pinned_one()
    {
        using var trusted = SelfSigned();
        using var other = SelfSigned();
        using var server = new TlsServer(other);
        PresentedCertificate? asked = null;
        var ex = Assert.Throws<AuthenticationException>(() =>
            PsRemoting.CheckCertificate(Target(ServerCertificates.Pin(trusted)), server.Open, (_, seen) => { asked = seen; return false; }));
        Assert.Contains("isn't trusted", ex.Message);
        Assert.NotNull(asked);
        Assert.True(asked.Changed);
        Assert.Equal(ServerCertificates.Pin(other), asked.Pin);
        Assert.Equal(other.Thumbprint, asked.Thumbprint);
        // without a window to ask in, it is refused as well
        Assert.Throws<AuthenticationException>(() => PsRemoting.CheckCertificate(Target(ServerCertificates.Pin(trusted)), server.Open, null));
        Assert.Equal(0, server.Received); // nothing was sent after the handshake
    }

    [Fact]
    public void WinRM_through_a_jump_host_asks_once_and_then_connects_without_asking()
    {
        using var cert = SelfSigned();
        using var server = new TlsServer(cert);
        var asks = 0;
        string? remembered = null;
        PsRemoting.CheckCertificate(Target(null), server.Open, (_, seen) =>
        {
            asks++;
            Assert.False(seen.Changed);
            remembered = seen.Pin;
            return true;
        });
        Assert.Equal(1, asks);
        Assert.Equal(ServerCertificates.Pin(cert), remembered);

        PsRemoting.CheckCertificate(Target(remembered), server.Open, (_, _) => { asks++; return false; });
        Assert.Equal(1, asks); // the same certificate again: not asked
    }

    [Fact]
    public void A_target_that_does_not_answer_TLS_through_the_jump_host_is_an_error_not_a_prompt()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _ = Task.Run(async () => { using var c = await listener.AcceptTcpClientAsync(); }); // closes at once
        try
        {
            var asked = false;
            Func<CancellationToken, Task<Stream>> open = async ct =>
            {
                var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, ct);
                return client.GetStream();
            };
            var ex = Assert.Throws<IOException>(() => PsRemoting.CheckCertificate(Target(null), open, (_, _) => asked = true));
            Assert.Contains("TLS handshake", ex.Message);
            Assert.False(asked);
        }
        finally { listener.Stop(); }
    }
}
