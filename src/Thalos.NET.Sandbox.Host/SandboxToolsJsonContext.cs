using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Thalos.Sandbox;

namespace Thalos.Sandbox.Host;

/// <summary>
/// The source-generated JSON metadata of <see cref="SandboxTools"/>' parameters and results, all strings, so the tools
/// bind and answer without reflection-based JSON.
/// </summary>
[JsonSerializable(typeof(string))]
internal sealed partial class SandboxToolsJsonContext : JsonSerializerContext
{
    /// <summary>
    /// <see cref="AIJsonUtilities.DefaultOptions"/>' settings, the ones a reflected tool's arguments and results go through,
    /// with this context as the only resolver. A method, not a static property: the generated <see cref="Default"/> is
    /// not yet set while this partial class's own static initializers run.
    /// </summary>
    public static JsonSerializerOptions CreateToolOptions()
    {
        var options = new JsonSerializerOptions(AIJsonUtilities.DefaultOptions) { TypeInfoResolver = Default };
        options.MakeReadOnly();
        return options;
    }
}
