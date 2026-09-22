namespace Thalos;

/// <summary>
/// Declares that a detached run (see <see cref="SubagentRunRequest.RequiredOutcome"/>) must report its result by
/// calling a tool named <see cref="ToolName"/> whose sole argument (conventionally named <c>"outcome"</c>) is a
/// string constrained by the tool's own JSON schema to exactly <see cref="AllowedValues"/>. This is what makes a
/// closed set of outcomes structural rather than a prompt the model could paraphrase around: a schema-level
/// <c>enum</c> constraint on a tool call, not free text a caller then tries to parse. A caller receiving the turn
/// back still reads the outcome off <see cref="AgentTurnResult.ToolCalls"/> and independently validates it against
/// the same closed set — the schema narrows what a well-behaved model can send, it does not by itself guarantee
/// every provider enforces it, so the read side must never simply trust the value arrived in range.
/// </summary>
/// <param name="ToolName">
/// The qualified tool name the model must call to report its result (e.g. <c>"workflow__report_outcome"</c>).
/// </param>
/// <param name="AllowedValues">The closed set of values the tool's argument schema restricts the call to.</param>
public sealed record OutcomeToolSchema(string ToolName, IReadOnlyList<string> AllowedValues);
