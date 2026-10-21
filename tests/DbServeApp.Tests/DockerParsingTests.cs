using System.Text.Json;
using DbServeApp.Services;

namespace DbServeApp.Tests;

public class DockerParsingTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement;

    [Theory]
    [InlineData("0", 0)]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    [InlineData("garbage", 0)]
    [InlineData("512B", 512)]
    [InlineData("1kB", 1000)]
    [InlineData("1KiB", 1024)]
    [InlineData("1.5MiB", 1572864)]
    [InlineData("2GB", 2e9)]
    [InlineData("1 GiB", 1073741824)]
    public void ParseSize_HandlesUnits(string? input, double expected) =>
        Assert.Equal(expected, DockerService.ParseSize(input), 3);

    [Theory]
    [InlineData("abcdef1234567890", "abcdef123456")]
    [InlineData("sha256:abcdef1234567890", "abcdef123456")]
    [InlineData("abc", "abc")]
    public void ShortId_StripsPrefixAndTruncates(string input, string expected) =>
        Assert.Equal(expected, DockerService.ShortId(input));

    [Fact]
    public void ParseStat_ReadsDockerStatsJson()
    {
        var s = DockerService.ParseStat(J("""
            {"ID":"abc123","Name":"web","CPUPerc":"12.50%","MemPerc":"3.00%","MemUsage":"100MiB / 2GiB",
             "NetIO":"1kB / 2kB","BlockIO":"3MB / 4MB","PIDs":"7"}
            """));

        Assert.Equal("abc123", s.Id);
        Assert.Equal("web", s.Name);
        Assert.Equal(12.5, s.CpuPercent);
        Assert.Equal(3.0, s.MemPercent);
        Assert.Equal(100d * 1024 * 1024, s.MemUsedBytes);
        Assert.Equal(2d * 1024 * 1024 * 1024, s.MemLimitBytes);
        Assert.Equal(1000, s.NetRxBytes);
        Assert.Equal(2000, s.NetTxBytes);
        Assert.Equal(3e6, s.BlockReadBytes);
        Assert.Equal(4e6, s.BlockWriteBytes);
        Assert.Equal(7, s.Pids);
    }

    [Fact]
    public void ParseStat_ToleratesMissingFields()
    {
        var s = DockerService.ParseStat(J("{}"));

        Assert.Equal(0, s.CpuPercent);
        Assert.Equal(0, s.Pids);
    }

    [Fact]
    public void ParseContainer_ReadsPortsHealthAndComposeProject()
    {
        var c = DockerService.ParseContainer(J("""
            {"ID":"abc123","Names":"db","Image":"postgres:16","State":"running","Status":"Up 2 hours (healthy)",
             "Ports":"0.0.0.0:5432->5432/tcp","Command":"\"postgres\"","Networks":"bridge",
             "Labels":"a=b,com.docker.compose.project=shop,c=d"}
            """));

        Assert.Equal("db", c.Name);
        Assert.Equal("running", c.State);
        Assert.Equal("postgres:16", c.Image);
        Assert.Equal("shop", c.Project);
        Assert.Contains("5432", c.Ports);
    }

    [Fact]
    public void ParseContainer_PodmanPortObjectsAndLabelMap()
    {
        var c = DockerService.ParseContainer(J("""
            {"Id":"abc","Names":["web"],"Image":"nginx","State":"running",
             "Ports":[{"host_ip":"","host_port":8080,"container_port":80,"protocol":"tcp"}],
             "Labels":{"io.podman.compose.project":"site"}}
            """));

        Assert.Equal("web", c.Name);
        Assert.Equal("8080->80/tcp", c.Ports);
        Assert.Equal("site", c.Project);
    }

    [Fact]
    public void ParseContainer_StatusFallsBackToState()
    {
        var c = DockerService.ParseContainer(J("""{"ID":"a","Names":"x","State":"exited"}"""));

        Assert.Equal("exited", c.Status);
    }

    [Fact]
    public void ParseImage_DockerShape()
    {
        var i = DockerService.ParseImage(J("""{"ID":"sha","Repository":"redis","Tag":"7","Size":"100MB","CreatedSince":"2 weeks ago"}"""));

        Assert.Equal("redis", i.Repository);
        Assert.Equal("7", i.Tag);
        Assert.Equal("100MB", i.Size);
    }

    [Fact]
    public void ParseImage_PodmanNamesAreSplitAtLastColon()
    {
        var i = DockerService.ParseImage(J("""{"Id":"x","Names":["localhost:5000/team/app:1.2"],"Size":1000000}"""));

        Assert.Equal("localhost:5000/team/app", i.Repository);
        Assert.Equal("1.2", i.Tag);
        Assert.Equal("1MB", i.Size);
    }

    [Fact]
    public void ParseImage_RegistryPortWithoutTagIsNotSplit()
    {
        var i = DockerService.ParseImage(J("""{"Id":"x","Names":["localhost:5000/app"]}"""));

        Assert.Equal("localhost:5000/app", i.Repository);
        Assert.Equal("", i.Tag);
    }

    [Fact]
    public void ParseVolumeAndNetwork_ReadBasics()
    {
        var v = DockerService.ParseVolume(J("""{"Name":"data","Driver":"local","Mountpoint":"/var/lib/docker/volumes/data/_data"}"""));
        var n = DockerService.ParseNetwork(J("""{"ID":"n1","Name":"bridge","Driver":"bridge"}"""));

        Assert.Equal("data", v.Name);
        Assert.Equal("/var/lib/docker/volumes/data/_data", v.Mountpoint);
        Assert.Equal("local", n.Scope);
    }

    [Fact]
    public void ParseHost_DockerInfo()
    {
        var h = DockerService.ParseHost("""
            {"ServerVersion":"29.0.0","OperatingSystem":"Ubuntu 24.04","KernelVersion":"6.6","Architecture":"x86_64","NCPU":8,
             "MemTotal":33000000000,"Driver":"overlayfs","DockerRootDir":"/var/lib/docker","CgroupVersion":"2","Name":"box",
             "LoggingDriver":"json-file","DefaultRuntime":"runc","Warnings":["swap limit missing"]}
            """);

        Assert.NotNull(h);
        Assert.Equal("29.0.0", h!.ServerVersion);
        Assert.Equal(8, h.Cpus);
        Assert.Equal("json-file", h.LoggingDriver);
        Assert.Equal("runc", h.DefaultRuntime);
        Assert.Equal(["swap limit missing"], h.Warnings);
    }

    [Fact]
    public void ParseHost_PodmanInfo()
    {
        var h = DockerService.ParseHost("""
            {"host":{"arch":"amd64","cpus":4,"memTotal":8000000000,"kernel":"6.1","hostname":"pm","logDriver":"journald",
                     "distribution":{"distribution":"fedora","version":"40"},"ociRuntime":{"name":"crun"},"cgroupVersion":"v2"},
             "store":{"graphDriverName":"overlay","graphRoot":"/root/storage"},"version":{"Version":"5.0.0"}}
            """);

        Assert.NotNull(h);
        Assert.Equal("5.0.0", h!.ServerVersion);
        Assert.Equal("fedora 40", h.OperatingSystem);
        Assert.Equal("crun", h.DefaultRuntime);
        Assert.Equal("overlay", h.StorageDriver);
    }

    [Fact]
    public void ParseHost_InvalidJson_ReturnsNull() => Assert.Null(DockerService.ParseHost("not json"));

    [Fact]
    public void ParseHostMetrics_ReadsProcAndDf()
    {
        var m = DockerService.ParseHostMetrics(
            "0.50 0.40 0.30 2/300 1234\n" +
            "3600.55 7000.10\n" +
            "MemTotal:       32000 kB\nMemAvailable:   16000 kB\nSwapTotal:       8000 kB\nSwapFree:        8000 kB\n" +
            "Filesystem 1024-blocks Used Available Capacity Mounted on\n/dev/sdc 1000 250 750 25% /");

        Assert.NotNull(m);
        Assert.Equal(0.5, m!.Load1);
        Assert.Equal(0.3, m.Load15);
        Assert.Equal(3600.55, m.UptimeSeconds);
        Assert.Equal(32000d * 1024, m.MemTotalBytes);
        Assert.Equal(16000d * 1024, m.MemAvailableBytes);
        Assert.Equal(1000d * 1024, m.DiskTotalBytes);
        Assert.Equal(250d * 1024, m.DiskUsedBytes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("only one line")]
    public void ParseHostMetrics_RejectsIncompleteOutput(string text) => Assert.Null(DockerService.ParseHostMetrics(text));

    [Fact]
    public void ParseDetail_ReadsStateMountsAndNetworks()
    {
        var d = DockerService.ParseDetail(J("""
            {"Id":"abcdef1234567890","Created":"2026-10-21T07:18:58.1871369Z","Image":"sha256:0f51e63e","RestartCount":3,
             "State":{"StartedAt":"2026-10-21T07:18:58.2401458Z","FinishedAt":"0001-01-01T00:00:00Z","ExitCode":137,"OOMKilled":true,"Pid":99,
                      "Health":{"Status":"unhealthy","Log":[{"Output":"old"},{"Output":" connection refused \n"}]}},
             "HostConfig":{"RestartPolicy":{"Name":"unless-stopped"},"Memory":536870912,"NanoCpus":1500000000},
             "Mounts":[{"Type":"volume","Name":"data","Source":"/v/data","Destination":"/var/lib/data","RW":true},
                       {"Type":"bind","Source":"/etc","Destination":"/cfg","RW":false}],
             "NetworkSettings":{"Networks":{"bridge":{"IPAddress":"172.18.0.5","Gateway":"172.18.0.1"}}}}
            """));

        Assert.Equal(3, d.RestartCount);
        Assert.Equal(137, d.ExitCode);
        Assert.True(d.OomKilled);
        Assert.Equal("unless-stopped", d.RestartPolicy);
        Assert.Equal(536870912, d.MemLimitBytes);
        Assert.Equal(1.5, d.CpuLimit);
        Assert.Equal(99, d.Pid);
        Assert.Equal("unhealthy", d.HealthStatus);
        Assert.Equal("connection refused", d.HealthOutput);
        Assert.NotNull(d.StartedAt);
        Assert.Equal(2026, d.StartedAt!.Value.Year);
        Assert.Null(d.FinishedAt); // Docker's zero date means "never"

        Assert.Equal(2, d.Mounts.Count);
        Assert.True(d.Mounts[0].ReadWrite);
        Assert.False(d.Mounts[1].ReadWrite);
        var net = Assert.Single(d.Networks);
        Assert.Equal(("bridge", "172.18.0.5", "172.18.0.1"), (net.Network, net.Ip, net.Gateway));
    }

    [Fact]
    public void ParseDetail_CpuQuotaIsConvertedToCpus()
    {
        var d = DockerService.ParseDetail(J("""{"Id":"a","HostConfig":{"CpuQuota":50000,"CpuPeriod":100000}}"""));

        Assert.Equal(0.5, d.CpuLimit);
    }

    [Fact]
    public void ParseDetail_ToleratesMinimalJson()
    {
        var d = DockerService.ParseDetail(J("""{"Id":"a"}"""));

        Assert.Equal(0, d.RestartCount);
        Assert.False(d.OomKilled);
        Assert.Empty(d.Mounts);
        Assert.Empty(d.Networks);
    }

    [Fact]
    public void ParseEvent_ReadsActionNameAndExitCode()
    {
        var ev = DockerService.ParseEvent(J("""
            {"Type":"container","Action":"die","Actor":{"Attributes":{"name":"web","exitCode":"1","image":"nginx"}},"timeNano":1760000000000000000}
            """));

        Assert.Equal("die", ev.Action);
        Assert.Equal("web", ev.Name);
        Assert.Equal("exit code 1", ev.Detail);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1760000000), ev.Time);
    }

    [Fact]
    public void ParseEvent_FallsBackToSecondsAndImageDetail()
    {
        var ev = DockerService.ParseEvent(J("""
            {"Type":"container","status":"start","Actor":{"Attributes":{"name":"web","image":"nginx"}},"time":1760000000}
            """));

        Assert.Equal("start", ev.Action);
        Assert.Equal("nginx", ev.Detail);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1760000000), ev.Time);
    }

    [Fact]
    public void ParseNetworkExtra_DockerIpam()
    {
        var (id, extra) = DockerService.ParseNetworkExtra(J("""
            {"Id":"7abc04cc8853ffff","Internal":true,"IPAM":{"Config":[{"Subnet":"172.18.0.0/16","Gateway":"172.18.0.1"}]},
             "Containers":{"x":{"Name":"db","IPv4Address":"172.18.0.5/16"},"y":{"Name":"cache","IPv4Address":""}}}
            """));

        Assert.Equal("7abc04cc8853ffff", id);
        Assert.Equal("172.18.0.0/16", extra.Subnet);
        Assert.Equal("172.18.0.1", extra.Gateway);
        Assert.True(extra.Internal);
        Assert.Equal(["db (172.18.0.5)", "cache"], extra.Attached);
    }

    [Fact]
    public void ParseNetworkExtra_PodmanSubnets()
    {
        var (_, extra) = DockerService.ParseNetworkExtra(J("""{"id":"p1","subnets":[{"subnet":"10.89.0.0/24","gateway":"10.89.0.1"}]}"""));

        Assert.Equal("10.89.0.0/24", extra.Subnet);
        Assert.Equal("10.89.0.1", extra.Gateway);
    }
}
