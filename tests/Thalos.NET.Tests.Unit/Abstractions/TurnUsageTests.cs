namespace Thalos.Tests.Unit.Abstractions;

public sealed class TurnUsageTests
{
    [Fact]
    public void TurnUsage_adds()
    {
        var a = new TurnUsage(10, 5, "m");
        var b = new TurnUsage(1, 2, "m");
        (a + b).Should().Be(new TurnUsage(11, 7, "m"));
    }

    [Fact]
    public void Adding_to_an_empty_seed_takes_the_model_id_from_the_right_operand()
    {
        (TurnUsage.Empty("") + new TurnUsage(1, 1, "m")).ModelId.Should().Be("m");
    }

    [Fact]
    public void Left_model_id_wins_when_set()
    {
        (new TurnUsage(1, 1, "a") + new TurnUsage(1, 1, "b")).ModelId.Should().Be("a");
    }
}
