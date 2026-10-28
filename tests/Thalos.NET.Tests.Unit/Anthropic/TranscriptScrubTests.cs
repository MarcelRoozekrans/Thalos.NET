using System.Reflection;
using ZeroAlloc.Rest.Anthropic.Transcripts;

namespace Thalos.Tests.Unit.Anthropic;

/// <summary>
/// Guards the recorded transcripts under <c>Anthropic/Transcripts/</c>, copied unedited from ZeroAlloc.Rest's
/// <c>TranscriptScrubTests</c> with the same assertions: no secret in any file, and every expected transcript recorded
/// in full. The output folder is <c>Transcripts/</c> next to the test assembly.
/// </summary>
public sealed class TranscriptScrubTests
{
    private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "Transcripts");

    [Fact]
    public void No_transcript_holds_a_secret()
    {
        var key = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        var leaks = new List<string>();
        var files = Files();
        // Red: break the copy so the output folder is empty; this then fails instead of scanning nothing.
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            foreach (var finding in Findings(File.ReadAllText(file), key))
                leaks.Add($"{Path.GetFileName(file)}: {finding}");
        }

        // Red: add "x-api-key":"x" to one meta file under Transcripts/; that file is then listed here.
        Assert.Empty(leaks);
    }

    [Fact]
    public void The_scrub_finds_each_kind_of_secret()
    {
        // Red: drop the sk-ant- check from Findings; the first line is then empty.
        Assert.Single(Findings("a sk-ant-api03-abc b", null));
        // Red: compare x-api-key case-sensitively; the mixed-case header is then missed.
        Assert.Single(Findings("{\"X-Api-Key\":\"x\"}", null));
        // Red: drop the authorization check from Findings; this is then empty.
        Assert.Single(Findings("Authorization: Bearer x", null));
        // Red: skip the key comparison in Findings; the key is then not found.
        Assert.Single(Findings("the value hunter2 here", "hunter2"));
        // Red: treat an empty key as a key to find; every text then matches.
        Assert.Empty(Findings("clean text", string.Empty));
        // Red: report a finding for clean text, for example always add one; this is then not empty.
        Assert.Empty(Findings("{\"status\":200,\"headers\":{\"request-id\":\"req_1\"}}", "hunter2"));
    }

    [Fact]
    public void Every_transcript_has_its_three_files()
    {
        var missing = new List<string>();
        foreach (var name in TranscriptRequests.Names)
        {
            // Red: delete one of the recorded request files; its name is then listed here.
            AddIfMissing(missing, $"{name}.request.json");
            // Red: delete one of the recorded meta files; its name is then listed here.
            AddIfMissing(missing, $"{name}.meta.json");
            // Red: delete one of the recorded response files; its name is then listed here.
            if (!File.Exists(Path.Combine(Folder, $"{name}.response.json")) && !File.Exists(Path.Combine(Folder, $"{name}.response.sse")))
                missing.Add($"{name}.response.json or .sse");
        }

        Assert.Empty(missing);
    }

    [Fact]
    public void The_output_copy_holds_exactly_the_recorded_files()
    {
        var source = typeof(TranscriptScrubTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => string.Equals(a.Key, "TranscriptsSourceDirectory", StringComparison.Ordinal)).Value;
        Assert.NotNull(source);
        string[] Data(string folder) => Directory.Exists(folder)
            ? [.. Directory.GetFiles(folder).Select(Path.GetFileName).Where(n => !n!.EndsWith(".cs", StringComparison.Ordinal)).Order(StringComparer.Ordinal)!]
            : [];

        var expected = Data(source);

        // Red: hook ClearStaleTranscripts to CopyFilesToOutputDirectory again; it then deletes the copy and this is empty.
        // Red: leave a stale file in the output Transcripts folder, or break the copy glob; the names then differ.
        Assert.Equal(expected, Data(Folder));
    }

    [Fact]
    public void The_output_folder_holds_the_recordings()
    {
        // Red: break the copy or hook the clear after the copy; the folder is then empty and this fails, so the
        // scrub guard cannot scan nothing.
        Assert.NotEmpty(Files());
    }

    private static void AddIfMissing(List<string> missing, string file)
    {
        if (!File.Exists(Path.Combine(Folder, file)))
            missing.Add(file);
    }

    private static string[] Files() => Directory.Exists(Folder) ? Directory.GetFiles(Folder, "*", SearchOption.AllDirectories) : [];

    private static List<string> Findings(string text, string? key)
    {
        var found = new List<string>();
        foreach (var forbidden in new[] { "sk-ant-", "x-api-key", "authorization" })
        {
            if (text.Contains(forbidden, StringComparison.OrdinalIgnoreCase))
                found.Add(forbidden);
        }

        if (!string.IsNullOrEmpty(key) && text.Contains(key, StringComparison.Ordinal))
            found.Add("the value of ANTHROPIC_API_KEY");
        return found;
    }
}
