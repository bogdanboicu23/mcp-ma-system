using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agents.Core;

/// <summary>One line of the experiment log: everything about a single model call.</summary>
public sealed record CallRecord(
    string RunId,
    string Config,
    string AgentId,
    string Role,
    int Turn,
    DateTimeOffset Timestamp,
    long LatencyMs,
    Usage Usage,
    decimal CostUsd,
    long EstimatedPromptTokens,
    int StubbedToolResults,
    int ToolCalls,
    string StopReason,
    TokenAttribution? Attribution = null);

public interface ICallLog
{
    void Write(CallRecord record);
}

public sealed class NullCallLog : ICallLog
{
    public void Write(CallRecord record)
    {
    }
}

public sealed class MemoryCallLog : ICallLog
{
    private readonly object _lock = new();
    private readonly List<CallRecord> _records = new();

    public IReadOnlyList<CallRecord> Records
    {
        get
        {
            lock (_lock)
            {
                return _records.ToList();
            }
        }
    }

    public void Write(CallRecord record)
    {
        lock (_lock)
        {
            _records.Add(record);
        }
    }
}

/// <summary>Append-only JSON Lines file; safe to share between parallel agents.</summary>
public sealed class JsonlCallLog : ICallLog
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly object _lock = new();
    private readonly string _path;

    public JsonlCallLog(string path)
    {
        _path = path;
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    public void Write(CallRecord record)
    {
        var line = JsonSerializer.Serialize(record, Options);
        lock (_lock)
        {
            File.AppendAllText(_path, line + Environment.NewLine);
        }
    }
}
