using Microsoft.Extensions.Logging.Abstractions;
using QavrenSwarm.Services;
using Xunit;

namespace QavrenSwarm.Tests;

public class BuildEnvTests
{
    // BuildEnv does not touch the Docker client, so a null client is safe here.
    private static DockerLifecycleManager Manager() =>
        new(docker: null!, new SwarmConfig(), NullLogger<DockerLifecycleManager>.Instance);

    private static JobState Job(string provider) => new()
    {
        Id = "t",
        Runtime = "python",
        Provider = provider,
        WorkspacePath = @"C:\x",
        Task = "t",
    };

    [Fact]
    public void Openai_uses_per_call_baseurl_override()
    {
        var env = Manager().BuildEnv(Job("openai"), model: null, thinkingBudget: null, nonce: "n",
            baseUrl: "http://host.docker.internal:9999/v1");

        Assert.Contains("OPENAI_BASE_URL=http://host.docker.internal:9999/v1", env);
        Assert.Contains("QAVREN_NONCE=n", env);
        Assert.Contains("QAVREN_PROVIDER=openai", env);
    }

    [Fact]
    public void Openai_without_override_uses_a_default_base_url()
    {
        var env = Manager().BuildEnv(Job("openai"), null, null, "n", baseUrl: null);

        Assert.Contains(env, e => e.StartsWith("OPENAI_BASE_URL=", System.StringComparison.Ordinal));
        Assert.DoesNotContain(env, e => e.Contains(":9999"));
    }
}
