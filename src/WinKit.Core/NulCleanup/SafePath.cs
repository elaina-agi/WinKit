using Microsoft.Win32.SafeHandles;

namespace WinKit.Core.NulCleanup;

internal sealed record SafePath(string FullPath, string Anchor, string[] Components)
{
    internal const int MaximumLength = 32700;
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    { "CON", "PRN", "AUX", "NUL", "CLOCK$", "CONIN$", "CONOUT$" };

    internal static SafePath Parse(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) throw new SafetyException("请选择或输入绝对文件夹路径。");
        if (input.Length > MaximumLength || input.Contains('/') || input.StartsWith(@"\\?", StringComparison.Ordinal)
            || input.StartsWith(@"\\.", StringComparison.Ordinal) || input.StartsWith(@"\??", StringComparison.Ordinal))
            throw new SafetyException("仅接受规范的磁盘或 UNC 绝对路径；不接受设备路径、扩展路径或正斜杠。");

        string anchor;
        string remainder;
        if (input.Length >= 3 && char.IsAsciiLetter(input[0]) && input[1] == ':' && input[2] == '\\')
        {
            anchor = char.ToUpperInvariant(input[0]) + @":\";
            remainder = input[3..];
        }
        else if (input.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var parts = input[2..].Split('\\');
            if (parts.Length < 2) throw new SafetyException("UNC 路径必须包含服务器和共享名。");
            ValidateComponent(parts[0], false);
            ValidateComponent(parts[1], false);
            anchor = @"\\" + parts[0] + @"\" + parts[1] + @"\";
            remainder = string.Join(@"\", parts.Skip(2));
        }
        else throw new SafetyException("仅接受形如 C:\\目录 或 \\\\服务器\\共享\\目录 的绝对路径。");

        // A single trailing separator is fine. Internal duplicates, dot segments and ambiguous
        // trailing dots/spaces are rejected rather than silently changing the user's scope.
        if (remainder.EndsWith('\\')) remainder = remainder[..^1];
        var components = remainder.Length == 0 ? [] : remainder.Split('\\');
        foreach (var component in components) ValidateComponent(component, false);
        if (components.Length > 256) throw new SafetyException("路径超过安全目录深度限制（256 层）。");
        return new SafePath(components.Length == 0 ? anchor : anchor + string.Join(@"\", components), anchor, components);
    }

    internal static void ValidateComponent(string name, bool allowNul)
    {
        if (name.Length is 0 or > 255 || name is "." or ".." || name.EndsWith('.') || name.EndsWith(' ')
            || name.Any(c => c < 32 || "\\/:*?\"<>|".Contains(c)))
            throw new SafetyException("路径含有不规范、歧义或不安全的名称。");
        var baseName = name.Split('.')[0];
        var reserved = Reserved.Contains(baseName)
            || (baseName.Length == 4 && (baseName.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                || baseName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && "123456789¹²³".Contains(baseName[3]));
        if (reserved && !(allowNul && string.Equals(name, "nul", StringComparison.OrdinalIgnoreCase)))
            throw new SafetyException("路径中有保留设备名，已拒绝作为目录或非目标文件处理。");
    }

    internal static SafePath ParseCandidate(string input)
    {
        var separator = input.LastIndexOf('\\');
        if (separator < 0 || !string.Equals(input[(separator + 1)..], "nul", StringComparison.OrdinalIgnoreCase))
            throw new SafetyException("仅允许删除名称精确为 nul 的普通文件。");
        // Parse the parent separately: a NUL directory can never become a traversal component.
        var parentText = input[..separator];
        if (parentText.Length == 2 && parentText[1] == ':') parentText += @"\";
        var parent = Parse(parentText);
        ValidateComponent(input[(separator + 1)..], true);
        return new SafePath(Combine(parent.FullPath, input[(separator + 1)..]), parent.Anchor,
            [.. parent.Components, input[(separator + 1)..]]);
    }

    internal static string Combine(string parent, string name) => parent.EndsWith('\\') ? parent + name : parent + @"\" + name;
    internal string ExtendedAnchor => Anchor.StartsWith(@"\\", StringComparison.Ordinal)
        ? @"\\?\UNC\" + Anchor[2..] : @"\\?\" + Anchor;

    internal sealed class PinnedChain : IDisposable
    {
        private readonly List<SafeFileHandle> _handles = [];
        internal SafeFileHandle Leaf => _handles[^1];
        internal string Identity { get; private set; } = string.Empty;

        internal static PinnedChain Open(SafePath path)
        {
            var chain = new PinnedChain();
            try
            {
                chain._handles.Add(NativeFileSystem.OpenAnchor(path.ExtendedAnchor));
                NativeFileSystem.RequireDirectory(chain.Leaf);
                foreach (var component in path.Components)
                {
                    chain._handles.Add(NativeFileSystem.OpenChild(chain.Leaf, component, true));
                    NativeFileSystem.RequireDirectory(chain.Leaf);
                }
                chain.Identity = NativeFileSystem.GetMetadata(chain.Leaf).Identity;
                return chain;
            }
            catch { chain.Dispose(); throw; }
        }

        public void Dispose()
        {
            for (var index = _handles.Count - 1; index >= 0; index--) _handles[index].Dispose();
            _handles.Clear();
        }
    }
}
