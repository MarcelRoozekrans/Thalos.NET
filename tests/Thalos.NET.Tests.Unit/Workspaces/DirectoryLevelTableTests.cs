using Thalos.Workspaces;

namespace Thalos.Tests.Unit.Workspaces;

/// <summary>
/// Round-5 ruling (t): <see cref="DirectoryLevelTable"/> counts the chains that hold each directory level, lets a
/// cleanup remove a level only when its own lease is the only one, and holds back a pin of a level while it is being
/// removed.
/// </summary>
public sealed class DirectoryLevelTableTests
{
    [Fact]
    public void A_level_another_chain_holds_is_not_removed_until_that_chain_lets_go()
    {
        var table = new DirectoryLevelTable();
        var removals = 0;
        bool Remove()
        {
            removals++;
            return true;
        }

        var cleaner = table.Pin("root/d");
        var writer = table.Pin("root/d");

        table.TryRemove(cleaner, Remove).Should().BeFalse("another chain still holds the level");
        removals.Should().Be(0);

        writer.Dispose();
        table.TryRemove(cleaner, Remove).Should().BeTrue();
        removals.Should().Be(1);

        cleaner.Dispose();
        table.Count.Should().Be(0);
    }

    [Fact]
    public void Releasing_a_lease_twice_drops_only_one_reference()
    {
        var table = new DirectoryLevelTable();
        var first = table.Pin("root/d");
        var second = table.Pin("root/d");

        first.Dispose();
        first.Dispose();

        table.Count.Should().Be(1, "the second lease still holds the level");
        table.TryRemove(second, () => true).Should().BeTrue();
        second.Dispose();
        table.Count.Should().Be(0);
    }

    /// <summary>
    /// A pin that arrives while a removal of its level is running must not return until the removal is finished, or
    /// it would open or create the level while the removal is still deleting it. The removal is parked inside its own
    /// callback; the pin starts, the test gives it time to get through if it could, then lets the removal finish.
    /// The pin records whether the removal had finished by the time it returned.
    /// </summary>
    [Fact]
    public async Task A_pin_that_arrives_during_a_removal_returns_only_after_the_removal_finished()
    {
        var table = new DirectoryLevelTable();
        var cleaner = table.Pin("root/d");
        using var removing = new SemaphoreSlim(0);
        using var finishRemoval = new SemaphoreSlim(0);
        var removalFinished = 0;

        var removal = Task.Run(() => table.TryRemove(cleaner, () =>
        {
            removing.Release();
            finishRemoval.Wait(TimeSpan.FromSeconds(30));
            Volatile.Write(ref removalFinished, 1);
            return true;
        }));
        if (!await removing.WaitAsync(TimeSpan.FromSeconds(30)))
        {
            throw new TimeoutException("the removal never started");
        }

        var pin = Task.Run(() =>
        {
            using var lease = table.Pin("root/d");
            return Volatile.Read(ref removalFinished) == 1;
        });
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        finishRemoval.Release();

        (await removal).Should().BeTrue();
        (await pin).Should().BeTrue("the pin must wait for the removal it arrived during");
        cleaner.Dispose();
        table.Count.Should().Be(0);
    }

    /// <summary>
    /// Keys follow the file system: case-insensitive on Windows, where <c>D</c> and <c>d</c> are one directory, and
    /// ordinal elsewhere, where they are two.
    /// </summary>
    [Fact]
    public void Keys_compare_the_way_the_file_system_does()
    {
        var table = new DirectoryLevelTable();
        var lower = table.Pin("root/d");
        using var upper = table.Pin("root/D");

        table.TryRemove(lower, () => true).Should().Be(!OperatingSystem.IsWindows());
        lower.Dispose();
    }
}
