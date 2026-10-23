using Thalos.Workflow.Orm;

namespace Thalos.Tests.Workflow.Orm;

/// <summary>
/// The deepest bag <see cref="Thalos.Workflow.WorkflowVariableJson"/> accepts still reads back from the
/// <c>variables</c> column, whose reader parses with <see cref="System.Text.Json.JsonDocument"/>'s default depth.
/// </summary>
public sealed class VariableDepthReadBackTests
{
    // Red: in DeserializeVariables, parse with JsonDocumentOptions MaxDepth = 63; the read then throws.
    [Fact]
    public void A_variable_of_63_nested_lists_reads_back_from_the_column()
    {
        var bag = new Dictionary<string, object?>(StringComparer.Ordinal) { ["v"] = Nested(63) };

        var read = OrmWorkflowJson.DeserializeVariables(OrmWorkflowJson.SerializeVariables(bag));

        var levels = 0;
        for (object? value = read["v"]; value is List<object?> list; value = list.Count == 0 ? null : list[0])
        {
            levels++;
        }

        levels.Should().Be(63);
    }

    private static List<object?> Nested(int levels)
    {
        var list = new List<object?>();
        for (var i = 1; i < levels; i++)
        {
            list = [list];
        }

        return list;
    }
}
