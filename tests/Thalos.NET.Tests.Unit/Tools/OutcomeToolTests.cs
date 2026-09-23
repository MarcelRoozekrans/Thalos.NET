using System.Text.Json;
using Microsoft.Extensions.AI;
using NSubstitute;
using Thalos.Runtime;
using Thalos.Testing;
using Thalos.Tools;
using ZeroAlloc.Authorization;

namespace Thalos.Tests.Unit.Tools;

/// <summary>
/// The synthetic outcome tool itself: the <c>enum</c> in its schema is the whole point of the type, so most of
/// these assert the schema document rather than behaviour. Each names, in its own remarks, the production change
/// that turns it red.
/// </summary>
public sealed class OutcomeToolTests
{
    private static readonly OutcomeToolSchema Approval = new("workflow__report_outcome", ["approved", "rejected"]);

    private static AIFunctionArguments Args(object? outcome) =>
        new(StringComparer.Ordinal) { [OutcomeToolSchema.ArgumentName] = outcome };

    /// <summary>Red if <c>BuildSchema</c> stops writing the <c>enum</c> array, or writes values other than the declared ones.</summary>
    [Fact]
    public void Schema_constrains_the_argument_to_exactly_the_declared_values()
    {
        var tool = new OutcomeTool(Approval);

        var outcome = tool.JsonSchema.GetProperty("properties").GetProperty(OutcomeToolSchema.ArgumentName);
        outcome.GetProperty("type").GetString().Should().Be("string");
        outcome.GetProperty("enum").EnumerateArray().Select(e => e.GetString()).Should().Equal("approved", "rejected");
    }

    /// <summary>
    /// Red if the schema stops requiring the outcome argument, starts requiring the optional variables argument,
    /// starts tolerating properties beyond the two it declares, or grows a third.
    /// </summary>
    [Fact]
    public void Schema_requires_only_the_outcome_argument_and_closes_the_object()
    {
        var tool = new OutcomeTool(Approval);

        tool.JsonSchema.GetProperty("type").GetString().Should().Be("object");
        tool.JsonSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).Should().Equal(OutcomeToolSchema.ArgumentName);
        tool.JsonSchema.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        tool.JsonSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name)
            .Should().Equal(OutcomeToolSchema.ArgumentName, OutcomeToolSchema.VariablesArgumentName);
    }

    /// <summary>
    /// The variables argument rides on this one tool rather than a second <c>set_variables</c> tool, so that
    /// there is one read path instead of two that can drift. Red if <c>BuildSchema</c> stops writing the
    /// property, or writes it as something other than an object — the read side merges an object and discards the
    /// whole call for anything else.
    /// </summary>
    [Fact]
    public void Schema_offers_an_optional_object_valued_variables_argument()
    {
        var tool = new OutcomeTool(Approval);

        var variables = tool.JsonSchema.GetProperty("properties").GetProperty(OutcomeToolSchema.VariablesArgumentName);
        variables.GetProperty("type").GetString().Should().Be("object");
        variables.GetProperty("additionalProperties").GetBoolean().Should().BeTrue("the caller chooses the keys, so the object itself must stay open");
        variables.GetProperty("description").GetString().Should().NotBeNullOrWhiteSpace();
        tool.Description.Should().Contain(OutcomeToolSchema.VariablesArgumentName,
            "the tool's own description is what a model reads first, so it has to mention the argument exists");
    }

    /// <summary>
    /// A variables argument in the wrong shape is refused rather than ignored, because the read side discards the
    /// entire call — outcome included — when it cannot merge the object. Red if the shape check in
    /// <c>InvokeCoreAsync</c> is removed: the model would be told its outcome was recorded and the node would
    /// then fail with "no outcome reported", which is the least debuggable pair of messages available.
    /// </summary>
    [Theory]
    [InlineData("\"just a string\"")]
    [InlineData("[1,2]")]
    [InlineData("7")]
    public async Task Variables_argument_in_the_wrong_shape_is_refused(string json)
    {
        using var document = JsonDocument.Parse(json);
        var arguments = new AIFunctionArguments(StringComparer.Ordinal)
        {
            [OutcomeToolSchema.ArgumentName] = "approved",
            [OutcomeToolSchema.VariablesArgumentName] = document.RootElement,
        };

        var result = await new OutcomeTool(Approval).InvokeAsync(arguments);

        result.Should().BeOfType<string>().Which.Should().StartWith("'variables' must be a JSON object.");
    }

    /// <summary>Red if the shape check starts rejecting the shapes the read side actually accepts — a JSON object, or an omitted-as-null argument.</summary>
    [Theory]
    [InlineData("{\"a\":1}")]
    [InlineData("null")]
    public async Task Variables_argument_in_an_accepted_shape_records_the_outcome(string json)
    {
        using var document = JsonDocument.Parse(json);
        var arguments = new AIFunctionArguments(StringComparer.Ordinal)
        {
            [OutcomeToolSchema.ArgumentName] = "approved",
            [OutcomeToolSchema.VariablesArgumentName] = document.RootElement,
        };

        var result = await new OutcomeTool(Approval).InvokeAsync(arguments);

        result.Should().Be("Outcome 'approved' recorded.");
    }

    /// <summary>
    /// A value containing JSON metacharacters must survive as data. Red if <c>BuildSchema</c> is rewritten with
    /// string concatenation instead of <see cref="System.Text.Json.Utf8JsonWriter"/> — the document would not parse
    /// at all, or would parse with the value truncated at the quote.
    /// </summary>
    [Fact]
    public void Schema_escapes_values_containing_json_metacharacters()
    {
        var tool = new OutcomeTool(new OutcomeToolSchema("t", [@"say ""hi""\ok"]));

        tool.JsonSchema.GetProperty("properties").GetProperty(OutcomeToolSchema.ArgumentName)
            .GetProperty("enum").EnumerateArray().Single().GetString().Should().Be(@"say ""hi""\ok");
    }

    /// <summary>The tool is named by the schema, not by any source prefix. Red if <c>Name</c> stops echoing <see cref="OutcomeToolSchema.ToolName"/>.</summary>
    [Fact]
    public void Name_is_the_schema_tool_name()
    {
        new OutcomeTool(Approval).Name.Should().Be("workflow__report_outcome");
    }

    /// <summary>Red if <c>InvokeCoreAsync</c> stops comparing against the declared set and accepts anything.</summary>
    [Fact]
    public async Task Declared_value_is_accepted()
    {
        var result = await new OutcomeTool(Approval).InvokeAsync(Args("approved"));

        result.Should().Be("Outcome 'approved' recorded.");
    }

    /// <summary>Arguments arrive as <see cref="JsonElement"/> from some providers. Red if <c>AsString</c> drops the <see cref="JsonElement"/> case.</summary>
    [Fact]
    public async Task Declared_value_arriving_as_a_json_element_is_accepted()
    {
        using var document = JsonDocument.Parse("\"rejected\"");

        var result = await new OutcomeTool(Approval).InvokeAsync(Args(document.RootElement));

        result.Should().Be("Outcome 'rejected' recorded.");
    }

    /// <summary>Red if the comparison is loosened to <see cref="StringComparison.OrdinalIgnoreCase"/> — "Approved" is not the declared value.</summary>
    [Fact]
    public async Task Value_differing_only_in_case_is_refused()
    {
        var result = await new OutcomeTool(Approval).InvokeAsync(Args("Approved"));

        result.Should().BeOfType<string>().Which.Should().StartWith("'Approved' is not an allowed outcome.");
    }

    /// <summary>Red if the out-of-set branch is removed and every value is confirmed as recorded.</summary>
    [Fact]
    public async Task Undeclared_value_is_refused_and_the_allowed_values_are_named()
    {
        var result = await new OutcomeTool(Approval).InvokeAsync(Args("maybe"));

        result.Should().BeOfType<string>().Which.Should().Be(
            "'maybe' is not an allowed outcome. Call 'workflow__report_outcome' again with 'outcome' set to exactly one of: approved, rejected.");
    }

    /// <summary>Red if a missing or non-string argument is allowed to fall through as a reported outcome.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(42)]
    public async Task Missing_or_non_string_argument_reports_nothing(object? value)
    {
        var arguments = value is null ? new AIFunctionArguments(StringComparer.Ordinal) : Args(value);

        var result = await new OutcomeTool(Approval).InvokeAsync(arguments);

        result.Should().BeOfType<string>().Which.Should().StartWith("No outcome reported:");
    }

    /// <summary>Red if <c>Validate</c> stops rejecting a schema that cannot be expressed as a provider tool.</summary>
    [Theory]
    [InlineData("", new[] { "a" })]
    [InlineData("has space", new[] { "a" })]
    [InlineData("has__separator_but_also_a_dot.", new[] { "a" })]
    [InlineData("ok", new[] { "a", " " })]
    [InlineData("ok", new[] { "a", "a" })]
    public void Validate_rejects_unusable_schemas(string toolName, string[] values)
    {
        OutcomeTool.Validate(new OutcomeToolSchema(toolName, values))
            .Should().NotBeNull().And.Match<AgentError?>(e => e!.Value.Code == AgentErrorCode.Validation);
    }

    /// <summary>Red if <c>Validate</c> stops rejecting a value set with nothing in it, which would produce an empty <c>enum</c> no model could satisfy.</summary>
    [Fact]
    public void Validate_rejects_an_empty_value_set()
    {
        OutcomeTool.Validate(new OutcomeToolSchema("ok", [])).Should().NotBeNull();
    }

    /// <summary>Red if <c>Validate</c> starts rejecting a schema the rest of the suite relies on being accepted.</summary>
    [Fact]
    public void Validate_accepts_a_usable_schema()
    {
        OutcomeTool.Validate(Approval).Should().BeNull();
    }

    /// <summary>Red if the 64-character provider ceiling on tool names is dropped.</summary>
    [Fact]
    public void Validate_rejects_a_tool_name_over_the_provider_ceiling()
    {
        OutcomeTool.Validate(new OutcomeToolSchema(new string('a', 65), ["x"])).Should().NotBeNull();
    }
}

/// <summary>
/// The factory's one non-obvious job: the tool it hands back is already wrapped for authorization and audit. Every
/// test here fails if <c>Create</c> returns the bare <see cref="OutcomeTool"/> instead.
/// </summary>
public sealed class OutcomeToolFactoryTests
{
    private static readonly OutcomeToolSchema Approval = new("workflow__report_outcome", ["approved", "rejected"]);

    private static (OutcomeToolFactory factory, IToolAuthorizer authorizer, RecordingNotificationPublisher publisher) Build(bool allow)
    {
        var authorizer = Substitute.For<IToolAuthorizer>();
        authorizer.AuthorizeAsync(default!, default!, default, default)
            .ReturnsForAnyArgs(allow ? ToolAuthorizationDecision.Allow() : ToolAuthorizationDecision.Deny("nope"));
        var publisher = new RecordingNotificationPublisher();
        return (new OutcomeToolFactory(authorizer, publisher, TimeProvider.System), authorizer, publisher);
    }

    /// <summary>Red if <c>Create</c> stops consulting <see cref="OutcomeTool.Validate"/> and hands back a tool no provider would accept.</summary>
    [Fact]
    public void Create_refuses_an_unusable_schema()
    {
        var (factory, _, _) = Build(allow: true);

        var result = factory.Create(new OutcomeToolSchema("not a valid name", ["a"]));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(AgentErrorCode.Validation);
    }

    /// <summary>
    /// The authorization decision: an outcome tool goes through the same enforcement point as any other tool. Red
    /// if <c>Create</c> returns <c>new OutcomeTool(schema)</c> directly — the authorizer would never be asked.
    /// </summary>
    [Fact]
    public async Task Created_tool_asks_the_authorizer_before_running()
    {
        var (factory, authorizer, _) = Build(allow: true);
        var tool = (AIFunction)factory.Create(Approval).Value;

        await tool.InvokeAsync(new AIFunctionArguments(StringComparer.Ordinal) { [OutcomeToolSchema.ArgumentName] = "approved" });

        await authorizer.Received(1).AuthorizeAsync(
            Arg.Any<ISecurityContext>(), "workflow__report_outcome", Arg.Any<JsonElement>(), Arg.Any<CancellationToken>());
    }

    /// <summary>A host that denies the outcome tool gets a denial, not a silent report. Red if the wrapper is removed.</summary>
    [Fact]
    public async Task Denied_outcome_tool_reports_the_denial_to_the_model()
    {
        var (factory, _, _) = Build(allow: false);
        var tool = (AIFunction)factory.Create(Approval).Value;

        var result = await tool.InvokeAsync(new AIFunctionArguments(StringComparer.Ordinal) { [OutcomeToolSchema.ArgumentName] = "approved" });

        result.Should().BeOfType<string>().Which.Should().Be("Tool call denied: nope");
    }

    /// <summary>
    /// The wrapper is also the mechanism, not only the audit: it is what records the call into the turn scope, and
    /// the recorded arguments are the only thing a caller reads the outcome from. Red if the wrapper is removed.
    /// </summary>
    [Fact]
    public async Task Created_tool_records_the_call_into_the_turn_scope()
    {
        var (factory, _, _) = Build(allow: true);
        var tool = (AIFunction)factory.Create(Approval).Value;
        using var scope = TurnScope.Begin(SessionId.New(), TurnId.New(), new Runtime.TestSecurityContext("u1"));

        await tool.InvokeAsync(new AIFunctionArguments(StringComparer.Ordinal) { [OutcomeToolSchema.ArgumentName] = "approved" });

        var call = scope.ToolCalls.Should().ContainSingle().Which;
        call.ToolName.Should().Be("workflow__report_outcome");
        call.Succeeded.Should().BeTrue();
        using var arguments = JsonDocument.Parse(call.ArgumentsJson);
        arguments.RootElement.GetProperty(OutcomeToolSchema.ArgumentName).GetString().Should().Be("approved");
    }

    /// <summary>The wrapper must not hide the schema — the enum has to reach the provider through it. Red if <c>AuthorizingAIFunction</c> stops delegating <c>JsonSchema</c>.</summary>
    [Fact]
    public void Created_tool_still_exposes_the_enum_constrained_schema()
    {
        var (factory, _, _) = Build(allow: true);

        var tool = (AIFunction)factory.Create(Approval).Value;

        tool.JsonSchema.GetProperty("properties").GetProperty(OutcomeToolSchema.ArgumentName)
            .GetProperty("enum").EnumerateArray().Select(e => e.GetString()).Should().Equal("approved", "rejected");
    }
}
