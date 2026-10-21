using System.Text.Encodings.Web;
using System.Text.Json;
using Thalos.Mcp;

namespace Thalos.Tests.Mcp;

/// <summary>
/// What <see cref="McpConfigFile.Parse"/> builds from a hand-edited <c>.mcp.json</c>, captured before it moves to a
/// JSON context: comments, trailing commas, a <c>"00:00:30"</c> timeout and a run-scoped section. The dump is written
/// by the reflection-based serializer in test code, which stays allowed as an oracle. A characterization test: it
/// passes on today's code.
/// </summary>
public sealed class McpConfigFileGoldenTests
{
    private static readonly JsonSerializerOptions DumpOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private const string File = """
        {
          // a line comment
          "mcpServers": {
            /* a block comment */
            "roslyn": {
              "type": "stdio",
              "command": "dnx",
              "args": ["RoslynCodeLens.Mcp", "--", "C:/x/x.sln",],
              "env": { "ROSLYN_TIMEOUT": "600", },
              "passEnvironment": ["PATH"],
              "cwd": "/repo",
              "timeout": "00:00:30",
              "shutdownTimeout": "00:00:05",
              "runScoped": {
                "args": ["RoslynCodeLens.Mcp", "--", "${run.workspace.solution}"],
                "env": { "RUN": "${run.id}" },
                "cwd": "${run.workspace.root}/src",
                "readyTool": "list_solutions",
                "reload": "tool:rebuild_solution",
                "readyWaitTimeout": "00:10:00",
                "callTimeout": "00:00:45",
                "remote": true,
              },
            },
            "slow": { "command": "slow-server", "timeout": "00:01:30", },
            "context7": { "Type": "http", "URL": "https://context7.com/api", "headers": { "Authorization": "Bearer t" } },
          },
        }
        """;

    /// <summary>The dump of <see cref="File"/> captured from today's code.</summary>
    private const string Expected = """
        {
          "roslyn": {
            "Type": "stdio",
            "Command": "dnx",
            "Args": [
              "RoslynCodeLens.Mcp",
              "--",
              "C:/x/x.sln"
            ],
            "Env": {
              "ROSLYN_TIMEOUT": "600"
            },
            "PassEnvironment": [
              "PATH"
            ],
            "Cwd": "/repo",
            "Url": null,
            "Headers": null,
            "Timeout": "00:00:30",
            "ShutdownTimeout": "00:00:05",
            "RunScoped": {
              "Args": [
                "RoslynCodeLens.Mcp",
                "--",
                "${run.workspace.solution}"
              ],
              "Env": {
                "RUN": "${run.id}"
              },
              "Cwd": "${run.workspace.root}/src",
              "ReadyTool": "list_solutions",
              "Reload": "tool:rebuild_solution",
              "ReadyWaitTimeout": "00:10:00",
              "CallTimeout": "00:00:45",
              "Remote": true
            },
            "EffectiveType": "stdio"
          },
          "slow": {
            "Type": null,
            "Command": "slow-server",
            "Args": null,
            "Env": null,
            "PassEnvironment": null,
            "Cwd": null,
            "Url": null,
            "Headers": null,
            "Timeout": "00:01:30",
            "ShutdownTimeout": "00:00:02",
            "RunScoped": null,
            "EffectiveType": "stdio"
          },
          "context7": {
            "Type": "http",
            "Command": null,
            "Args": null,
            "Env": null,
            "PassEnvironment": null,
            "Cwd": null,
            "Url": "https://context7.com/api",
            "Headers": {
              "Authorization": "Bearer t"
            },
            "Timeout": "00:00:30",
            "ShutdownTimeout": "00:00:02",
            "RunScoped": null,
            "EffectiveType": "http"
          }
        }
        """;

    // Red: in McpConfigFile, drop ReadCommentHandling = JsonCommentHandling.Skip; the parse throws on the first comment.
    // Red: put [JsonPropertyName("timeoutX")] on McpServerDefinition.Timeout; the key no longer binds, and "slow"
    // dumps the 30-second default instead of 00:01:30.
    // Red: in McpConfigFile, build the options without JsonSerializerDefaults.Web; the camelCase keys then no longer
    // bind and the dump holds only defaults.
    [Fact]
    public void A_commented_file_with_trailing_commas_parses_as_before()
    {
        var dump = JsonSerializer.Serialize(McpConfigFile.Parse(File), DumpOptions);

        dump.Should().Be(Expected);
    }
}
