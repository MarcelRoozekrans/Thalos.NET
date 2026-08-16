using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Thalos.Tests.McpServer;

[McpServerToolType]
public static class EchoTools
{
    [McpServerTool(Name = "echo"), Description("Echoes the input")]
    public static string Echo([Description("Text to echo")] string text) => $"echo:{text}";

    [McpServerTool(Name = "add"), Description("Adds two numbers")]
    public static int Add(int a, int b) => a + b;

    [McpServerTool(Name = "fail"), Description("Always fails")]
    public static string Fail() => throw new InvalidOperationException("boom");

    [McpServerTool(Name = "env"), Description("Returns the value of an environment variable of the server process")]
    public static string Env([Description("Variable name")] string name) => Environment.GetEnvironmentVariable(name) ?? "<unset>";
}
