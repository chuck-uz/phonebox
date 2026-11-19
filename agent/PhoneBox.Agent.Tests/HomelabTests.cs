using Xunit;

namespace PhoneBox.Agent.Tests;

public class HomelabTests
{
    private static string Scrape(double idle, double user, double rx, double tx) => $$"""
    # HELP node_cpu_seconds_total Seconds the CPUs spent in each mode.
    # TYPE node_cpu_seconds_total counter
    node_cpu_seconds_total{cpu="0",mode="idle"} {{idle.ToString(System.Globalization.CultureInfo.InvariantCulture)}}
    node_cpu_seconds_total{cpu="0",mode="user"} {{user.ToString(System.Globalization.CultureInfo.InvariantCulture)}}
    node_cpu_seconds_total{cpu="0",mode="iowait"} 5
    node_cpu_seconds_total{cpu="1",mode="idle"} {{idle.ToString(System.Globalization.CultureInfo.InvariantCulture)}}
    node_cpu_seconds_total{cpu="1",mode="user"} {{user.ToString(System.Globalization.CultureInfo.InvariantCulture)}}
    node_memory_MemTotal_bytes 1.7179869184e+10
    node_memory_MemAvailable_bytes 4.294967296e+09
    node_network_receive_bytes_total{device="eth0"} {{rx.ToString(System.Globalization.CultureInfo.InvariantCulture)}}
    node_network_transmit_bytes_total{device="eth0"} {{tx.ToString(System.Globalization.CultureInfo.InvariantCulture)}}
    node_network_receive_bytes_total{device="veth1"} 9e+12
    node_network_receive_bytes_total{device="vmbr0"} 9e+12
    node_network_receive_bytes_total{device="lo"} 9e+12
    node_filesystem_size_bytes{device="/dev/sda1",fstype="ext4",mountpoint="/"} 1.073741824e+11
    node_filesystem_avail_bytes{device="/dev/sda1",fstype="ext4",mountpoint="/"} 5.36870912e+10
    node_filesystem_size_bytes{device="tmpfs",fstype="tmpfs",mountpoint="/run"} 1.073741824e+09
    node_hwmon_temp_celsius{chip="platform_coretemp_0",sensor="temp1"} 51.6
    node_hwmon_temp_celsius{chip="nvme_nvme0",sensor="temp1"} 70
    node_load1 0.75
    node_boot_time_seconds 1.0e+09
    node_time_seconds 1.000090000e+09
    node_uname_info{nodename="lab",release="6.8.12-4-pve",sysname="Linux"} 1
    """;

    [Fact]
    public void Two_scrapes_give_cpu_and_network_rates_from_real_ports_only()
    {
        var st = HomelabMonitor.NewState();
        var first = HomelabMonitor.Build("lab", HomelabMonitor.Parse(Scrape(100, 100, 1000, 500)), st, 1_000_000);
        Assert.Null(first.CpuPct);
        Assert.Null(first.RxBps);

        // 10 s later: per CPU 6 s user + 4 s idle -> 60 % busy; eth0 +10 000 B rx and +5 000 B tx
        var second = HomelabMonitor.Build("lab", HomelabMonitor.Parse(Scrape(104, 106, 11_000, 5_500)), st, 1_010_000);
        Assert.Equal(60, second.CpuPct);
        Assert.Equal(1000, second.RxBps);
        Assert.Equal(500, second.TxBps);
        Assert.Equal(2, second.Cpus);
        Assert.Equal([60.0], second.CpuHist);
    }

    [Fact]
    public void Memory_disks_temperature_uptime_and_os()
    {
        var s = HomelabMonitor.Build("lab", HomelabMonitor.Parse(Scrape(1, 1, 1, 1)), HomelabMonitor.NewState(), 1);
        Assert.Equal(12, s.MemUsedGb);
        Assert.Equal(16, s.MemTotalGb);
        var disk = Assert.Single(s.Disks);
        Assert.Equal("/", disk.Mount);
        Assert.Equal(50, disk.UsedGb);
        Assert.Equal(52, s.TempC);          // CPU sensor, not the hotter NVMe one
        Assert.Equal(90_000, s.UptimeSec);
        Assert.Equal("Proxmox", s.Os);
        Assert.Equal("lab", s.Node);
        Assert.Equal(0.75, s.Load1);
    }

    [Fact]
    public void A_counter_reset_gives_no_rate_instead_of_a_negative_one()
    {
        var st = HomelabMonitor.NewState();
        HomelabMonitor.Build("lab", HomelabMonitor.Parse(Scrape(100, 100, 9000, 9000)), st, 0 + 1);
        var afterReboot = HomelabMonitor.Build("lab", HomelabMonitor.Parse(Scrape(1, 1, 10, 10)), st, 10_001);
        Assert.Null(afterReboot.CpuPct);
        Assert.Null(afterReboot.RxBps);
    }

    [Theory]
    [InlineData("100.64.0.10:9100", "http://100.64.0.10:9100/metrics")]
    [InlineData("http://lab:9100/", "http://lab:9100/metrics")]
    [InlineData("http://lab:9100/custom", "http://lab:9100/custom")]
    public void Addresses_get_the_metrics_path(string input, string expected) => Assert.Equal(expected, HomelabMonitor.MetricsUrl(input));

    [Fact]
    public void The_phone_config_never_carries_server_addresses()
    {
        var cfg = new AgentConfig();
        cfg.Homelab.Hosts.Add(new HomelabHost { Name = "lab", Url = "http://10.9.8.7:9100" });
        Assert.DoesNotContain("10.9.8.7", System.Text.Json.JsonSerializer.Serialize(cfg.ToPublic()));
    }
}
