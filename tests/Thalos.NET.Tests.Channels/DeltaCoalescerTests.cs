using Microsoft.Extensions.Time.Testing;
using Thalos.Channels;

namespace Thalos.Tests.Channels;

public sealed class DeltaCoalescerTests
{
    private static (DeltaCoalescer Coalescer, FakeTimeProvider Clock) Build(double seconds = 1)
    {
        var clock = new FakeTimeProvider();
        return (new DeltaCoalescer(TimeSpan.FromSeconds(seconds), clock), clock);
    }

    [Fact]
    public void First_delta_renders_immediately_so_the_user_sees_life()
    {
        var (coalescer, _) = Build();
        coalescer.TryAppend("Hel", out var render).Should().BeTrue();
        render.Should().Be("Hel");
    }

    [Fact]
    public void Deltas_inside_the_interval_are_accumulated_but_not_rendered()
    {
        var (coalescer, _) = Build();
        coalescer.TryAppend("Hel", out _);
        coalescer.TryAppend("lo", out var render).Should().BeFalse();
        render.Should().BeNull();
        coalescer.Text.Should().Be("Hello");
    }

    [Fact]
    public void A_delta_after_the_interval_renders_everything_accumulated()
    {
        var (coalescer, clock) = Build();
        coalescer.TryAppend("Hel", out _);
        coalescer.TryAppend("lo", out _);
        clock.Advance(TimeSpan.FromSeconds(1.1));

        coalescer.TryAppend(" world", out var render).Should().BeTrue();
        render.Should().Be("Hello world");
    }

    [Fact]
    public void A_zero_interval_renders_every_delta()
    {
        var (coalescer, _) = Build(seconds: 0);
        coalescer.TryAppend("a", out _).Should().BeTrue();
        coalescer.TryAppend("b", out var render).Should().BeTrue();
        render.Should().Be("ab");
    }

    [Fact]
    public void Identical_consecutive_renders_are_suppressed()
    {
        // Telegram rejects an unchanged editMessageText with 400 "message is not modified".
        var (coalescer, clock) = Build();
        coalescer.TryAppend("a", out _);
        clock.Advance(TimeSpan.FromSeconds(2));
        coalescer.TryAppend(string.Empty, out var render).Should().BeFalse();
        render.Should().BeNull();
    }

    [Fact]
    public void Changing_the_activity_line_forces_a_render_even_mid_interval()
    {
        var (coalescer, _) = Build();
        coalescer.TryAppend("thinking", out _);
        coalescer.SetActivity("roslyn__find_callers");

        coalescer.TryAppend(string.Empty, out var render).Should().BeTrue();
        render.Should().Be("▸ roslyn__find_callers\nthinking");
    }

    [Fact]
    public void Flush_drops_the_activity_line_and_returns_the_final_text()
    {
        var (coalescer, _) = Build();
        coalescer.TryAppend("answer", out _);
        coalescer.SetActivity("some_tool");

        coalescer.Flush().Should().Be("answer");
    }
}
