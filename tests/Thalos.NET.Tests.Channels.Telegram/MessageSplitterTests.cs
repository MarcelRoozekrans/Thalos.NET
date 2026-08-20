using Thalos.Channels.Telegram;

namespace Thalos.Tests.Channels.Telegram;

public sealed class MessageSplitterTests
{
    [Fact]
    public void Short_text_is_one_chunk()
    {
        MessageSplitter.Split("hello").Should().ContainSingle().Which.Should().Be("hello");
    }

    [Fact]
    public void Splitting_prefers_a_paragraph_boundary()
    {
        var text = new string('a', 30) + "\n\n" + new string('b', 30);
        var chunks = MessageSplitter.Split(text, limit: 40);

        chunks.Should().HaveCount(2);
        chunks[0].Should().Be(new string('a', 30));
        chunks[1].Should().Be(new string('b', 30));
    }

    [Fact]
    public void Falls_back_to_a_line_boundary_when_there_is_no_paragraph_break()
    {
        var text = new string('a', 30) + "\n" + new string('b', 30);
        var chunks = MessageSplitter.Split(text, limit: 40);

        chunks.Should().HaveCount(2);
        chunks[0].Should().Be(new string('a', 30));
        chunks[1].Should().Be(new string('b', 30));
    }

    [Fact]
    public void A_single_unbroken_run_longer_than_the_limit_is_hard_split_rather_than_dropped()
    {
        var chunks = MessageSplitter.Split(new string('x', 100), limit: 40);

        chunks.Should().HaveCount(3);
        chunks.Sum(c => c.Length).Should().Be(100);
        chunks.Should().OnlyContain(c => c.Length <= 40);
        // Not just lengths: the concatenation must reconstruct the original run exactly, or a chunk could contain
        // wrong content while still satisfying the length-only assertions above.
        string.Concat(chunks).Should().Be(new string('x', 100));
    }

    [Fact]
    public void Every_chunk_respects_the_limit()
    {
        var text = string.Join('\n', Enumerable.Range(0, 500).Select(i => $"line {i}"));
        MessageSplitter.Split(text, limit: 200).Should().OnlyContain(c => c.Length <= 200);
    }

    [Fact]
    public void Empty_text_yields_no_chunks_so_nothing_is_sent()
    {
        MessageSplitter.Split(string.Empty).Should().BeEmpty();
    }

    [Fact]
    public void A_boundary_that_lands_inside_a_fenced_block_closes_and_reopens_the_fence_in_every_chunk()
    {
        // A single unbroken line inside the fence, long enough to force more than one internal split, wrapped in
        // prose before and after so the fence is neither the first nor the last thing in the message.
        var code = new string('x', 200);
        var text = "before the block\n```csharp\n" + code + "\n```\nafter the block";

        var chunks = MessageSplitter.Split(text, limit: 60);

        // A splitter that ignores fences entirely would still produce chunks within the length limit (that
        // assertion alone would be vacuous here), so the real assertions are about fence structure: every chunk
        // must contain a triple-backtick marker an even number of times (opened-and-closed within that chunk,
        // never left dangling either way), and a chunk closed with a synthetic ``` must be followed immediately
        // by a chunk reopening with the very same language tag it closed with.
        chunks.Should().HaveCountGreaterThan(1);
        chunks.Should().OnlyContain(c => c.Length <= 60);

        foreach (var chunk in chunks)
        {
            (CountFenceMarkers(chunk) % 2).Should().Be(0, $"chunk {chunk} must be independently balanced");
        }

        for (var i = 0; i < chunks.Count - 1; i++)
        {
            if (!chunks[i].EndsWith("```", StringComparison.Ordinal))
            {
                continue;
            }

            chunks[i + 1].Should().StartWith("```csharp\n", $"chunk {i} closed a csharp fence, so chunk {i + 1} must reopen with the same tag");
        }

        // Every 'x' from the source content must reappear somewhere — fence bookkeeping must add characters, never
        // consume or duplicate the underlying content.
        chunks.Sum(c => c.Count(ch => ch == 'x')).Should().Be(200);
    }

    [Fact]
    public void A_short_fenced_block_that_already_fits_is_left_with_a_single_open_and_close_pair()
    {
        const string text = "before\n```csharp\nvar x = 1;\n```\nafter";

        var chunks = MessageSplitter.Split(text, limit: 4096);

        chunks.Should().ContainSingle().Which.Should().Be(text);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    public void A_limit_below_the_minimum_throws_rather_than_risking_a_hang(int limit)
    {
        var act = () => MessageSplitter.Split("some text", limit);

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName(nameof(limit));
    }

    [Fact]
    public void The_minimum_limit_itself_is_accepted()
    {
        // The companion of the throw test above: proves the boundary is exactly 8, not 9 (an off-by-one that
        // rejected 8 too) and not something smaller that would let A_limit_below_the_minimum's neighbours through.
        var act = () => MessageSplitter.Split("some text", limit: 8);

        act.Should().NotThrow();
    }

    [Fact(Timeout = 5000)]
    public async Task A_language_tag_too_long_for_a_small_limit_reopens_bare_and_completes_without_hanging()
    {
        // A 56-character language tag against a 60-character limit leaves no room to reopen "```<tag>\n" plus a
        // closer plus any content — exactly the shape that, without the bare-reopen fallback and the forward
        // progress guard, would either loop forever (avail == 0, FindBoundary never advances) or throw from
        // LastIndexOf (avail < 0). xUnit's Timeout only applies to async tests, so the actual call runs on a
        // background thread via Task.Run: a regression turns into a failing (timed-out) test instead of a test
        // run that never returns.
        var lang = new string('L', 56);
        var code = new string('x', 300);
        var text = "```" + lang + "\n" + code + "\n```";

        var chunks = await Task.Run(() => MessageSplitter.Split(text, limit: 60));

        chunks.Should().HaveCountGreaterThan(1);
        chunks.Should().OnlyContain(c => c.Length <= 60);

        foreach (var chunk in chunks)
        {
            (CountFenceMarkers(chunk) % 2).Should().Be(0, $"chunk {chunk} must be independently balanced");
        }

        // Round-trip check standing in for concatenation here: chunk boundaries legitimately drop separators and
        // add synthetic fence markers, so raw string.Concat can't equal the source. Counting each character class
        // instead proves nothing was dropped *and* nothing was duplicated. In particular, if the oversized tag had
        // been re-embedded on every reopen instead of dropped, the 'L' count below would be far higher than 56 (it
        // would include one full 56-character copy per reopened chunk); counting exactly 56 proves the fallback
        // fired on every reopen, not just the first.
        chunks.Sum(c => c.Count(ch => ch == 'L')).Should().Be(56);
        chunks.Sum(c => c.Count(ch => ch == 'x')).Should().Be(300);
    }

    private static int CountFenceMarkers(string chunk)
    {
        var count = 0;
        var i = 0;

        while (i + 3 <= chunk.Length)
        {
            if (chunk[i] == '`' && chunk[i + 1] == '`' && chunk[i + 2] == '`')
            {
                count++;
                i += 3;
                continue;
            }

            i++;
        }

        return count;
    }
}
