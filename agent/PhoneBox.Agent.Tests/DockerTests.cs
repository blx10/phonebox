using Xunit;

namespace PhoneBox.Agent.Tests;

public class DockerTests
{
    [Fact]
    public void ParsePodmanJsonArrayAndStats()
    {
        const string psJson = """
        [
          {
            "Id": "704cad0fb81e2195b08264115dc5b290057662668208a9401ad48283ea7f4c54",
            "Image": "",
            "IsInfra": true,
            "Names": ["9e7869cac400-infra"],
            "PodName": "gh-mailpit",
            "State": "running",
            "Status": "Up 3 hours"
          },
          {
            "Id": "b741041b580bbe12bd52137c4a3ed48d8204cf0af46468f3cdecfee58d2263fb",
            "Image": "localhost/atlas:dev",
            "IsInfra": false,
            "Labels": { "org.opencontainers.image.title": "atlas" },
            "Names": ["gh-dev-app"],
            "PodName": "gh-dev",
            "State": "running",
            "Status": "Up 3 hours"
          },
          {
            "Id": "0e7cc9c523ea09538781677443b9d66bb080c84748f59d796b4d1b4b652e3824",
            "Image": "docker.io/library/postgres:17",
            "IsInfra": false,
            "Labels": null,
            "Names": ["fh-postgres"],
            "PodName": "",
            "State": "exited",
            "Status": "Exited (0) 2 days ago"
          }
        ]
        """;

        const string statsJson = """
        [
          {
            "id": "b741041b580b",
            "name": "gh-dev-app",
            "cpu_percent": "1.12%",
            "avg_cpu": "1.12%",
            "mem_usage": "107.6MB / 8.29GB"
          }
        ]
        """;

        var stats = DockerMonitor.ParseStats(statsJson);
        var containers = DockerMonitor.ParseContainers(psJson, stats);

        Assert.Equal(2, containers.Count);

        var running = containers[0];
        Assert.Equal("b741041b580b", running.Id);
        Assert.Equal("gh-dev-app", running.Name);
        Assert.Equal("localhost/atlas:dev", running.Image);
        Assert.Equal("running", running.State);
        Assert.Equal("Up 3 hours", running.Status);
        Assert.Equal("gh-dev", running.Project);
        Assert.Equal(1.12, running.Cpu);
        Assert.Equal("107.6MB", running.Mem);

        var exited = containers[1];
        Assert.Equal("0e7cc9c523ea", exited.Id);
        Assert.Equal("fh-postgres", exited.Name);
        Assert.Equal("postgres:17", exited.Image);
        Assert.Equal("exited", exited.State);
        Assert.Equal("Exited (0) 2 days ago", exited.Status);
        Assert.Null(exited.Project);
        Assert.Null(exited.Cpu);
    }

    [Fact]
    public void ParsePodmanTemplateNdjsonFallback()
    {
        var started = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 7200;
        var psLines = $$"""
        {"Id":"fb0f00d224c5e64456d795385d1449b66c8e58184543f1312635199cc934cac8","Image":"docker.io/axllent/mailpit@sha256:98b9","IsInfra":false,"Labels":{"io.podman.compose.project":"mail-stack"},"Names":["gh-mailpit-smtp"],"PodName":"gh-mailpit","StartedAt":{{started}},"State":"running","Status":""}
        """;
        const string statsLines = """
        {"ContainerID":"fb0f00d224c5e64456d795385d1449b66c8e58184543f1312635199cc934cac8","Name":"gh-mailpit-smtp","CPU":0.45,"MemUsage":6549504}
        """;

        var stats = DockerMonitor.ParseStats(statsLines);
        var containers = DockerMonitor.ParseContainers(psLines, stats);

        var c = Assert.Single(containers);
        Assert.Equal("fb0f00d224c5", c.Id);
        Assert.Equal("gh-mailpit-smtp", c.Name);
        Assert.StartsWith("axllent/mailpit", c.Image);
        Assert.Equal("mail-stack", c.Project);
        Assert.StartsWith("Up 2 hours", c.Status);
        Assert.Equal(0.45, c.Cpu);
        Assert.Equal("6.5MB", c.Mem);
    }

    [Fact]
    public void ParseDockerNdjsonFormat()
    {
        const string psLines = """
        {"ID":"a1b2c3d4e5f6","Image":"redis:7-alpine","Labels":"com.docker.compose.project=webapp,com.docker.compose.service=cache","Names":"webapp-cache-1","State":"running","Status":"Up 45 minutes","HealthStatus":"healthy"}
        """;
        const string statsLines = """
        {"ID":"a1b2c3d4e5f6","CPUPerc":"2.50%","MemUsage":"24.5MiB / 7.77GiB"}
        """;

        var stats = DockerMonitor.ParseStats(statsLines);
        var containers = DockerMonitor.ParseContainers(psLines, stats);

        var c = Assert.Single(containers);
        Assert.Equal("a1b2c3d4e5f6", c.Id);
        Assert.Equal("webapp-cache-1", c.Name);
        Assert.Equal("redis:7-alpine", c.Image);
        Assert.Equal("webapp", c.Project);
        Assert.Equal("healthy", c.Health);
        Assert.Equal(2.5, c.Cpu);
        Assert.Equal("24.5MiB", c.Mem);
    }
}
