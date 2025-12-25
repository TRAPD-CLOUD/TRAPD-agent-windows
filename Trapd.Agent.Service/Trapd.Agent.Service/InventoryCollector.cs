using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Trapd.Agent.Service;

public sealed class InventoryCollector
{
    private static readonly DateTime _agentStartTime = DateTime.UtcNow;

    // Hardware cache for performance (refresh every 5 minutes)
    private HardwareData? _cachedHardware;
    private DateTime _hardwareCacheTime;
    private static readonly TimeSpan HardwareCacheDuration = TimeSpan.FromMinutes(5);

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
        var osBuild = GetOsBuild();

        // Get all UP interfaces that are not Loopback
        var interfaces = NetworkInterface.GetAllNetworkInterfaces()
            .Where(ni => ni.OperationalStatus == OperationalStatus.Up)
            .Where(ni => ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .ToList();

        var ipAddrs = new List<string>();
        var macAddrs = new List<string>();
        string? primaryIp = null;

        foreach (var ni in interfaces)
        {
            // Collect MAC address
            var mac = ni.GetPhysicalAddress();
            if (mac != null && mac.GetAddressBytes().Length > 0)
            {
                var macString = FormatMacAddress(mac);
                if (!string.IsNullOrEmpty(macString) && !macAddrs.Contains(macString))
                {
                    macAddrs.Add(macString);
                }
            }

            var props = ni.GetIPProperties();
            var ips = props.UnicastAddresses
                .Where(ua => ua.Address.AddressFamily == AddressFamily.InterNetwork) // IPv4
                .Select(ua => ua.Address.ToString())
                .Where(ip => !ip.StartsWith("127.")) // Skip loopback
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

            // Also collect IPv6 (excluding link-local)
            var ipv6Addrs = props.UnicastAddresses
                .Where(ua => ua.Address.AddressFamily == AddressFamily.InterNetworkV6)
                .Select(ua => ua.Address.ToString())
                .Where(ip => !ip.StartsWith("fe80:")) // Skip link-local
                .ToList();
            ipAddrs.AddRange(ipv6Addrs);
        }

        // Fallback for primary IP
        if (primaryIp == null && ipAddrs.Count > 0)
        {
            primaryIp = ipAddrs.First();
        }

        // Collect system info
        var (timezone, bootTime, systemUptime) = GetSystemInfo();

        // Collect hardware info (cached)
        var hardware = CollectHardwareInfo();

        // Calculate agent uptime
        var agentUptimeSeconds = (long)(DateTime.UtcNow - _agentStartTime).TotalSeconds;

        return new InventoryData(
            Hostname: hostname,
            Fqdn: fqdn,
            Os: "Windows",
            OsVersion: osDesc,
            OsBuild: osBuild,
            Arch: arch,
            PrimaryIp: primaryIp ?? "127.0.0.1",
            IpAddrs: ipAddrs.Distinct().ToList(),
            MacAddrs: macAddrs.Distinct().ToList(),
            Domain: domain,
            Joined: joined,
            AadJoined: DetectAadJoined(),
            Timezone: timezone,
            BootTime: bootTime,
            SystemUptimeSeconds: systemUptime,
            AgentUptimeSeconds: agentUptimeSeconds,
            AgentLastRestart: _agentStartTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            Hardware: hardware
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

    private static string FormatMacAddress(PhysicalAddress mac)
    {
        var bytes = mac.GetAddressBytes();
        if (bytes.Length == 0 || bytes.All(b => b == 0))
        {
            return string.Empty;
        }
        return string.Join(":", bytes.Select(b => b.ToString("X2")));
    }

    private static string? GetOsBuild()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            return key?.GetValue("CurrentBuild")?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static (string? timezone, string? bootTime, long? uptimeSeconds) GetSystemInfo()
    {
        string? timezone = null;
        string? bootTime = null;
        long? uptimeSeconds = null;

        try
        {
            // Timezone
            timezone = TimeZoneInfo.Local.Id;

            // System uptime
            uptimeSeconds = Environment.TickCount64 / 1000;

            // Boot time (calculated from uptime)
            var bootDateTime = DateTime.UtcNow.AddSeconds(-(double)uptimeSeconds);
            bootTime = bootDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");
        }
        catch
        {
            // Silently fail
        }

        return (timezone, bootTime, uptimeSeconds);
    }

    private static bool? DetectAadJoined()
    {
        try
        {
            // Check Azure AD join status via registry
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\CloudDomainJoin\JoinInfo");
            if (key != null)
            {
                var subKeyNames = key.GetSubKeyNames();
                return subKeyNames.Length > 0;
            }
        }
        catch
        {
            // Silently fail
        }
        return null;
    }

    private HardwareData? CollectHardwareInfo()
    {
        var now = DateTime.UtcNow;
        
        // Return cached if still valid
        if (_cachedHardware != null && (now - _hardwareCacheTime) < HardwareCacheDuration)
        {
            return _cachedHardware;
        }

        try
        {
            // CPU info via WMI
            string? cpuModel = null;
            int? cpuCores = null;

            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher("SELECT Name, NumberOfCores FROM Win32_Processor");
                foreach (var obj in searcher.Get())
                {
                    cpuModel = obj["Name"]?.ToString()?.Trim();
                    cpuCores = Convert.ToInt32(obj["NumberOfCores"]);
                    break; // Take first processor
                }
            }
            catch
            {
                // WMI not available
            }

            // RAM info via WMI
            int? ramTotalGb = null;
            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
                foreach (var obj in searcher.Get())
                {
                    var totalBytes = Convert.ToUInt64(obj["TotalPhysicalMemory"]);
                    ramTotalGb = (int)(totalBytes / 1024 / 1024 / 1024);
                    break;
                }
            }
            catch
            {
                // WMI not available
            }

            // Disk info via DriveInfo
            long? diskTotalGb = null;
            long? diskFreeGb = null;

            try
            {
                long totalBytes = 0;
                long freeBytes = 0;

                foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
                {
                    totalBytes += drive.TotalSize;
                    freeBytes += drive.AvailableFreeSpace;
                }

                diskTotalGb = totalBytes / 1024 / 1024 / 1024;
                diskFreeGb = freeBytes / 1024 / 1024 / 1024;
            }
            catch
            {
                // Disk info not available
            }

            _cachedHardware = new HardwareData(
                CpuModel: cpuModel,
                CpuCores: cpuCores,
                RamTotalGb: ramTotalGb,
                DiskTotalGb: diskTotalGb,
                DiskFreeGb: diskFreeGb
            );
            _hardwareCacheTime = now;

            return _cachedHardware;
        }
        catch
        {
            return null;
        }
    }
}

public record HardwareData(
    string? CpuModel,
    int? CpuCores,
    int? RamTotalGb,
    long? DiskTotalGb,
    long? DiskFreeGb
);

public record InventoryData(
    string Hostname,
    string Fqdn,
    string Os,
    string OsVersion,
    string? OsBuild,
    string Arch,
    string PrimaryIp,
    List<string> IpAddrs,
    List<string> MacAddrs,
    string? Domain,
    bool Joined,
    bool? AadJoined,
    string? Timezone,
    string? BootTime,
    long? SystemUptimeSeconds,
    long? AgentUptimeSeconds,
    string? AgentLastRestart,
    HardwareData? Hardware
);
