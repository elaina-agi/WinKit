using System.ComponentModel;
using Microsoft.Win32.SafeHandles;

namespace WinKit.Core.NulCleanup;

/// <summary>
/// Windows-only, fail-closed scanner/deleter for exact NUL ordinary files. Keep the same
/// instance between scanning and deletion: only records from the latest scan are accepted.
/// All work is serialized per instance; cancellation returns explicit partial results.
/// </summary>
public sealed class NulCleanupService
{
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private Snapshot? _snapshot;
    private sealed record Snapshot(string RootPath, string RootIdentity, IReadOnlyDictionary<string, NulFileCandidate> Candidates);

    public async Task<ScanResult> ScanAsync(string rootPath, IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try { return await Task.Run(() => Scan(rootPath, progress, cancellationToken)).ConfigureAwait(false); }
        finally { _operationGate.Release(); }
    }

    public async Task<IReadOnlyList<DeleteResult>> DeleteAsync(string scannedRoot,
        IReadOnlyList<NulFileCandidate> selected, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selected);
        // Materialize before the background operation so caller mutations cannot add unconfirmed rows.
        var items = selected.ToArray();
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try { return await Task.Run(() => Delete(scannedRoot, items, cancellationToken)).ConfigureAwait(false); }
        finally { _operationGate.Release(); }
    }

    private ScanResult Scan(string rootPath, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        _snapshot = null;
        var files = new List<NulFileCandidate>();
        var issues = new List<ScanIssue>();
        var skipped = 0;
        var visited = 0;
        var canonicalRoot = rootPath ?? string.Empty;
        SafePath path;
        if (!OperatingSystem.IsWindows())
            return new ScanResult(canonicalRoot, [], [new ScanIssue(canonicalRoot, "此工具仅支持 Windows。")], 0, false);
        try { path = SafePath.Parse(canonicalRoot); canonicalRoot = path.FullPath; }
        catch (SafetyException error)
        { return new ScanResult(canonicalRoot, [], [new ScanIssue(canonicalRoot, error.Message)], 0, cancellationToken.IsCancellationRequested); }
        if (cancellationToken.IsCancellationRequested) return new ScanResult(canonicalRoot, [], [], 0, true);

        try
        {
            using var root = SafePath.PinnedChain.Open(path);
            Visit(root.Leaf, canonicalRoot, path.Components.Length);
            _snapshot = new Snapshot(canonicalRoot, root.Identity,
                files.ToDictionary(file => file.FullPath, StringComparer.Ordinal));
        }
        catch (Exception error) when (IsExpected(error))
        { issues.Add(new ScanIssue(canonicalRoot, Explain(error))); }

        return new ScanResult(canonicalRoot, files.AsReadOnly(), issues.AsReadOnly(), skipped,
            cancellationToken.IsCancellationRequested);

        void Visit(SafeFileHandle directory, string displayPath, int depth)
        {
            if (cancellationToken.IsCancellationRequested) return;
            visited++;
            Report(displayPath);
            try
            {
                foreach (var entry in NativeFileSystem.Enumerate(directory))
                {
                    if (cancellationToken.IsCancellationRequested) return;
                    var childPath = SafePath.Combine(displayPath, entry.Name);
                    if ((entry.Attributes & NativeFileSystem.ReparseAttribute) != 0) { skipped++; continue; }
                    if ((entry.Attributes & NativeFileSystem.DirectoryAttribute) != 0)
                    {
                        if (depth >= 256 || childPath.Length > SafePath.MaximumLength)
                        {
                            issues.Add(new ScanIssue(childPath, "超过安全路径长度或目录深度限制，未扫描该目录。"));
                            continue;
                        }
                        try
                        {
                            SafePath.ValidateComponent(entry.Name, false);
                            using var child = NativeFileSystem.OpenChild(directory, entry.Name, true);
                            var metadata = NativeFileSystem.GetMetadata(child);
                            if ((metadata.Attributes & NativeFileSystem.ReparseAttribute) != 0) { skipped++; continue; }
                            NativeFileSystem.RequireDirectory(child);
                            Visit(child, childPath, depth + 1);
                        }
                        catch (Exception error) when (IsExpected(error))
                        { issues.Add(new ScanIssue(childPath, Explain(error))); }
                    }
                    else if (string.Equals(entry.Name, "nul", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            if (childPath.Length > SafePath.MaximumLength) throw new SafetyException("目标路径超过安全长度限制。");
                            using var handle = NativeFileSystem.OpenChild(directory, entry.Name, false);
                            var metadata = NativeFileSystem.GetMetadata(handle);
                            if ((metadata.Attributes & NativeFileSystem.ReparseAttribute) != 0) { skipped++; continue; }
                            if (metadata.IsDirectory || metadata.DeletePending)
                                throw new SafetyException("目标已变为目录或待删除项，未纳入结果。");
                            files.Add(new NulFileCandidate(childPath, metadata.Size, metadata.LastWriteTimeUtc, metadata.Identity));
                            Report(displayPath);
                        }
                        catch (Exception error) when (IsExpected(error))
                        { issues.Add(new ScanIssue(childPath, Explain(error))); }
                    }
                }
            }
            catch (Exception error) when (IsExpected(error))
            { issues.Add(new ScanIssue(displayPath, "目录枚举未完成：" + Explain(error))); }
        }

        void Report(string currentDirectory)
        {
            // An observer must not accidentally abort a safe filesystem operation.
            try { progress?.Report(new ScanProgress(visited, files.Count, currentDirectory)); }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
            { issues.Add(new ScanIssue(currentDirectory, "进度通知失败：" + error.Message)); }
        }
    }

    private IReadOnlyList<DeleteResult> Delete(string scannedRoot, NulFileCandidate[] selected,
        CancellationToken cancellationToken)
    {
        var results = new List<DeleteResult>(selected.Length);
        var snapshot = _snapshot;
        SafePath rootPath;
        try
        {
            if (!OperatingSystem.IsWindows()) throw new SafetyException("此工具仅支持 Windows。");
            rootPath = SafePath.Parse(scannedRoot);
            if (snapshot is null || !string.Equals(snapshot.RootPath, rootPath.FullPath, StringComparison.OrdinalIgnoreCase))
                throw new SafetyException("没有该目录的有效扫描记录；请使用同一服务实例重新扫描。");
        }
        catch (Exception error) when (IsExpected(error))
        { return selected.Select(file => new DeleteResult(file, DeleteStatus.Refused, Explain(error))).ToArray(); }

        if (cancellationToken.IsCancellationRequested)
            return selected.Select(file => new DeleteResult(file, DeleteStatus.Refused, "操作已取消，未执行删除。")).ToArray();

        SafePath.PinnedChain root;
        try
        {
            root = SafePath.PinnedChain.Open(rootPath);
            if (!string.Equals(root.Identity, snapshot.RootIdentity, StringComparison.Ordinal))
            {
                root.Dispose();
                throw new SafetyException("扫描根目录的文件身份已改变；请重新扫描。");
            }
        }
        catch (Exception error) when (IsExpected(error))
        {
            // A missing/replaced root is not evidence that each target disappeared. Fail closed.
            return selected.Select(file => new DeleteResult(file,
                error is SafetyException ? DeleteStatus.Refused : DeleteStatus.Failed, Explain(error))).ToArray();
        }

        using (root)
        {
            foreach (var candidate in selected)
            {
                if (cancellationToken.IsCancellationRequested)
                { results.Add(new DeleteResult(candidate, DeleteStatus.Refused, "操作已取消，未执行删除。")); continue; }
                var parents = new List<SafeFileHandle>();
                try
                {
                    var targetPath = SafePath.ParseCandidate(candidate.FullPath);
                    var rootPrefix = rootPath.FullPath.EndsWith('\\') ? rootPath.FullPath : rootPath.FullPath + @"\";
                    if (!targetPath.FullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(targetPath.Anchor, rootPath.Anchor, StringComparison.OrdinalIgnoreCase)
                        || targetPath.Components.Length <= rootPath.Components.Length)
                        throw new SafetyException("目标路径不在本次扫描根目录内，已拒绝。");
                    for (var i = 0; i < rootPath.Components.Length; i++)
                        if (!string.Equals(rootPath.Components[i], targetPath.Components[i], StringComparison.OrdinalIgnoreCase))
                            throw new SafetyException("目标目录链越界，已拒绝。");
                    if (!snapshot.Candidates.TryGetValue(targetPath.FullPath, out var scanned) || scanned != candidate)
                        throw new SafetyException("目标不是最新扫描产生的原始候选项，已拒绝。");

                    var parent = root.Leaf;
                    foreach (var component in targetPath.Components[rootPath.Components.Length..^1])
                    {
                        var next = NativeFileSystem.OpenChild(parent, component, true);
                        parents.Add(next);
                        NativeFileSystem.RequireDirectory(next);
                        parent = next;
                    }
                    using (var file = NativeFileSystem.OpenChild(parent, targetPath.Components[^1], false, deleting: true))
                    {
                        var metadata = NativeFileSystem.GetMetadata(file);
                        if (metadata.IsDirectory || (metadata.Attributes & NativeFileSystem.ReparseAttribute) != 0 || metadata.DeletePending)
                            throw new SafetyException("目标不是可处理的普通文件，已拒绝。");
                        if (!string.Equals(metadata.Identity, candidate.Identity, StringComparison.Ordinal))
                            throw new SafetyException("文件身份与扫描时不同，可能已被替换；请重新扫描。");
                        if (metadata.Size != candidate.Size || metadata.LastWriteTimeUtc != candidate.LastWriteTimeUtc)
                            throw new SafetyException("文件大小或修改时间与扫描时不同；请重新扫描。");
                        if ((metadata.Attributes & NativeFileSystem.ReadOnlyAttribute) != 0)
                            throw new Win32Exception(5, "文件为只读；工具不会清除属性。");
                        if (cancellationToken.IsCancellationRequested)
                            throw new SafetyException("操作已取消，未执行删除。");
                        NativeFileSystem.MarkForDeletion(file);
                    }
                    // No success is reported before disposition succeeds and the deletion handle closes.
                    results.Add(new DeleteResult(candidate, DeleteStatus.Deleted, "已永久删除（不经过回收站）。"));
                }
                catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3)
                { results.Add(new DeleteResult(candidate, DeleteStatus.AlreadyMissing, "文件或其父目录已不存在。")); }
                catch (Exception error) when (IsExpected(error))
                { results.Add(new DeleteResult(candidate, error is SafetyException ? DeleteStatus.Refused : DeleteStatus.Failed, Explain(error))); }
                finally
                { for (var i = parents.Count - 1; i >= 0; i--) parents[i].Dispose(); }
            }
        }
        return results.AsReadOnly();
    }

    private static bool IsExpected(Exception error) => error is SafetyException or Win32Exception
        or UnauthorizedAccessException or IOException or ArgumentException or NotSupportedException;

    private static string Explain(Exception error) => error is Win32Exception native
        ? $"{native.Message}（Windows 错误 {native.NativeErrorCode}）" : error.Message;
}
