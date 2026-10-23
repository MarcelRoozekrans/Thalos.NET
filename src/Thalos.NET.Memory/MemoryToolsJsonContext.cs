using System.Text.Json.Serialization;

namespace Thalos.Memory;

/// <summary>
/// The source-generated JSON metadata of <see cref="MemoryTools"/>' parameters and results, so the tools bind and answer
/// without reflection-based JSON. <see cref="MemoryToolSource"/> builds its options from it.
/// </summary>
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(double?))]
[JsonSerializable(typeof(int?))]
[JsonSerializable(typeof(bool))]
internal sealed partial class MemoryToolsJsonContext : JsonSerializerContext;
