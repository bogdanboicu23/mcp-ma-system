using System.Text.Json.Nodes;

namespace Agents.Core.Tools;

public static class ToolNames
{
    public const string ReadFile = "read_file";
    public const string ListDir = "list_dir";
    public const string EditFile = "edit_file";
    public const string WriteFile = "write_file";
    public const string RunCommand = "run_command";
    public const string RunTests = "run_tests";
}

/// <summary>
/// The ONE definition of the six tools. The direct host (config A) and the MCP server (configs B-E) both
/// advertise exactly these names, descriptions and schemas, so any cost difference between them comes from
/// the MCP transport and not from different tool text.
/// Descriptions are deliberately short: they are sent on every request, and the MCP layer's token cost is
/// one of the things this thesis measures.
/// </summary>
public static class ToolCatalog
{
    public static IReadOnlyList<ToolDef> All { get; } = new[]
    {
        Define(
            ToolNames.ReadFile,
            "Read a text file with line numbers. Optional start_line and end_line select a range.",
            """
            {"type":"object","properties":{
              "path":{"type":"string","description":"File path, relative to the repository root."},
              "start_line":{"type":"integer"},
              "end_line":{"type":"integer"}},
             "required":["path"]}
            """),
        Define(
            ToolNames.ListDir,
            "List the entries of a directory (directories end with /).",
            """
            {"type":"object","properties":{"path":{"type":"string","description":"Directory path, default '.'."}}}
            """),
        Define(
            ToolNames.EditFile,
            "Replace one exact occurrence of old_string with new_string in a file. Fails if old_string is missing or not unique.",
            """
            {"type":"object","properties":{
              "path":{"type":"string"},
              "old_string":{"type":"string"},
              "new_string":{"type":"string"}},
             "required":["path","old_string","new_string"]}
            """),
        Define(
            ToolNames.WriteFile,
            "Create or overwrite a file with the given content.",
            """
            {"type":"object","properties":{"path":{"type":"string"},"content":{"type":"string"}},
             "required":["path","content"]}
            """),
        Define(
            ToolNames.RunCommand,
            "Run a shell command in the repository and return its exit code and output.",
            """
            {"type":"object","properties":{
              "command":{"type":"string"},
              "timeout_seconds":{"type":"integer","description":"Default 120, maximum 900."}},
             "required":["command"]}
            """),
        Define(
            ToolNames.RunTests,
            "Run a test command (for example pytest on one file) and return the exit code and the end of the output.",
            """
            {"type":"object","properties":{
              "command":{"type":"string"},
              "timeout_seconds":{"type":"integer","description":"Default 600, maximum 900."}},
             "required":["command"]}
            """),
    };

    /// <summary>Tools that only look at the repository. A planner or reviewer role can be limited to these.</summary>
    public static IReadOnlySet<string> ReadOnly { get; } =
        new HashSet<string> { ToolNames.ReadFile, ToolNames.ListDir };

    public static IReadOnlySet<string> AllNames { get; } =
        new HashSet<string>(All.Select(t => t.Name));

    private static ToolDef Define(string name, string description, string schemaJson) =>
        new(name, description, JsonNode.Parse(schemaJson)!.AsObject());
}
