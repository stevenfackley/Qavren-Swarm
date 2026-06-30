using System.Security.Cryptography;

namespace QavrenSwarm.Services;

/// <summary>Process-wide configuration + the per-session broker token. Read once at
/// startup from environment variables so both home (claude-code) and work (openai/local)
/// boxes are configured the same way.</summary>
public sealed class SwarmConfig
{
    public int BrokerPort { get; }
    public string BrokerToken { get; }
    public bool BrokerEnabled { get; }
    public string ClaudeBin { get; }

    public string DefaultProvider { get; }
    public string DefaultAnthropicModel { get; }
    public string DefaultClaudeCodeModel { get; }
    public string DefaultOpenAiBaseUrl { get; }
    public string DefaultOpenAiModel { get; }
    public int DefaultThinkingBudget { get; }
    public int JobTimeoutSeconds { get; }
    public int PauseGraceSeconds { get; }
    public int BrokerTimeoutSeconds { get; }
    public int PidsLimit { get; }
    public long MemoryBytes { get; }
    public long NanoCpus { get; }
    public string? NetworkMode { get; }
    public bool PersistJobs { get; }
    public string JobsDir { get; }

    public SwarmConfig()
    {
        BrokerPort = ParseInt("QAVREN_BROKER_PORT", 8787);
        // A fresh random token each launch; injected into containers, validated by the broker.
        BrokerToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        BrokerEnabled = ParseBool("QAVREN_BROKER_ENABLED", true);
        ClaudeBin = Env("QAVREN_CLAUDE_BIN", "claude");

        DefaultProvider = Env("QAVREN_DEFAULT_PROVIDER", "claude-code").ToLowerInvariant();
        DefaultAnthropicModel = Env("QAVREN_ANTHROPIC_MODEL", "claude-sonnet-4-6");
        DefaultClaudeCodeModel = Env("QAVREN_CLAUDE_CODE_MODEL", "sonnet"); // CLI alias, drift-proof
        DefaultOpenAiBaseUrl = Env("QAVREN_OPENAI_BASE_URL", "http://host.docker.internal:11434/v1");
        DefaultOpenAiModel = Env("QAVREN_OPENAI_MODEL", "qwen2.5-coder");
        DefaultThinkingBudget = ParseInt("QAVREN_THINKING_BUDGET", 8000);
        JobTimeoutSeconds = ParseInt("QAVREN_JOB_TIMEOUT_SECONDS", 900);     // 15 min wall-clock cap per job
        PauseGraceSeconds = ParseInt("QAVREN_PAUSE_GRACE_SECONDS", 1800);    // 30 min to resume a hung (Paused) job before it's reaped
        BrokerTimeoutSeconds = ParseInt("QAVREN_BROKER_TIMEOUT_SECONDS", 300); // 5 min cap per `claude -p`
        PidsLimit = ParseInt("QAVREN_PIDS_LIMIT", 512);                      // fork-bomb guard
        MemoryBytes = (long)ParseInt("QAVREN_MEMORY_MB", 2048) * 1024 * 1024; // OOM guard (0 = unlimited)
        NanoCpus = (long)(ParseDouble("QAVREN_CPUS", 2.0) * 1_000_000_000d);  // CPU cap (0 = unlimited)
        NetworkMode = Environment.GetEnvironmentVariable("QAVREN_NETWORK_MODE"); // null=bridge; "none" for offline
        PersistJobs = ParseBool("QAVREN_PERSIST_JOBS", true);                 // jobs survive restarts
        JobsDir = Env("QAVREN_JOBS_DIR", Path.Combine(AppContext.BaseDirectory, "jobs"));
    }

    private static string Env(string key, string fallback) =>
        Environment.GetEnvironmentVariable(key) is { Length: > 0 } v ? v : fallback;

    private static int ParseInt(string key, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(key), out var n) ? n : fallback;

    private static bool ParseBool(string key, bool fallback) =>
        bool.TryParse(Environment.GetEnvironmentVariable(key), out var b) ? b : fallback;

    private static double ParseDouble(string key, double fallback) =>
        double.TryParse(Environment.GetEnvironmentVariable(key), out var d) ? d : fallback;
}
