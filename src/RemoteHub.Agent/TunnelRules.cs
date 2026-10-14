using System.Net;
using System.Net.Sockets;

namespace RemoteHub.Agent;

/// <summary>
/// Policy <c>TunnelTargets</c> and <c>TunnelPorts</c>: where a jump connection may go. A target address is allowed if it
/// lies in an allowed address range, or the target's name matches an allowed name (or no targets are set). Addresses of
/// the device itself and others that are never a remote device (loopback, unspecified, link-local, multicast, broadcast)
/// are only allowed by an entry about them – the device's own adapter addresses by one naming that address, the others by
/// a range of /8 or narrower – never by a name, which could resolve to anything, nor by a range for a whole network.
/// </summary>
public sealed class TunnelRules
{
    /// <summary>No values set: every target (except the special addresses) and every port.</summary>
    public static readonly TunnelRules Any = new(null, [], [], null, []);

    private readonly List<AddressRange>? _ranges; // null = any target
    private readonly List<string> _names;          // "sql01.contoso.local", or ".contoso.local" for "*.contoso.local"
    private readonly List<(int From, int To)>? _ports; // null = any port

    private TunnelRules(List<AddressRange>? ranges, List<string> names, List<string> ignored, List<(int, int)>? ports, List<string> ignoredPorts)
    {
        _ranges = ranges;
        _names = names;
        _ports = ports;
        Ignored = [.. ignored, .. ignoredPorts];
    }

    /// <summary>Entries that couldn't be read (they allow nothing).</summary>
    public IReadOnlyList<string> Ignored { get; }

    public bool AnyTarget => _ranges is null;
    public bool AnyPort => _ports is null;

    /// <summary>
    /// Reads the policy values (null = not set). A value that is set but has no readable entry allows nothing: a policy
    /// that was meant to restrict must not turn into "anything".
    /// </summary>
    public static TunnelRules Parse(IReadOnlyList<string>? targets, IReadOnlyList<string>? ports)
    {
        List<AddressRange>? ranges = null;
        List<string> names = [], ignored = [], ignoredPorts = [];
        if (targets is not null)
        {
            ranges = [];
            foreach (var raw in targets)
            {
                var t = raw.Trim();
                if (t.Length == 0) continue;
                if (t == "*") { ranges = null; names.Clear(); break; }
                if (AddressRange.TryParse(t, out var range)) ranges.Add(range);
                else if (NamePattern(t) is { } name) names.Add(name);
                else ignored.Add(t);
            }
        }
        List<(int, int)>? allowedPorts = null;
        if (ports is not null)
        {
            allowedPorts = [];
            foreach (var raw in ports)
            {
                var p = raw.Trim();
                if (p.Length == 0) continue;
                if (p == "*") { allowedPorts = null; break; }
                var dash = p.IndexOf('-');
                if (dash < 0 && int.TryParse(p, out var single) && single is >= 1 and <= 65535) allowedPorts.Add((single, single));
                else if (dash > 0 && int.TryParse(p[..dash], out var from) && int.TryParse(p[(dash + 1)..], out var to)
                         && from is >= 1 and <= 65535 && to >= from && to <= 65535) allowedPorts.Add((from, to));
                else ignoredPorts.Add(p);
            }
        }
        return new TunnelRules(ranges, names, ignored, allowedPorts, ignoredPorts);
    }

    public bool PortAllowed(int port) => _ports is null || _ports.Any(r => port >= r.From && port <= r.To);

    /// <summary>The addresses of <paramref name="host"/> a jump connection may go to (in the given order); empty if none.</summary>
    public IReadOnlyList<IPAddress> Allowed(string host, IEnumerable<IPAddress> addresses)
    {
        var name = Normalize(host);
        var nameAllowed = !IPAddress.TryParse(host.Trim('[', ']'), out _) && _names.Any(n => n.StartsWith('.') ? name.EndsWith(n, StringComparison.Ordinal) && name.Length > n.Length : name == n);
        var result = new List<IPAddress>();
        foreach (var raw in addresses)
        {
            var a = Plain(raw);
            var inRange = _ranges?.Any(r => r.Contains(a)) == true;
            // A special address only by a range that is about it: the device's own adapter addresses only by an entry naming
            // that one address, the others by a range of /8 or narrower – "10.0.0.0/8" for the network, or "0.0.0.0/0" for
            // "all IPv4", opens neither the loopback nor the jump host's own services.
            var own = !IPAddress.IsLoopback(a) && OwnAddresses().Contains(a);
            var namedByRange = _ranges?.Any(r => (own ? r.IsSingle : r.Prefix >= 8) && r.Contains(a)) == true;
            if (IsSpecial(a) ? namedByRange : _ranges is null || inRange || nameAllowed) result.Add(a.AddressFamily == AddressFamily.InterNetworkV6 && raw.ScopeId != 0 ? raw : a); // (ScopeId throws for IPv4)
        }
        return result;
    }

    /// <summary>
    /// Never a remote device: this device's own addresses, loopback, unspecified, link-local, multicast, broadcast and
    /// reserved addresses.
    /// </summary>
    public static bool IsSpecial(IPAddress raw)
    {
        var a = Plain(raw);
        if (IPAddress.IsLoopback(a) || OwnAddresses().Contains(a)) return true;
        if (a.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = a.GetAddressBytes();
            return b[0] == 0 || b[0] == 127 || (b[0] == 169 && b[1] == 254) || b[0] >= 224;
        }
        return a.Equals(IPAddress.IPv6Any) || a.Equals(IPAddress.IPv6None) || a.IsIPv6LinkLocal || a.IsIPv6Multicast;
    }

    /// <summary>IPv4 for an IPv4-mapped address, and without an IPv6 scope id (which would make "::1%1" look unlike "::1").</summary>
    private static IPAddress Plain(IPAddress raw)
    {
        if (raw.IsIPv4MappedToIPv6) return raw.MapToIPv4();
        return raw.AddressFamily == AddressFamily.InterNetworkV6 && raw.ScopeId != 0 ? new IPAddress(raw.GetAddressBytes()) : raw;
    }

    private static (HashSet<IPAddress> Addresses, DateTime Read) _own = ([], DateTime.MinValue);

    /// <summary>The addresses of this device's network adapters (read again after 30 s): services listening on them are its own.</summary>
    private static HashSet<IPAddress> OwnAddresses()
    {
        var own = _own;
        if (DateTime.UtcNow - own.Read < TimeSpan.FromSeconds(30)) return own.Addresses;
        var set = new HashSet<IPAddress>();
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                foreach (var u in nic.GetIPProperties().UnicastAddresses)
                    set.Add(Plain(u.Address));
        }
        catch { /* keep what is known */ }
        _own = (set, DateTime.UtcNow);
        return set;
    }

    private static string Normalize(string host) => host.Trim().TrimEnd('.').ToLowerInvariant();

    /// <summary>"sql01.contoso.local" or "*.contoso.local" (stored as ".contoso.local"); null if it isn't a host name.</summary>
    private static string? NamePattern(string entry)
    {
        var e = Normalize(entry);
        var wildcard = e.StartsWith("*.", StringComparison.Ordinal);
        var name = wildcard ? e[2..] : e;
        if (name.Length == 0 || name.Length > 253 || name.Split('.').Any(l => l.Length is 0 or > 63 || !l.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-')))
            return null;
        return wildcard ? "." + name : name;
    }

    /// <summary>An address and how many leading bits must match ("10.20.0.0/16", "fd00::/8", "10.1.2.3").</summary>
    private readonly record struct AddressRange(byte[] Bytes, int Prefix)
    {
        /// <summary>One address ("10.1.2.3", "10.1.2.3/32").</summary>
        public bool IsSingle => Prefix == Bytes.Length * 8;

        public static bool TryParse(string text, out AddressRange range)
        {
            range = default;
            var slash = text.IndexOf('/');
            if (!IPAddress.TryParse(slash < 0 ? text : text[..slash], out var raw)) return false;
            var a = raw.IsIPv4MappedToIPv6 ? raw.MapToIPv4() : raw;
            var bits = a.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
            var prefix = bits;
            if (slash >= 0 && (!int.TryParse(text[(slash + 1)..], out prefix) || prefix < 0 || prefix > bits)) return false;
            range = new AddressRange(a.GetAddressBytes(), prefix);
            return true;
        }

        public bool Contains(IPAddress a)
        {
            var b = a.GetAddressBytes();
            if (b.Length != Bytes.Length) return false;
            var full = Prefix / 8;
            for (var i = 0; i < full; i++)
                if (b[i] != Bytes[i]) return false;
            var rest = Prefix % 8;
            if (rest == 0) return true;
            var mask = (byte)(0xFF << (8 - rest));
            return (b[full] & mask) == (Bytes[full] & mask);
        }
    }
}
