using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Trapd.Agent.Service;

public sealed class InventoryCollector
{
    public InventoryData Collect()
    {
        var hostname = Environment.MachineName;
        string fqdn = hostname;
        string? domain = null;
        bool joined = false;

        try
        {
            var ipProps = IPGlobalProperties.GetIPGlobalProperties();
            domain = ipProps.DomainName;
            if (!string.IsNullOrWhiteSpace(domain))
            {
                fqdn = $"{ipProps.HostName}.{domain}";
                joined = true;
            }
            else
            {
                fqdn = Dns.GetHostEntry(hostname).HostName;
            }
        }
        catch
        {
            // Fallback
        }

        var osDesc = RuntimeInformation.OSDescription; // e.g. Microsoft Windows 10.0...
        var arch = NormalizeArch(RuntimeInformation.OSArchitecture);

        // Get all UP interfaces that are not Loopback
        var interfaces = NetworkInterface.GetAllNetworkInterfaces()
            .Where(ni => ni.OperationalStatus == OperationalStatus.Up)
            .Where(ni => ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .ToList();

        var ipAddrs = new List<string>();
        string? primaryIp = null;

        foreach (var ni in interfaces)
        {
            var props = ni.GetIPProperties();
            var ips = props.UnicastAddresses
                .Where(ua => ua.Address.AddressFamily == AddressFamily.InterNetwork) // IPv4
                .Select(ua => ua.Address.ToString())
                .ToList();

            if (ips.Count > 0)
            {
                ipAddrs.AddRange(ips);

                // Heuristic: Primary IP usually has a gateway
                if (primaryIp == null && props.GatewayAddresses.Count > 0)
                {
                    primaryIp = ips.First();
                }
            }
        }

        // Fallback for primary IP
        if (primaryIp == null && ipAddrs.Count > 0)
        {
            primaryIp = ipAddrs.First();
        }

        return new InventoryData(
            Hostname: hostname,
            Fqdn: fqdn,
            Os: "Windows",
            OsVersion: osDesc,
            Arch: arch,
            PrimaryIp: primaryIp ?? "127.0.0.1",
            IpAddrs: ipAddrs.Distinct().ToList(),
            Domain: domain,
            Joined: joined,
            AadJoined: false // Placeholder
        );
    }

    private static string NormalizeArch(Architecture arch)
    {
        return arch switch
        {
            Architecture.X64 => "x86_64",
            Architecture.Arm64 => "aarch64",
            Architecture.Arm => "arm",
            Architecture.X86 => "i686",
            _ => "unknown"
        };
    }
}

public record InventoryData(
    string Hostname,
    string Fqdn,
    string Os,
    string OsVersion,
    string Arch,
    string PrimaryIp,
    List<string> IpAddrs,
    string? Domain,
    bool Joined,
    bool? AadJoined
);
