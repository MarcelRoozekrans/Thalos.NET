// Checks that each DLL given embeds its portable PDB, was built deterministically, and carries a Source Link map
// that sends every C# source document to this repository on GitHub. That is what lets a consumer press
// Go to Definition on a package type and land in the original source.
// usage: dotnet run scripts/check-embedded-pdb/check-embedded-pdb.cs -- <expected Source Link prefix> <dll>...
// Exits 0 when every DLL passes, 1 when any fails, and 2 on a usage error.
using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: check-embedded-pdb.cs <expected Source Link prefix> <dll>...");
    return 2;
}

var expectedPrefix = args[0];
var failed = 0;
foreach (var dll in args.Skip(1))
{
    List<string> problems;
    var documentCount = 0;
    try
    {
        problems = Check(dll, expectedPrefix, out documentCount);
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException)
    {
        problems = [$"cannot be read: {e.Message}"];
    }

    if (problems.Count == 0)
    {
        Console.WriteLine($"ok: {dll} ({documentCount} documents)");
        continue;
    }

    failed++;
    foreach (var problem in problems)
    {
        Console.WriteLine($"::error::{dll} {problem}");
    }
}

return failed == 0 ? 0 : 1;

static List<string> Check(string dll, string expectedPrefix, out int documentCount)
{
    var sourceLinkKind = new Guid("CC110556-A091-4D38-9FEC-25AB9A351A6A");
    var embeddedSourceKind = new Guid("0E8A571B-6926-466E-B4AD-8AB04611F5FE");
    var problems = new List<string>();
    documentCount = 0;

    using var pe = new PEReader(File.OpenRead(dll));
    var debugDirectory = pe.ReadDebugDirectory();
    if (!debugDirectory.Any(e => e.Type == DebugDirectoryEntryType.Reproducible))
    {
        problems.Add("is not a deterministic build: its debug directory has no Reproducible entry");
    }

    var embedded = debugDirectory.Where(e => e.Type == DebugDirectoryEntryType.EmbeddedPortablePdb).ToList();
    if (embedded.Count == 0)
    {
        problems.Add("has no embedded PDB, so Go to Definition into it shows decompiled code");
        return problems;
    }

    // Decompresses the PDB, and throws when its size differs from the size the entry declares.
    using var provider = pe.ReadEmbeddedPortablePdbDebugDirectoryData(embedded[0]);
    var pdb = provider.GetMetadataReader();
    documentCount = pdb.Documents.Count;

    string? sourceLinkJson = null;
    var withEmbeddedSource = new HashSet<DocumentHandle>();
    foreach (var handle in pdb.CustomDebugInformation)
    {
        var info = pdb.GetCustomDebugInformation(handle);
        var kind = pdb.GetGuid(info.Kind);
        if (kind == sourceLinkKind && info.Parent.Kind == HandleKind.ModuleDefinition)
        {
            sourceLinkJson = Encoding.UTF8.GetString(pdb.GetBlobBytes(info.Value));
        }
        else if (kind == embeddedSourceKind && info.Parent.Kind == HandleKind.Document
            && IsEmbeddedSource(pdb.GetBlobBytes(info.Value)))
        {
            withEmbeddedSource.Add((DocumentHandle)info.Parent);
        }
    }

    if (sourceLinkJson is null)
    {
        problems.Add("has an embedded PDB without a Source Link map");
        return problems;
    }

    var map = ParseMap(sourceLinkJson, problems);
    if (map is null)
    {
        return problems;
    }

    foreach (var (key, url) in map)
    {
        if (!url.StartsWith(expectedPrefix, StringComparison.Ordinal))
        {
            problems.Add($"maps {key} to {url}, which does not start with {expectedPrefix}");
        }
    }

    // A document with embedded source needs no URL. Every other C# document must sit under the deterministic /_/
    // root that ContinuousIntegrationBuild writes, and resolve through the map.
    foreach (var handle in pdb.Documents)
    {
        var name = pdb.GetString(pdb.GetDocument(handle).Name);
        if (withEmbeddedSource.Contains(handle) || !name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        if (!name.StartsWith("/_/", StringComparison.Ordinal))
        {
            problems.Add($"has the document {name}, which is not under /_/; was it built with ContinuousIntegrationBuild?");
        }
        else if (Resolve(map, name) is null)
        {
            problems.Add($"has the document {name}, which no Source Link entry maps");
        }
    }

    return problems;
}

// Portable PDB spec: embedded source is a 4-byte format, then the text. Format 0 means the text is stored raw; a
// positive format is the text's length, compressed with deflate. Only a well-formed blob exempts its document from
// the map.
static bool IsEmbeddedSource(byte[] blob)
{
    if (blob.Length < 4)
    {
        return false;
    }

    var format = BinaryPrimitives.ReadInt32LittleEndian(blob);
    if (format <= 0)
    {
        return format == 0;
    }

    try
    {
        using var deflate = new DeflateStream(new MemoryStream(blob, 4, blob.Length - 4), CompressionMode.Decompress);
        var text = new MemoryStream();
        deflate.CopyTo(text);
        return text.Length == format;
    }
    catch (InvalidDataException)
    {
        return false;
    }
}

// Reads the documents map of the Source Link JSON. Returns null, with the problems added, when the map is unusable.
static List<(string Key, string Url)>? ParseMap(string json, List<string> problems)
{
    var map = new List<(string Key, string Url)>();
    try
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("documents", out var documents)
            || documents.ValueKind != JsonValueKind.Object)
        {
            problems.Add("has a Source Link map without a documents object");
            return null;
        }

        foreach (var entry in documents.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.String)
            {
                problems.Add($"has the Source Link entry {entry.Name}, whose URL is not a string");
                continue;
            }

            // Source Link spec: a key and its URL both hold no * or both hold exactly one. The key's * must be its
            // last character; the URL's may sit anywhere, as in .../*?raw=1.
            var url = entry.Value.GetString()!;
            var keyWildcards = entry.Name.Count(c => c == '*');
            var urlWildcards = url.Count(c => c == '*');
            if (keyWildcards > 1 || keyWildcards != urlWildcards || (keyWildcards == 1 && !entry.Name.EndsWith('*')))
            {
                problems.Add($"has the Source Link entry {entry.Name} -> {url}, which breaks the wildcard rule");
                continue;
            }

            map.Add((entry.Name, url));
        }
    }
    catch (JsonException e)
    {
        problems.Add($"has a Source Link map that is not valid JSON: {e.Message}");
        return null;
    }

    if (map.Count == 0 && problems.Count == 0)
    {
        problems.Add("has a Source Link map with no documents");
    }

    return problems.Count == 0 ? map : null;
}

// Source Link spec: paths match case-insensitively. An exact key wins; otherwise the longest wildcard key whose
// prefix matches, and the rest of the path, in its original case, replaces the * in the URL.
static string? Resolve(List<(string Key, string Url)> map, string path)
{
    foreach (var (key, url) in map)
    {
        if (!key.EndsWith('*') && string.Equals(key, path, StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }
    }

    string? best = null;
    var bestLength = -1;
    foreach (var (key, url) in map)
    {
        if (!key.EndsWith('*'))
        {
            continue;
        }

        var prefix = key[..^1];
        if (prefix.Length > bestLength && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            best = url.Replace("*", path[prefix.Length..], StringComparison.Ordinal);
            bestLength = prefix.Length;
        }
    }

    return best;
}
