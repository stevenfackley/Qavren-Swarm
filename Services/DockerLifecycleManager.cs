using System.Formats.Tar;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging;

namespace QavrenSwarm.Services;

/// <summary>Owns the container lifecycle: lazily builds the agent images from the
/// embedded build context, then for each job mounts the workspace read-only, runs the
/// agent, captures its stdout, and tears the container down.</summary>
public sealed class DockerLifecycleManager
{
    private readonly IDockerClient _docker;
    private readonly SwarmConfig _config;
    private readonly ILogger<DockerLifecycleManager> _log;

    // One build lock so concurrent spawns don't race a duplicate build.
    private readonly SemaphoreSlim _buildGate = new(1, 1);
    // Thread-safe: read/written from parallel swarm spawns outside the build gate.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _builtRuntimes = new();


    public DockerLifecycleManager(IDockerClient docker, SwarmConfig config, ILogger<DockerLifecycleManager> log)
    {
        _docker = docker;
        _config = config;
        _log = log;
    }

    private static (string image, string dockerfileResource) ImageFor(string runtime) => runtime switch
    {
        "node" => ("qavren-agent-node:latest", "qavren.agent.Dockerfile.node"),
        "python" => ("qavren-agent-python:latest", "qavren.agent.Dockerfile.python"),
        _ => throw new ArgumentException($"unknown runtime '{runtime}' (expected node|python)"),
    };

    // ------------------------------------------------------------------ #
    // Image build (lazy, idempotent)
    // ------------------------------------------------------------------ #

    public async Task EnsureImageAsync(string runtime, CancellationToken ct)
    {
        var (image, dockerfileRes) = ImageFor(runtime);
        if (_builtRuntimes.ContainsKey(runtime) || await ImageExistsAsync(image, ct))
        {
            _builtRuntimes[runtime] = 0;
            return;
        }

        await _buildGate.WaitAsync(ct);
        try
        {
            if (_builtRuntimes.ContainsKey(runtime) || await ImageExistsAsync(image, ct))
            {
                _builtRuntimes[runtime] = 0;
                return;
            }

            _log.LogInformation("Building agent image {Image}…", image);
            await using var context = BuildContextTar(dockerfileRes);
            var parameters = new ImageBuildParameters
            {
                Dockerfile = "Dockerfile",
                Tags = new List<string> { image },
                Remove = true,
                ForceRemove = true,
            };
            var progress = new Progress<JSONMessage>(m =>
            {
                var line = m.Stream ?? m.Status ?? m.ErrorMessage;
                if (!string.IsNullOrWhiteSpace(line))
                    _log.LogDebug("[build {Image}] {Line}", image, line.TrimEnd());
            });

            await _docker.Images.BuildImageFromDockerfileAsync(
                parameters, context, authConfigs: null, headers: null, progress, ct);

            _builtRuntimes[runtime] = 0;
            _log.LogInformation("Built {Image}.", image);
        }
        finally
        {
            _buildGate.Release();
        }
    }

    private async Task<bool> ImageExistsAsync(string image, CancellationToken ct)
    {
        var images = await _docker.Images.ListImagesAsync(new ImagesListParameters
        {
            Filters = new Dictionary<string, IDictionary<string, bool>>
            {
                ["reference"] = new Dictionary<string, bool> { [image] = true },
            },
        }, ct);
        return images.Count > 0;
    }

    /// <summary>Assemble an in-memory tar build context: the chosen Dockerfile (renamed to
    /// "Dockerfile") plus agent.py and requirements.txt, all read from embedded resources.</summary>
    private static Stream BuildContextTar(string dockerfileResource)
    {
        var ms = new MemoryStream();
        using (var tar = new TarWriter(ms, TarEntryFormat.Pax, leaveOpen: true))
        {
            AddEntry(tar, "Dockerfile", ReadResource(dockerfileResource));
            AddEntry(tar, "agent.py", ReadResource("qavren.agent.agent.py"));
            AddEntry(tar, "requirements.txt", ReadResource("qavren.agent.requirements.txt"));
        }
        ms.Position = 0;
        return ms;
    }

    private static void AddEntry(TarWriter tar, string name, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
        {
            DataStream = new MemoryStream(bytes),
            Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
        };
        tar.WriteEntry(entry);
    }

    private static string ReadResource(string logicalName)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException($"embedded resource missing: {logicalName}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    // ------------------------------------------------------------------ #
    // Run one job
    // ------------------------------------------------------------------ #

    public async Task RunAgentAsync(JobStateStore store, JobState job, string? model, int? thinkingBudget, CancellationToken ct)
    {
        string? containerId = null;
        try
        {
            store.Update(job.Id, j => j.Status = JobStatus.Running);
            await EnsureImageAsync(job.Runtime, ct);

            var (image, _) = ImageFor(job.Runtime);
            // Per-job nonce: the agent stamps it into the output markers; the model never sees it.
            var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
            var env = BuildEnv(job, model, thinkingBudget, nonce);

            var create = await _docker.Containers.CreateContainerAsync(new CreateContainerParameters
            {
                Image = image,
                Env = env,
                Tty = false,
                AttachStdout = true,
                AttachStderr = true,
                HostConfig = new HostConfig
                {
                    // Mounts (not Binds): Binds split on ':' and mangle the Windows drive-letter colon.
                    Mounts = new List<Mount>
                    {
                        new() { Type = "bind", Source = job.WorkspacePath, Target = "/workspace", ReadOnly = true },
                    },
                    // Make host.docker.internal resolve inside Linux containers on Docker Desktop.
                    ExtraHosts = new List<string> { "host.docker.internal:host-gateway" },
                    AutoRemove = false,
                    // --- Hardening: the container runs untrusted LLM-driven code + workspace test code. ---
                    CapDrop = new List<string> { "ALL" },                 // no Linux capabilities
                    SecurityOpt = new List<string> { "no-new-privileges:true" }, // block setuid escalation
                    PidsLimit = _config.PidsLimit,                        // fork-bomb guard
                    Memory = _config.MemoryBytes,                         // OOM guard
                    NanoCPUs = _config.NanoCpus,                          // CPU cap
                    // Optional egress lockdown (e.g. "none") — left at bridge by default so
                    // remote providers (anthropic) and dependency installs still work.
                    NetworkMode = string.IsNullOrWhiteSpace(_config.NetworkMode) ? null : _config.NetworkMode,
                },
            }, ct);

            containerId = create.ID;
            store.Update(job.Id, j => j.ContainerId = containerId);

            await _docker.Containers.StartContainerAsync(containerId, new ContainerStartParameters(), ct);
            var wait = await _docker.Containers.WaitContainerAsync(containerId, ct);

            using var logs = await _docker.Containers.GetContainerLogsAsync(
                containerId, tty: false,
                new ContainerLogsParameters { ShowStdout = true, ShowStderr = true, Follow = false }, ct);
            var (stdout, stderr) = await logs.ReadOutputToEndAsync(ct);

            var (diff, testsPassed, failedHunks) = ParseAgentOutput(stdout, nonce);
            var ok = wait.StatusCode == 0;

            store.Update(job.Id, j =>
            {
                j.ExitCode = wait.StatusCode;
                j.Diff = diff;
                j.TestsPassed = testsPassed;
                j.FailedHunks = failedHunks;
                j.StdErrTail = Tail(stderr, 4000);
                j.FinishedUtc = DateTimeOffset.UtcNow;
                j.Status = ok ? JobStatus.Completed : JobStatus.Failed;
                if (!ok)
                    j.Error = $"agent exited {wait.StatusCode}";
            });
            _log.LogInformation("Job {Id} finished exit={Exit} changed={Changed}", job.Id, wait.StatusCode, diff.Trim().Length > 0);
        }
        catch (OperationCanceledException)
        {
            _log.LogWarning("Job {Id} cancelled or timed out", job.Id);
            store.Update(job.Id, j =>
            {
                j.Status = JobStatus.Failed;
                j.Error = "cancelled or timed out";
                j.FinishedUtc = DateTimeOffset.UtcNow;
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Job {Id} failed", job.Id);
            store.Update(job.Id, j =>
            {
                j.Status = JobStatus.Failed;
                j.Error = $"{ex.GetType().Name}: {ex.Message}";
                j.FinishedUtc = DateTimeOffset.UtcNow;
            });
        }
        finally
        {
            if (containerId is not null)
            {
                try
                {
                    await _docker.Containers.RemoveContainerAsync(
                        containerId, new ContainerRemoveParameters { Force = true }, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Failed to remove container {Id}", containerId);
                }
            }
        }
    }

    private List<string> BuildEnv(JobState job, string? model, int? thinkingBudget, string nonce)
    {
        var env = new List<string>
        {
            $"QAVREN_TASK={job.Task}",
            $"QAVREN_RUNTIME={job.Runtime}",
            $"QAVREN_NONCE={nonce}",
        };

        switch (job.Provider)
        {
            case "anthropic":
                var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
                    ?? throw new InvalidOperationException(
                        "provider 'anthropic' requires ANTHROPIC_API_KEY in the server environment");
                env.Add("QAVREN_PROVIDER=anthropic");
                env.Add($"ANTHROPIC_API_KEY={apiKey}");
                env.Add($"ANTHROPIC_MODEL={model ?? _config.DefaultAnthropicModel}");
                env.Add($"THINKING_BUDGET={thinkingBudget ?? _config.DefaultThinkingBudget}");
                break;

            case "claude-code":
                // The container speaks OpenAI; the host broker bridges to `claude -p`.
                env.Add("QAVREN_PROVIDER=openai");
                env.Add($"OPENAI_BASE_URL=http://host.docker.internal:{_config.BrokerPort}/v1");
                env.Add($"OPENAI_API_KEY={_config.BrokerToken}");
                env.Add($"OPENAI_MODEL={model ?? _config.DefaultClaudeCodeModel}");
                break;

            case "openai":
                var oaKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
                env.Add("QAVREN_PROVIDER=openai");
                env.Add($"OPENAI_BASE_URL={_config.DefaultOpenAiBaseUrl}");
                env.Add($"OPENAI_API_KEY={(string.IsNullOrEmpty(oaKey) ? "local" : oaKey)}");
                env.Add($"OPENAI_MODEL={model ?? _config.DefaultOpenAiModel}");
                break;

            default:
                throw new ArgumentException($"unknown provider '{job.Provider}'");
        }
        return env;
    }

    /// <summary>Extract the nonce-stamped diff envelope and trailing result JSON from the
    /// agent's stdout. The nonce makes the markers unforgeable by injected task text.
    /// Internal for unit testing.</summary>
    internal static (string diff, bool? testsPassed, int failedHunks) ParseAgentOutput(string stdout, string nonce)
    {
        var suffix = string.IsNullOrEmpty(nonce) ? "" : $":{nonce}";
        var diffStart = $"===QAVREN_DIFF_START{suffix}===";
        var diffEnd = $"===QAVREN_DIFF_END{suffix}===";
        var resultPrefix = $"QAVREN_RESULT{suffix}=";

        // NB: do NOT normalize CRLF here — a CRLF file's diff carries CR bytes that must
        // survive so the patch applies cleanly to the (still-CRLF) host file.
        var diff = "";
        var start = stdout.IndexOf(diffStart, StringComparison.Ordinal);
        var end = stdout.IndexOf(diffEnd, StringComparison.Ordinal);
        if (start >= 0 && end > start)
        {
            var body = stdout[(start + diffStart.Length)..end];
            // Strip only the single framing newline emitted right after the start marker;
            // the diff's own internal/trailing newlines (and CRs) are preserved.
            if (body.StartsWith("\r\n", StringComparison.Ordinal)) body = body[2..];
            else if (body.StartsWith("\n", StringComparison.Ordinal)) body = body[1..];
            diff = body.Trim().Length == 0 ? "" : body;
        }

        bool? testsPassed = null;
        var failedHunks = 0;
        var idx = stdout.LastIndexOf(resultPrefix, StringComparison.Ordinal);
        if (idx >= 0)
        {
            var lineEnd = stdout.IndexOf('\n', idx);
            var json = lineEnd < 0 ? stdout[(idx + resultPrefix.Length)..] : stdout[(idx + resultPrefix.Length)..lineEnd];
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json.Trim());
                var root = doc.RootElement;
                if (root.TryGetProperty("testsPassed", out var tp) && tp.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
                    testsPassed = tp.GetBoolean();
                if (root.TryGetProperty("failedHunks", out var fh) && fh.TryGetInt32(out var n))
                    failedHunks = n;
            }
            catch (System.Text.Json.JsonException)
            {
                // Malformed result line — leave defaults; the diff is still authoritative.
            }
        }
        return (diff, testsPassed, failedHunks);
    }

    private static string Tail(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[^max..];
}
