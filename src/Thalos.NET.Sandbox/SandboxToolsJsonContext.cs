using System.Text.Json.Serialization;

namespace Thalos.Sandbox;

/// <summary>
/// The source-generated JSON metadata of <see cref="SandboxTools"/>' parameters and results, all strings, so the tools
/// bind and answer without reflection-based JSON. <see cref="SandboxToolOptions.SerializerOptions"/> is built from it.
/// </summary>
[JsonSerializable(typeof(string))]
internal sealed partial class SandboxToolsJsonContext : JsonSerializerContext;
