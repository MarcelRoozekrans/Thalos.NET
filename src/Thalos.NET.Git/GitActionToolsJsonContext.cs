using System.Text.Json.Serialization;

namespace Thalos.Git;

/// <summary>
/// The source-generated JSON metadata of <see cref="GitActionTools"/>' parameters and results, all strings, so the tools
/// bind and answer without reflection-based JSON. <see cref="GitActionToolsThalosBuilderExtensions.AddGitActionTools"/>
/// builds its options from it.
/// </summary>
[JsonSerializable(typeof(string))]
internal sealed partial class GitActionToolsJsonContext : JsonSerializerContext;
