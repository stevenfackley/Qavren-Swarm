using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace QavrenSwarm.Services;

/// <summary>Host-side OpenAI-compatible completion endpoint that bridges to the user's
/// logged-in Claude Code CLI (<c>claude -p</c>). Subscription credentials stay on the host
/// and never enter a container. Claude Code is used purely as a text engine — file/host
/// tools are disabled and it runs in an empty scratch directory.</summary>
public sealed class ClaudeCodeBroker
{
    private readonly SwarmConfig _config;
    private readonly ILogger<ClaudeCodeBroker> _log;
    private readonly SemaphoreSlim _concurrency = new(2, 2); // bound concurrent `claude` processes

    public ClaudeCodeBroker(SwarmConfig config, ILogger<ClaudeCodeBroker> log)
    {
        _config = config;
        _log = log;
    }

    /// <summary>Constant-time bearer check against the per-session token.</summary>
    public bool IsAuthorized(string? authorizationHeader)
    {
        if (string.IsNullOrEmpty(authorizationHeader))
            return false;
        const string scheme = "Bearer ";
        var presented = authorizationHeader.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)
            ? authorizationHeader[scheme.Length..]
            : authorizationHeader;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(presented.Trim()),
            Encoding.UTF8.GetBytes(_config.BrokerToken));
    }

    public async Task<ChatCompletionResponse> CompleteAsync(ChatCompletionRequest req, CancellationToken ct)
    {
        var prompt = FlattenMessages(req.Messages);
        var model = string.IsNullOrWhiteSpace(req.Model) ? _config.DefaultClaudeCodeModel : req.Model;

        await _concurrency.WaitAsync(ct);
        var scratch = Directory.CreateTempSubdirectory("qavren-broker-");
        try
        {
            var text = await InvokeClaudeAsync(prompt, model, scratch.FullName, ct);
            return ChatCompletionResponse.FromText(model, text);
        }
        finally
        {
            _concurrency.Release();
            try { scratch.Delete(recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    // Tools the host `claude` is forbidden from using — it must be a pure text engine.
    private static readonly string[] DisallowedTools =
        { "Bash", "Edit", "Write", "Read", "Glob", "Grep", "WebFetch", "WebSearch", "NotebookEdit", "Task" };

    private async Task<string> InvokeClaudeAsync(string prompt, string model, string cwd, CancellationToken ct)
    {
        // The prompt is fed over stdin (never argv) so it cannot be parsed as shell input.
        var bin = _config.ClaudeBin;
        var isShim = bin.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                     || bin.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
        var psi = new ProcessStartInfo
        {
            WorkingDirectory = cwd,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        // A real .exe runs directly; only .cmd/.bat shims need cmd.exe.
        if (isShim)
        {
            psi.FileName = "cmd.exe";
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(bin);
        }
        else
        {
            psi.FileName = bin;
        }
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("json");
        psi.ArgumentList.Add("--model");
        psi.ArgumentList.Add(model);
        // Variadic --disallowedTools list goes LAST so it consumes only the tool tokens.
        psi.ArgumentList.Add("--disallowedTools");
        foreach (var t in DisallowedTools)
            psi.ArgumentList.Add(t);

        // Hard wall-clock cap so a hung `claude` can't pin a concurrency slot forever.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_config.BrokerTimeoutSeconds));
        var token = timeoutCts.Token;

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        try
        {
            await proc.StandardInput.WriteAsync(prompt.AsMemory(), token);
            proc.StandardInput.Close();

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(token);
            var stderrTask = proc.StandardError.ReadToEndAsync(token);
            await proc.WaitForExitAsync(token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (proc.ExitCode != 0)
            {
                _log.LogWarning("claude exited {Code}: {Err}", proc.ExitCode, Trunc(stderr, 500));
                throw new InvalidOperationException($"claude CLI exited {proc.ExitCode}: {Trunc(stderr, 300)}");
            }
            return ExtractResultText(stdout);
        }
        catch (OperationCanceledException)
        {
            throw new InvalidOperationException($"claude CLI timed out after {_config.BrokerTimeoutSeconds}s");
        }
        finally
        {
            // Never leave an orphaned claude process holding the slot (timeout, broken pipe, etc.).
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
            catch { /* already gone */ }
        }
    }

    /// <summary>Parse `claude --output-format json` output. Modern Claude Code emits a JSON
    /// ARRAY of events ending in a {"type":"result","result":"…"} element; older versions emit
    /// a single result object. Handles both, with an assistant-text fallback.</summary>
    internal static string ExtractResultText(string stdout)
    {
        var trimmed = stdout.Trim();
        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                JsonElement? result = null;
                foreach (var el in root.EnumerateArray())
                    if (el.ValueKind == JsonValueKind.Object
                        && el.TryGetProperty("type", out var t) && t.GetString() == "result")
                        result = el; // keep the last one

                if (result is { } r)
                {
                    if (r.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True)
                        throw new InvalidOperationException("claude reported is_error=true");
                    if (r.TryGetProperty("result", out var rr) && rr.ValueKind == JsonValueKind.String)
                        return rr.GetString() ?? "";
                }

                // Fallback: concatenate assistant text blocks.
                var sb = new StringBuilder();
                foreach (var el in root.EnumerateArray())
                {
                    if (el.TryGetProperty("type", out var t) && t.GetString() == "assistant"
                        && el.TryGetProperty("message", out var msg)
                        && msg.TryGetProperty("content", out var content)
                        && content.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var block in content.EnumerateArray())
                            if (block.TryGetProperty("type", out var bt) && bt.GetString() == "text"
                                && block.TryGetProperty("text", out var txt))
                                sb.Append(txt.GetString());
                    }
                }
                if (sb.Length > 0)
                    return sb.ToString();
            }
            else if (root.ValueKind == JsonValueKind.Object
                     && root.TryGetProperty("result", out var r2) && r2.ValueKind == JsonValueKind.String)
            {
                return r2.GetString() ?? "";
            }
        }
        catch (JsonException) { /* not JSON — fall through to raw text */ }
        return trimmed;
    }

    private static string FlattenMessages(IReadOnlyList<ChatMessage> messages)
    {
        var sb = new StringBuilder();
        foreach (var m in messages)
        {
            var role = m.Role.ToLowerInvariant();
            if (role == "system")
                sb.Append("# System Instructions\n").Append(m.Content).Append("\n\n");
            else if (role == "assistant")
                sb.Append("# Previous Assistant\n").Append(m.Content).Append("\n\n");
            else
                sb.Append(m.Content).Append("\n\n");
        }
        return sb.ToString().TrimEnd();
    }

    private static string Trunc(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max];
}

// ----------------------------- OpenAI-compatible DTOs ----------------------------- #

public sealed class ChatMessage
{
    [JsonPropertyName("role")] public string Role { get; set; } = "user";
    [JsonPropertyName("content")] public string Content { get; set; } = "";
}

public sealed class ChatCompletionRequest
{
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("messages")] public List<ChatMessage> Messages { get; set; } = new();
    [JsonPropertyName("max_tokens")] public int? MaxTokens { get; set; }
    [JsonPropertyName("temperature")] public double? Temperature { get; set; }
}

public sealed class ChatCompletionResponse
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("object")] public string Object { get; set; } = "chat.completion";
    [JsonPropertyName("created")] public long Created { get; set; }
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("choices")] public List<Choice> Choices { get; set; } = new();

    public static ChatCompletionResponse FromText(string model, string text) => new()
    {
        Id = "qavren-" + Guid.NewGuid().ToString("N")[..12],
        Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        Model = model,
        Choices = new List<Choice>
        {
            new()
            {
                Index = 0,
                FinishReason = "stop",
                Message = new ChatMessage { Role = "assistant", Content = text },
            },
        },
    };

    public sealed class Choice
    {
        [JsonPropertyName("index")] public int Index { get; set; }
        [JsonPropertyName("message")] public ChatMessage Message { get; set; } = new();
        [JsonPropertyName("finish_reason")] public string FinishReason { get; set; } = "stop";
    }
}
