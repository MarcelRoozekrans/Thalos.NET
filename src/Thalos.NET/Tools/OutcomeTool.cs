using System.Buffers;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Thalos.Tools;

/// <summary>
/// The synthetic tool a turn carrying <see cref="AgentTurnRequest.RequiredOutcome"/> is offered: one function,
/// named by the schema, taking a single <see cref="OutcomeToolSchema.ArgumentName"/> string argument that its own
/// JSON schema restricts with an <c>enum</c> to exactly the declared values.
/// </summary>
/// <remarks>
/// <para>
/// The <c>enum</c> is the point. Instructing a model in prose to answer with one of N words leaves "approved, with
/// some concerns" available to it; a schema-level <c>enum</c> on a tool argument is what providers translate into a
/// constrained decode or reject outright, so an out-of-set value stops being a thing a well-behaved model can
/// produce at all. <c>additionalProperties: false</c> and <c>required</c> close the rest of the shape.
/// </para>
/// <para>
/// This type never records anything itself. The call is recorded — and therefore reaches
/// <see cref="AgentTurnResult.ToolCalls"/>, which is the only place a caller reads the outcome from — by
/// <see cref="AuthorizingAIFunction"/>, which every tool including this one is wrapped in. The value is echoed back
/// to the model rather than stored here.
/// </para>
/// <para>
/// A value outside <see cref="OutcomeToolSchema.AllowedValues"/> (a provider that ignored the schema) is
/// <em>refused</em>: the tool returns a message naming the allowed values instead of confirming a report it knows is
/// invalid. The refusal does not erase the call — <see cref="AuthorizingAIFunction"/> has already recorded the
/// arguments the model sent, so the read side still sees the bad value and fails the node loudly on it. That is
/// deliberate: every path here ends in a visible failure, never in a silently-chosen branch. A model that answers
/// the refusal by calling again with a different, valid value produces two recorded calls that disagree, which the
/// read side also refuses rather than guessing between.
/// </para>
/// </remarks>
internal sealed class OutcomeTool : AIFunction
{
    private const int MaxToolNameLength = 64;

    private readonly IReadOnlyList<string> _allowedValues;
    private readonly string _description;
    private readonly JsonElement _schema;

    /// <summary>Builds the tool for <paramref name="schema"/>. Call <see cref="Validate"/> first: the constructor assumes a valid schema.</summary>
    internal OutcomeTool(OutcomeToolSchema schema)
    {
        Name = schema.ToolName;
        _allowedValues = schema.AllowedValues;
        _description =
            $"Report the result of this task. Call this exactly once, after the work is done, with '{OutcomeToolSchema.ArgumentName}' "
            + $"set to one of: {string.Join(", ", schema.AllowedValues)}. This is the only way the result is read; text replies are ignored.";
        _schema = BuildSchema(_description, schema.AllowedValues);
    }

    /// <inheritdoc />
    public override string Name { get; }

    /// <inheritdoc />
    public override string Description => _description;

    /// <inheritdoc />
    public override JsonElement JsonSchema => _schema;

    /// <summary>
    /// Checks what <see cref="OutcomeTool"/> cannot represent: a tool name a provider would reject (empty, over
    /// 64 characters, or outside the <c>letters, digits, underscore, hyphen</c> set — Anthropic's rule, the
    /// strictest of the providers Thalos targets), and a value set that cannot form a usable <c>enum</c> (empty, or
    /// containing a blank or a duplicate). Returns <see langword="null"/> when the schema is usable. Reported as an
    /// <see cref="AgentError"/> rather than thrown: an unusable outcome schema is a caller's configuration mistake,
    /// which this codebase returns rather than throws.
    /// </summary>
    internal static AgentError? Validate(OutcomeToolSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        if (string.IsNullOrEmpty(schema.ToolName) || schema.ToolName.Length > MaxToolNameLength || !IsValidToolName(schema.ToolName))
        {
            return AgentError.Validation(
                $"Outcome tool name '{schema.ToolName}' is invalid: it must be 1 to {MaxToolNameLength} characters of letters, digits, '_' or '-'.");
        }

        if (schema.AllowedValues.Count == 0)
        {
            return AgentError.Validation($"Outcome tool '{schema.ToolName}' declares no allowed values.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in schema.AllowedValues)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return AgentError.Validation($"Outcome tool '{schema.ToolName}' declares a blank allowed value.");
            }

            if (!seen.Add(value))
            {
                return AgentError.Validation($"Outcome tool '{schema.ToolName}' declares the value '{value}' more than once.");
            }
        }

        return null;
    }

    /// <inheritdoc />
    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        cancellationToken.ThrowIfCancellationRequested();

        if (!arguments.TryGetValue(OutcomeToolSchema.ArgumentName, out var raw) || AsString(raw) is not { } value)
        {
            return new ValueTask<object?>(
                $"No outcome reported: call '{Name}' with a '{OutcomeToolSchema.ArgumentName}' argument set to one of: {string.Join(", ", _allowedValues)}.");
        }

        // Ordinal, never a culture- or case-insensitive match: "Approved" is not the declared "approved", and
        // accepting it here would hand the read side a value the process definition never declared.
        for (var i = 0; i < _allowedValues.Count; i++)
        {
            if (string.Equals(_allowedValues[i], value, StringComparison.Ordinal))
            {
                return new ValueTask<object?>($"Outcome '{value}' recorded.");
            }
        }

        return new ValueTask<object?>(
            $"'{value}' is not an allowed outcome. Call '{Name}' again with '{OutcomeToolSchema.ArgumentName}' set to exactly one of: {string.Join(", ", _allowedValues)}.");
    }

    /// <summary>
    /// The argument arrives either as a CLR <see cref="string"/> or as a <see cref="JsonElement"/> depending on how
    /// the provider's response was deserialized; anything else (a number, an object, <see langword="null"/>) is not
    /// an outcome.
    /// </summary>
    private static string? AsString(object? raw) => raw switch
    {
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
        _ => null,
    };

    private static bool IsValidToolName(string name)
    {
        foreach (var c in name)
        {
            if (c != '_' && c != '-' && !char.IsAsciiLetterOrDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Writes the schema document by hand rather than deriving it from a .NET method: the allowed values are only
    /// known at run time, so there is no C# <c>enum</c> for <c>AIFunctionFactory</c> to reflect over.
    /// <see cref="Utf8JsonWriter"/> (not string concatenation) so a value containing a quote or a backslash is
    /// escaped rather than producing a malformed document.
    /// </summary>
    private static JsonElement BuildSchema(string description, IReadOnlyList<string> allowedValues)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "object");
            writer.WritePropertyName("properties");
            writer.WriteStartObject();
            writer.WritePropertyName(OutcomeToolSchema.ArgumentName);
            writer.WriteStartObject();
            writer.WriteString("type", "string");
            writer.WriteString("description", description);
            writer.WritePropertyName("enum");
            writer.WriteStartArray();
            for (var i = 0; i < allowedValues.Count; i++)
            {
                writer.WriteStringValue(allowedValues[i]);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WritePropertyName("required");
            writer.WriteStartArray();
            writer.WriteStringValue(OutcomeToolSchema.ArgumentName);
            writer.WriteEndArray();
            writer.WriteBoolean("additionalProperties", false);
            writer.WriteEndObject();
        }

        // Clone so the element outlives the JsonDocument it was parsed from; the document is disposed on the way out.
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }
}
