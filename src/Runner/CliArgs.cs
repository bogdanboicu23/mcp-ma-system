using System.Globalization;

/// <summary>Tiny argument parser: <c>command --name value --flag</c>. No NuGet dependency on purpose.</summary>
internal sealed class CliArgs
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public string Command { get; private init; } = string.Empty;

    public static CliArgs Parse(string[] argv)
    {
        var result = new CliArgs { Command = argv.Length > 0 ? argv[0].ToLowerInvariant() : string.Empty };
        for (var i = 1; i < argv.Length; i++)
        {
            var token = argv[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unexpected argument '{token}'. Options look like --name value.");
            }

            var key = token[2..];
            if (i + 1 < argv.Length && !argv[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                result._values[key] = argv[i + 1];
                i++;
            }
            else
            {
                result._values[key] = "true"; // a flag without a value
            }
        }

        return result;
    }

    public bool Has(string key) => _values.ContainsKey(key);

    public string? Get(string key) => _values.TryGetValue(key, out var value) ? value : null;

    public string Get(string key, string fallback) => Get(key) ?? fallback;

    public string Require(string key) =>
        Get(key) ?? throw new ArgumentException($"Missing required option --{key}.");

    public int GetInt(string key, int fallback) =>
        Get(key) is { } v ? int.Parse(v, CultureInfo.InvariantCulture) : fallback;

    public long GetLong(string key, long fallback) =>
        Get(key) is { } v ? long.Parse(v, CultureInfo.InvariantCulture) : fallback;

    public decimal GetDecimal(string key, decimal fallback) =>
        Get(key) is { } v ? decimal.Parse(v, CultureInfo.InvariantCulture) : fallback;

    public double GetDouble(string key, double fallback) =>
        Get(key) is { } v ? double.Parse(v, CultureInfo.InvariantCulture) : fallback;
}
