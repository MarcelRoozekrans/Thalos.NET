using System.Text.Json.Serialization;
using Thalos.Sandbox;

namespace Thalos.Sandbox.Host;

/// <summary>
/// The source-generated JSON metadata of <see cref="SandboxTools"/>' parameters and results, all strings, so the tools
/// bind and answer without reflection-based JSON.
/// </summary>
[JsonSerializable(typeof(string))]
internal sealed partial class SandboxToolsJsonContext : JsonSerializerContext;
