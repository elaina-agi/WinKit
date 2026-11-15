using WinKit.Core.NulCleanup;
using WinKit.Core.Tests;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("These native integration tests require Windows.");
    return 2;
}

var tests = new (string Name, Func<Task> Run)[]
{
    ("Exact names, ordinary files and NUL directory exclusion", ExactNames),
    ("Unicode and >260-character paths", LongUnicodePaths),
    ("Untrusted/ambiguous/device input rejection", InvalidInputs),
    ("Outside-root and forged candidates rejected", ForgedCandidates),
    ("Missing target is AlreadyMissing", Missing),
    ("Replaced file identity is rejected", ReplacedFile),
    ("Replaced ordinary scan root is rejected", ReplacedRoot),
    ("Read-only is preserved and reported", ReadOnly),
    ("Sharing violation and retry", Occupied),
    ("Cancellation preserves partial scan and prevents deletes", Cancellation),
    ("Root and nested reparse points are not followed", ReparsePoints),
    ("Replaced ancestor junction is rejected", ReplacedAncestor),
    ("Pinned ancestor cannot rename during scan", PinnedScan),
    ("Hard links with identical identity remain separate paths", HardLinks),
    ("New scan invalidates old candidates", LatestScan),
    ("No-scan delete is rejected", NoScan),
    ("In-place content modification is rejected", ModifiedContent),
    ("Access-denied directories produce explicit partial issues", DeniedDirectory),
    ("Concurrent write handles are reported without modification", ConcurrentWriter),
    ("Case-sensitive exact NUL and nul selection", CaseSensitive),
    ("NUL symbolic link is skipped/refused", FileSymbolicLink),
    ("Missing selected parent is AlreadyMissing", MissingParent),
    ("Progress observer failures are surfaced as issues", ObserverFailure),
    ("Case-sensitive root names cannot alias authorization", CaseSensitiveRoot),
    ("Read handles even with delete sharing fail exclusive deletion", SharedReader),
    ("Ordinary case-insensitive root input stays usable", RootCasing),
};
var passed = 0;
var skipped = 0;
foreach (var test in tests)
{
    try { await test.Run(); passed++; Console.WriteLine("PASS " + test.Name); }
    catch (SkipException reason) { skipped++; Console.WriteLine("SKIP " + test.Name + ": " + reason.Message); }
    catch (Exception error) { Console.Error.WriteLine("FAIL " + test.Name + "\n" + error); }
}
Console.WriteLine($"RESULT {passed} passed, {skipped} skipped, {tests.Length - passed - skipped} failed ({tests.Length} total)");
return passed + skipped == tests.Length ? 0 : 1;

static void Assert(bool condition, string message)
{ if (!condition) throw new InvalidOperationException(message); }
static void ScanOkay(ScanResult scan, int count)
{
    Assert(!scan.WasCancelled, "Unexpected cancellation.");
    Assert(scan.Issues.Count == 0, string.Join("; ", scan.Issues.Select(issue => issue.Path + ": " + issue.Message)));
    Assert(scan.Files.Count == count, $"Expected {count} candidates, got {scan.Files.Count}.");
}
static async Task ExactNames()
{
    using var fixture = new Fixture();
    var service = new NulCleanupService();
    var lower = fixture.File(fixture.Root);
    var upper = fixture.File(fixture.Directory("upper"), "NUL", "UPPER");
    var txt = fixture.File(fixture.Root, "nul.txt");
    var normal = fixture.File(fixture.Root, "null");
    var ordinary = fixture.File(fixture.Root, "ordinary.txt");
    var nulDir = fixture.Directory("directory", "nul");
    fixture.File(nulDir, "ordinary.txt");
    var scan = await service.ScanAsync(fixture.Root);
    Assert(scan.Files.Count == 2, "Exact NUL matching failed.");
    Assert(scan.Issues.Any(issue => issue.Path == nulDir), "Reserved NUL directory restriction must be explicit.");
    Assert(scan.Files.Any(file => file.FullPath == upper && file.Size == 5), "Uppercase/size metadata missing.");
    var results = await service.DeleteAsync(scan.RootPath, scan.Files);
    Assert(results.All(result => result.Status == DeleteStatus.Deleted), string.Join("; ", results.Select(result => result.Message)));
    Assert(!TestNative.Exists(lower) && !TestNative.Exists(upper), "NUL files still exist.");
    Assert(TestNative.Exists(txt) && TestNative.Exists(normal) && TestNative.Exists(ordinary) && TestNative.Exists(nulDir), "Non-target changed.");
}
static async Task LongUnicodePaths()
{
    using var fixture = new Fixture();
    var segments = Enumerable.Range(0, 12).Select(i => "中文目录_" + i + "_" + new string('长', 18)).ToArray();
    var directory = fixture.Directory(segments);
    var path = fixture.File(directory);
    Assert(path.Length > 260, "Not a long-path fixture.");
    var service = new NulCleanupService();
    var scan = await service.ScanAsync(fixture.Root);
    ScanOkay(scan, 1);
    Assert(scan.Files[0].FullPath == path && scan.Files[0].Identity.Length > 40, "Metadata/path mismatch.");
    Assert((await service.DeleteAsync(scan.RootPath, scan.Files))[0].Status == DeleteStatus.Deleted, "Long NUL failed deletion.");
    Assert(!TestNative.Exists(path), "Long file survived.");
}
static async Task InvalidInputs()
{
    using var fixture = new Fixture();
    var service = new NulCleanupService();
    var invalid = new[] { "", "relative", "C:nul", @"\\.\NUL", @"\\?\C:\", @"\??\C:\", "C:/test", @"\\server", @"\\?\GLOBALROOT\Device", fixture.Root + @"\..", fixture.Root + @"\.", fixture.Root + @"\\child", fixture.Root + @"\name.", fixture.Root + @"\name ", fixture.Root + @"\CON", fixture.Root + @"\nul" };
    foreach (var input in invalid)
    {
        var scan = await service.ScanAsync(input);
        Assert(scan.Files.Count == 0 && scan.Issues.Count > 0, "Invalid input accepted: " + input);
    }
}
static async Task ForgedCandidates()
{
    using var fixture = new Fixture();
    var inside = fixture.Directory("scan");
    var outside = fixture.Directory("scan-sibling");
    var path = fixture.File(inside);
    var outsidePath = fixture.File(outside);
    var service = new NulCleanupService();
    var scan = await service.ScanAsync(inside);
    ScanOkay(scan, 1);
    var original = scan.Files[0];
    var forged = new[] { original with { FullPath = outsidePath }, original with { Identity = "forged" }, original with { Size = 999 }, original with { FullPath = path + ".txt" }, original with { FullPath = inside + @"\..\scan-sibling\nul" }, original with { FullPath = TestNative.Extended(path) } };
    var results = await service.DeleteAsync(scan.RootPath, forged);
    Assert(results.All(result => result.Status == DeleteStatus.Refused), "Forged row accepted.");
    Assert(TestNative.Exists(path) && TestNative.Exists(outsidePath), "Forged deletion modified fixture.");
}
static async Task Missing()
{
    using var fixture = new Fixture();
    var path = fixture.File(fixture.Root);
    var service = new NulCleanupService();
    var scan = await service.ScanAsync(fixture.Root); ScanOkay(scan, 1);
    TestNative.DeleteFile(path);
    Assert((await service.DeleteAsync(scan.RootPath, scan.Files))[0].Status == DeleteStatus.AlreadyMissing, "Missing target status wrong.");
}
static async Task ReplacedFile()
{
    using var fixture = new Fixture();
    var path = fixture.File(fixture.Root);
    var service = new NulCleanupService();
    var scan = await service.ScanAsync(fixture.Root); ScanOkay(scan, 1);
    TestNative.Move(path, path + ".original");
    fixture.File(fixture.Root, "nul", "replacement");
    Assert((await service.DeleteAsync(scan.RootPath, scan.Files))[0].Status == DeleteStatus.Refused, "Replacement was not refused.");
    Assert(TestNative.Exists(path), "Replacement was deleted.");
}
static async Task ReplacedRoot()
{
    using var fixture = new Fixture();
    var root = fixture.Directory("scan");
    fixture.File(root);
    var service = new NulCleanupService();
    var scan = await service.ScanAsync(root); ScanOkay(scan, 1);
    TestNative.Move(root, root + "-old");
    TestNative.CreateDirectory(root);
    var newFile = fixture.File(root);
    Assert((await service.DeleteAsync(scan.RootPath, scan.Files))[0].Status == DeleteStatus.Refused, "New ordinary root accepted.");
    Assert(TestNative.Exists(newFile), "Replacement root target was deleted.");
}
static async Task ReadOnly()
{
    using var fixture = new Fixture();
    var path = fixture.File(fixture.Root);
    TestNative.SetReadOnly(path, true);
    var service = new NulCleanupService();
    var scan = await service.ScanAsync(fixture.Root); ScanOkay(scan, 1);
    var result = (await service.DeleteAsync(scan.RootPath, scan.Files))[0];
    Assert(result.Status == DeleteStatus.Failed && TestNative.Exists(path) && (TestNative.Attributes(path) & 1) != 0, "Read-only file changed.");
}
static async Task Occupied()
{
    using var fixture = new Fixture();
    var path = fixture.File(fixture.Root);
    var service = new NulCleanupService();
    var scan = await service.ScanAsync(fixture.Root); ScanOkay(scan, 1);
    using (TestNative.HoldFile(path))
        Assert((await service.DeleteAsync(scan.RootPath, scan.Files))[0].Status == DeleteStatus.Failed, "Occupied file deleted.");
    Assert(TestNative.Exists(path), "Occupied fixture missing.");
    Assert((await service.DeleteAsync(scan.RootPath, scan.Files))[0].Status == DeleteStatus.Deleted, "Retry failed.");
}
static async Task Cancellation()
{
    using var fixture = new Fixture();
    fixture.File(fixture.Root);
    fixture.File(fixture.Directory("nested"));
    var service = new NulCleanupService();
    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
    var first = await service.ScanAsync(fixture.Root, cancellationToken: cancelled.Token);
    Assert(first.WasCancelled && first.Files.Count == 0, "Pre-cancelled scan did work.");
    using var partialToken = new CancellationTokenSource();
    var partial = await service.ScanAsync(fixture.Root, new InlineProgress(progress => { if (progress.FilesFound == 1) partialToken.Cancel(); }), partialToken.Token);
    Assert(partial.WasCancelled && partial.Files.Count == 1, "Partial cancellation lost scan results.");
    Assert((await service.DeleteAsync(partial.RootPath, partial.Files, cancelled.Token))[0].Status == DeleteStatus.Refused, "Cancelled delete ran.");
    Assert(TestNative.Exists(partial.Files[0].FullPath), "Cancelled delete removed file.");
    Assert((await service.DeleteAsync(partial.RootPath, partial.Files))[0].Status == DeleteStatus.Deleted, "Explicit partial-result deletion failed.");
}
static async Task ReparsePoints()
{
    using var fixture = new Fixture();
    var scanRoot = fixture.Directory("scan");
    var outside = fixture.Directory("outside");
    var outsideFile = fixture.File(outside);
    var link = Path.Combine(scanRoot, "junction");
    TestNative.Junction(link, outside);
    var service = new NulCleanupService();
    var scan = await service.ScanAsync(scanRoot);
    ScanOkay(scan, 0);
    Assert(scan.SkippedReparsePoints == 1, "Nested junction was not counted.");
    var direct = await service.ScanAsync(link);
    Assert(direct.Files.Count == 0 && direct.Issues.Count > 0, "Junction root accepted.");
    var insideLink = fixture.Directory("outside", "child");
    fixture.File(insideLink);
    var ancestor = await service.ScanAsync(Path.Combine(link, "child"));
    Assert(ancestor.Files.Count == 0 && ancestor.Issues.Count > 0, "Junction ancestor followed.");
    Assert(TestNative.Exists(outsideFile), "Outside junction target changed.");
}
static async Task ReplacedAncestor()
{
    using var fixture = new Fixture();
    var ancestor = fixture.Directory("ancestor");
    var root = fixture.Directory("ancestor", "scan");
    fixture.File(root);
    var outside = fixture.Directory("outside");
    var outsideRoot = fixture.Directory("outside", "scan");
    var outsideFile = fixture.File(outsideRoot);
    var service = new NulCleanupService();
    var scan = await service.ScanAsync(root); ScanOkay(scan, 1);
    TestNative.Move(ancestor, ancestor + "-old");
    TestNative.Junction(ancestor, outside);
    Assert((await service.DeleteAsync(scan.RootPath, scan.Files))[0].Status == DeleteStatus.Refused, "Replaced ancestor followed.");
    Assert(TestNative.Exists(outsideFile), "Outside NUL was removed.");
}
static async Task PinnedScan()
{
    using var fixture = new Fixture();
    var ancestor = fixture.Directory("ancestor");
    var root = fixture.Directory("ancestor", "scan");
    fixture.File(root);
    var service = new NulCleanupService();
    var attempted = false;
    var scan = await service.ScanAsync(root, new InlineProgress(_ =>
    {
        if (attempted) return;
        attempted = true;
        Assert(!TestNative.TryMove(ancestor, ancestor + "-moved"), "Pinned ancestor renamed during operation.");
        Assert(!TestNative.TryMove(root, root + "-moved"), "Pinned root renamed during operation.");
    }));
    ScanOkay(scan, 1);
    Assert(attempted, "No progress callback.");
}
static async Task HardLinks()
{
    using var fixture = new Fixture();
    var first = fixture.File(fixture.Directory("a"));
    var second = Path.Combine(fixture.Directory("b"), "nul");
    TestNative.HardLink(first, second);
    var service = new NulCleanupService();
    var scan = await service.ScanAsync(fixture.Root); ScanOkay(scan, 2);
    Assert(scan.Files[0].Identity == scan.Files[1].Identity, "Hard links should have same identity.");
    var results = await service.DeleteAsync(scan.RootPath, scan.Files);
    Assert(results.Count == 2 && results.All(result => result.Status == DeleteStatus.Deleted), string.Join("; ", results.Select(result => result.Message)));
    Assert(!TestNative.Exists(first) && !TestNative.Exists(second), "Hard-link name survived.");
}
static async Task LatestScan()
{
    using var fixture = new Fixture();
    var firstRoot = fixture.Directory("first"); fixture.File(firstRoot);
    var secondRoot = fixture.Directory("second"); fixture.File(secondRoot);
    var service = new NulCleanupService();
    var first = await service.ScanAsync(firstRoot);
    var second = await service.ScanAsync(secondRoot); ScanOkay(second, 1);
    Assert((await service.DeleteAsync(first.RootPath, first.Files))[0].Status == DeleteStatus.Refused, "Old scan still authorized.");
}
static async Task NoScan()
{
    using var fixture = new Fixture();
    var path = fixture.File(fixture.Root);
    var result = (await new NulCleanupService().DeleteAsync(fixture.Root, [new NulFileCandidate(path, 4, DateTime.UtcNow, "fake")]))[0];
    Assert(result.Status == DeleteStatus.Refused && TestNative.Exists(path), "No-scan forged candidate allowed.");
}

static async Task ModifiedContent()
{
    using var fixture = new Fixture();
    var path = fixture.File(fixture.Root);
    var service = new NulCleanupService();
    var scan = await service.ScanAsync(fixture.Root); ScanOkay(scan, 1);
    TestNative.RewriteInPlace(path, "longer replacement contents");
    Assert((await service.DeleteAsync(scan.RootPath, scan.Files))[0].Status == DeleteStatus.Refused, "In-place changed file was deleted.");
    Assert(TestNative.Exists(path), "Modified contents disappeared.");
}
static async Task DeniedDirectory()
{
    using var fixture = new Fixture();
    fixture.File(fixture.Root);
    var blocked = fixture.Directory("blocked"); fixture.File(blocked);
    var descriptor = TestNative.DenyDirectoryAccess(blocked);
    try
    {
        var scan = await new NulCleanupService().ScanAsync(fixture.Root);
        Assert(scan.Files.Count == 1 && scan.Issues.Any(issue => issue.Path == blocked && issue.Message.Contains("5")), "Access denial silently omitted.");
        var direct = await new NulCleanupService().ScanAsync(blocked);
        Assert(direct.Files.Count == 0 && direct.Issues.Count > 0, "Access denied root not reported.");
    }
    finally { TestNative.RestoreDirectoryAccess(blocked, descriptor); }
}
static async Task ConcurrentWriter()
{
    using var fixture = new Fixture();
    var path = fixture.File(fixture.Root);
    var service = new NulCleanupService();
    var scan = await service.ScanAsync(fixture.Root); ScanOkay(scan, 1);
    using (TestNative.HoldFile(path, 0x40000000, 7))
        Assert((await service.DeleteAsync(scan.RootPath, scan.Files))[0].Status == DeleteStatus.Failed, "Write handle did not block exclusive deletion.");
    Assert(TestNative.Exists(path), "Concurrently writable file disappeared.");
}
static async Task CaseSensitive()
{
    using var fixture = new Fixture();
    var root = fixture.Directory("case-sensitive");
    if (!TestNative.TryEnableCaseSensitivity(root, out var error))
        throw new SkipException($"case-sensitive directory unsupported/not permitted (Windows error {error}).");
    var lower = fixture.File(root, "nul", "lower");
    var upper = fixture.File(root, "NUL", "UPPERCASE");
    var service = new NulCleanupService();
    var scan = await service.ScanAsync(root); ScanOkay(scan, 2);
    Assert(scan.Files.Select(file => file.Identity).Distinct().Count() == 2, "Case-sensitive entries mapped to same handle.");
    var upperCandidate = scan.Files.Single(file => file.FullPath == upper);
    Assert((await service.DeleteAsync(scan.RootPath, [upperCandidate]))[0].Status == DeleteStatus.Deleted, "Uppercase deletion failed.");
    Assert(!TestNative.Exists(upper) && TestNative.Exists(lower), "Case-sensitive deletion selected wrong path.");
    Assert((await service.DeleteAsync(scan.RootPath, [scan.Files.Single(file => file.FullPath == lower)]))[0].Status == DeleteStatus.Deleted, "Lowercase deletion failed.");
}
static async Task FileSymbolicLink()
{
    using var fixture = new Fixture();
    var target = fixture.File(fixture.Root, "target.txt");
    var link = Path.Combine(fixture.Root, "nul");
    if (!TestNative.TrySymbolicLink(link, target, out var error))
        throw new SkipException($"file symbolic link creation unsupported/not permitted (Windows error {error}).");
    var service = new NulCleanupService();
    var scan = await service.ScanAsync(fixture.Root); ScanOkay(scan, 0);
    Assert(scan.SkippedReparsePoints == 1, "NUL symlink was not skipped.");
    TestNative.DeleteFile(link);
    fixture.File(fixture.Root);
    scan = await service.ScanAsync(fixture.Root); ScanOkay(scan, 1);
    TestNative.DeleteFile(link);
    Assert(TestNative.TrySymbolicLink(link, target, out _), "Could not replace candidate with link.");
    Assert((await service.DeleteAsync(scan.RootPath, scan.Files))[0].Status == DeleteStatus.Refused, "Reparse target accepted for deletion.");
    Assert(TestNative.Exists(target) && TestNative.Exists(link), "Symlink or target changed.");
}
static async Task MissingParent()
{
    using var fixture = new Fixture();
    var parent = fixture.Directory("child"); fixture.File(parent);
    var service = new NulCleanupService();
    var scan = await service.ScanAsync(fixture.Root); ScanOkay(scan, 1);
    TestNative.CleanTree(parent);
    Assert((await service.DeleteAsync(scan.RootPath, scan.Files))[0].Status == DeleteStatus.AlreadyMissing, "Missing parent result wrong.");
}
static async Task ObserverFailure()
{
    using var fixture = new Fixture(); fixture.File(fixture.Root);
    var scan = await new NulCleanupService().ScanAsync(fixture.Root, new InlineProgress(_ => throw new InvalidOperationException("observer")));
    Assert(scan.Files.Count == 1 && scan.Issues.Any(issue => issue.Message.Contains("进度通知失败")), "Observer failure broke scan or was hidden.");
}

static async Task CaseSensitiveRoot()
{
    using var fixture = new Fixture();
    var parent = fixture.Directory("case-parent");
    if (!TestNative.TryEnableCaseSensitivity(parent, out var error)) throw new SkipException("case sensitivity: " + error);
    var lower = fixture.Directory("case-parent", "scan");
    var upper = fixture.Directory("case-parent", "SCAN");
    var lowerFile = fixture.File(lower, "nul", "lower-root");
    var upperFile = fixture.File(upper, "nul", "upper-root");
    var service = new NulCleanupService();
    var scan = await service.ScanAsync(lower); ScanOkay(scan, 1);
    Assert(scan.Files[0].FullPath == lowerFile && scan.Files[0].Size == 10, "Exact lower root failed.");
    var wrongRoot = (await service.DeleteAsync(upper, scan.Files))[0];
    Assert(wrongRoot.Status == DeleteStatus.Refused && TestNative.Exists(lowerFile) && TestNative.Exists(upperFile), "Case-distinct root authorization bypassed identity.");
    scan = await service.ScanAsync(upper); ScanOkay(scan, 1);
    Assert(scan.Files[0].FullPath == upperFile, "Exact uppercase root failed.");
    Assert((await service.DeleteAsync(upper, scan.Files))[0].Status == DeleteStatus.Deleted && TestNative.Exists(lowerFile), "Uppercase root deletion modified lower root.");
}
static async Task SharedReader()
{
    using var fixture = new Fixture(); var path = fixture.File(fixture.Root);
    var service = new NulCleanupService(); var scan = await service.ScanAsync(fixture.Root); ScanOkay(scan, 1);
    using (TestNative.HoldFile(path, 0x80000000, 7))
        Assert((await service.DeleteAsync(scan.RootPath, scan.Files))[0].Status == DeleteStatus.Failed, "Reader escaped exclusive deletion policy.");
    Assert(TestNative.Exists(path), "Occupied read-shared file deleted.");
}
static async Task RootCasing()
{
    using var fixture = new Fixture(); var root = fixture.Directory("mixed-root"); fixture.File(root);
    var service = new NulCleanupService(); var scan = await service.ScanAsync(root.ToUpperInvariant()); ScanOkay(scan, 1);
    Assert((await service.DeleteAsync(scan.RootPath, scan.Files))[0].Status == DeleteStatus.Deleted, "Normal case-insensitive directory input stopped working.");
}

internal sealed class SkipException(string message) : Exception(message);

internal sealed class InlineProgress(Action<ScanProgress> report) : IProgress<ScanProgress>
{
    public void Report(ScanProgress value) => report(value);
}
