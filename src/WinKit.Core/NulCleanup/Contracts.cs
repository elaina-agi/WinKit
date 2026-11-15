namespace WinKit.Core.NulCleanup;

public sealed record NulFileCandidate(string FullPath, long Size, DateTime LastWriteTimeUtc, string Identity);
public sealed record ScanIssue(string Path, string Message);
public sealed record ScanProgress(int DirectoriesVisited, int FilesFound, string CurrentDirectory);
public sealed record ScanResult(string RootPath, IReadOnlyList<NulFileCandidate> Files,
    IReadOnlyList<ScanIssue> Issues, int SkippedReparsePoints, bool WasCancelled);
public enum DeleteStatus { Deleted, AlreadyMissing, Failed, Refused }
public sealed record DeleteResult(NulFileCandidate File, DeleteStatus Status, string Message);
