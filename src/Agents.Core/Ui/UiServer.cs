using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agents.Core.Runs;

namespace Agents.Core.Ui;

/// <summary>
/// A tiny read-only web UI for the results folder: one HTML page (embedded, no external files, works offline) and two
/// JSON endpoints. It only reads <c>results/runs/*</c> and the labels CSV; it never changes anything and listens on
/// 127.0.0.1 only.
/// </summary>
public sealed class UiServer
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly RunStore _store;
    private readonly string? _labelsPath;

    public UiServer(RunStore store, string? labelsPath = null)
    {
        _store = store;
        _labelsPath = labelsPath;
    }

    public sealed record Response(int Status, string ContentType, byte[] Body);

    /// <summary>Pure request handler (also used by the tests): path and query in, bytes out.</summary>
    public Response Handle(string path, IReadOnlyDictionary<string, string> query)
    {
        switch (path)
        {
            case "/" or "/index.html":
                return new Response(200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(PageHtml()));
            case "/api/data":
                return Ok(BuildData());
            case "/api/run":
                return query.TryGetValue("id", out var id) ? RunDetail(id) : Fail(400, "missing id");
            default:
                return Fail(404, "not found");
        }
    }

    public async Task ServeAsync(int port, CancellationToken ct)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        using var stop = ct.Register(() => listener.Close());

        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (ct.IsCancellationRequested || !listener.IsListening)
            {
                break;
            }

            try
            {
                var query = new Dictionary<string, string>();
                foreach (var key in context.Request.QueryString.AllKeys.Where(k => k is not null))
                {
                    query[key!] = context.Request.QueryString[key] ?? string.Empty;
                }

                var response = Handle(context.Request.Url?.AbsolutePath ?? "/", query);
                context.Response.StatusCode = response.Status;
                context.Response.ContentType = response.ContentType;
                context.Response.Headers["Cache-Control"] = "no-store";
                context.Response.ContentLength64 = response.Body.Length;
                await context.Response.OutputStream.WriteAsync(response.Body, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }
    }

    private static string PageHtml()
    {
        var assembly = typeof(UiServer).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("index.html", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private object BuildData()
    {
        var labels = ReadLabels();
        var runs = _store.ListManifests()
            .OrderBy(m => m.Config, StringComparer.Ordinal)
            .ThenBy(m => m.InstanceId, StringComparer.Ordinal)
            .ThenBy(m => m.Repeat)
            .Select(m =>
            {
                labels.TryGetValue(m.InstanceId, out var label);
                return new
                {
                    manifest = m,
                    resolved = _store.ReadVerdict(m.RunId)?.Resolved,
                    label = label?.Label,
                    pHat = label?.PHat,
                };
            })
            .ToList();

        return new { root = _store.Root, labelsFound = labels.Count > 0, runs };
    }

    private Response RunDetail(string id)
    {
        var manifest = _store.ListManifests().FirstOrDefault(m => m.RunId == id);
        if (manifest is null)
        {
            return Fail(404, "unknown run");
        }

        var dir = _store.RunDir(manifest.RunId);
        var calls = new JsonArray();
        var callsPath = Path.Combine(dir, "calls.jsonl");
        if (File.Exists(callsPath))
        {
            foreach (var line in File.ReadLines(callsPath).Where(l => l.Length > 0))
            {
                try
                {
                    calls.Add(JsonNode.Parse(line));
                }
                catch (JsonException)
                {
                    // a half-written last line of a run in progress
                }
            }
        }

        var patchPath = Path.Combine(dir, "patch.diff");
        return Ok(new
        {
            manifest,
            verdict = _store.ReadVerdict(manifest.RunId),
            calls,
            patch = File.Exists(patchPath) ? File.ReadAllText(patchPath) : string.Empty,
        });
    }

    private sealed record LabelRow(string Label, double PHat);

    private Dictionary<string, LabelRow> ReadLabels()
    {
        var result = new Dictionary<string, LabelRow>();
        var candidates = new[]
        {
            _labelsPath,
            Path.Combine(_store.Root, "labels.csv"),
            Path.Combine("tasks", "labels.csv"),
        };

        var path = candidates.FirstOrDefault(p => p is not null && File.Exists(p));
        if (path is null)
        {
            return result;
        }

        foreach (var line in File.ReadLines(path).Where(l => l.Length > 0 && !l.StartsWith('#') && !l.StartsWith("instance_id", StringComparison.Ordinal)))
        {
            var cells = line.Split(',');
            if (cells.Length >= 7 && double.TryParse(cells[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var p))
            {
                result[cells[0]] = new LabelRow(cells[6].Trim().ToLowerInvariant(), p);
            }
        }

        return result;
    }

    private static Response Ok(object value) =>
        new(200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(value, Json));

    private static Response Fail(int status, string message) =>
        new(status, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(new { error = message }, Json));
}
