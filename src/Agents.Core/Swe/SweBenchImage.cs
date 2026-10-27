namespace Agents.Core.Swe;

public sealed record SweBenchImageOptions
{
    public string Namespace { get; init; } = "swebench";

    public string Arch { get; init; } = "x86_64";

    public string Tag { get; init; } = "latest";

    public string WorkDir { get; init; } = "/testbed";

    /// <summary>
    /// Shell text placed before every command so that "python" and the test runner come from the instance's
    /// conda environment. UNCONFIRMED: this is the expected value for the SWE-bench instance images; verify it with
    /// <c>Runner check</c> on the experiment PC and override with --prelude if it differs.
    /// </summary>
    public string Prelude { get; init; } = "source /opt/miniconda3/bin/activate && conda activate testbed && ";
}

public static class SweBenchImage
{
    /// <summary>
    /// Image of one instance, for example <c>django__django-14140</c> becomes
    /// <c>swebench/sweb.eval.x86_64.django_1776_django-14140:latest</c> (the naming seen on Docker Hub).
    /// </summary>
    public static string Name(string instanceId, SweBenchImageOptions? options = null)
    {
        var o = options ?? new SweBenchImageOptions();
        var key = instanceId.ToLowerInvariant().Replace("__", "_1776_");
        return $"{o.Namespace}/sweb.eval.{o.Arch}.{key}:{o.Tag}";
    }
}
