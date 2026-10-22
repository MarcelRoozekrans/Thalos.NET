using System.ComponentModel;
using YamlDotNet.Serialization;

namespace Thalos.Workflow.Yaml;

/// <summary>
/// Generated plumbing, not a supported API: the YamlDotNet static context that <see cref="ProcessLoader"/>'s typed pass
/// deserializes through, so loading a process needs no reflection and stays trim- and AOT-safe. Do not reference it;
/// it can change or disappear in any release.
/// </summary>
/// <remarks>
/// It is public only because YamlDotNet's static generator, <c>Vecc.YamlDotNet.Analyzers.StaticGenerator</c>,
/// hard-codes <c>public partial class</c> for the context (StaticContextFile.cs:35), so an <c>internal</c>
/// declaration fails with CS0262, and emits a public <c>StaticTypeInspector</c> into this namespace
/// (StaticTypeInspectorFile.cs:42). It goes back to <c>internal</c> once upstream respects the declared accessibility.
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
[YamlStaticContext]
[YamlSerializable(typeof(ProcessLoader.ProcessDefinitionDto))]
[YamlSerializable(typeof(ProcessLoader.ProcessNodeDto))]
public partial class ProcessYamlContext : StaticContext;
