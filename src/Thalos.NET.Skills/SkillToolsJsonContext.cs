using System.Text.Json.Serialization;

namespace Thalos.Skills;

/// <summary>
/// The source-generated JSON metadata of <see cref="SkillTools"/>' parameters and results, so the tools bind and answer
/// without reflection-based JSON. <see cref="SkillToolSource"/> builds its options from it.
/// </summary>
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int?))]
internal sealed partial class SkillToolsJsonContext : JsonSerializerContext;
