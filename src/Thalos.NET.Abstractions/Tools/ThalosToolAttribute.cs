namespace Thalos;

/// <summary>Marks a class whose <see cref="ThalosToolAttribute"/> methods are exposed as in-process tools.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ThalosToolTypeAttribute : Attribute;

/// <summary>Marks a method as a tool. Name defaults to the method name; description comes from <see cref="System.ComponentModel.DescriptionAttribute"/>.</summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class ThalosToolAttribute(string? name = null) : Attribute
{
    /// <summary>Explicit tool name; <see langword="null"/> means the method name.</summary>
    public string? Name { get; } = name;
}
