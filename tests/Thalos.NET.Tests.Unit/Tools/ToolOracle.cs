using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Thalos.Tests;

/// <summary>
/// What the model sees of a tool list, and what it gets back from a call, as text a golden or a guard compares. Lives
/// in Thalos.NET.Tests.Unit and is shared with the other test projects as a linked file, so every tool golden and
/// every typed-versus-reflective guard formats tools the same way.
/// </summary>
internal static class ToolOracle
{
    /// <summary>
    /// One line per tool, in list order: <c>name | description | JsonSchema | ReturnJsonSchema</c>. The schemas are the
    /// raw JSON text M.E.AI wrote, which is compact, so a change in their encoding shows rather than being rewritten here.
    /// </summary>
    public static string Dump(IReadOnlyList<AITool> tools) => string.Join('\n', tools.Select(Line));

    /// <summary>
    /// A tool call's result as the text that reaches the model: the raw JSON of a <see cref="JsonElement"/>, which is
    /// what an <see cref="AIFunction"/> returns after serialising the method's result, so the encoder shows.
    /// </summary>
    public static string Result(object? result) => result switch
    {
        null => "null",
        JsonElement element => element.GetRawText(),
        _ => throw new ArgumentException($"A tool result of type '{result.GetType().FullName}' is not a serialised result.", nameof(result)),
    };

    private static string Line(AITool tool)
    {
        var declaration = tool as AIFunctionDeclaration;
        return $"{tool.Name} | {tool.Description} | {Compact(declaration?.JsonSchema)} | {Compact(declaration?.ReturnJsonSchema)}";
    }

    private static string Compact(JsonElement? schema) => schema is { } element ? element.GetRawText() : "null";
}
