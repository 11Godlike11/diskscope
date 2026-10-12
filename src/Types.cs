namespace DiskScope;

readonly struct ExtensionKey(char[] pool, int offset, int length, int hash) : IEquatable<ExtensionKey>
{
    public ReadOnlySpan<char> Span => pool.AsSpan(offset, length);
    public override int GetHashCode() => hash;
    public bool Equals(ExtensionKey other) => Span.Equals(other.Span, StringComparison.OrdinalIgnoreCase);
    public override bool Equals(object? obj) => obj is ExtensionKey other && Equals(other);
    public override string ToString() => length == 0 ? "(no extension)" : new string(Span).ToLowerInvariant();
    public static ExtensionKey From(Snapshot s, int index)
    {
        ref var n = ref s.Nodes[index]; var name = s.NamePool.AsSpan(n.NameOffset, n.NameLength);
        int dot = name.LastIndexOf('.'); int length = dot < 0 || dot == name.Length - 1 ? 0 : name.Length - dot;
        var extension = length == 0 ? ReadOnlySpan<char>.Empty : name[dot..];
        return new(s.NamePool, n.NameOffset + (length == 0 ? 0 : dot), length, string.GetHashCode(extension, StringComparison.OrdinalIgnoreCase));
    }
}

sealed class TypeTotal
{
    public long Allocated, Files;
    public int Largest = -1;
}

static class FileTypes
{
    // Classification is a filename hint, not content detection or permission to delete.
    static readonly (string Category, string Extensions)[] groups = [
        ("video", ".mp4 .mkv .avi .mov .wmv .webm .m4v .mpeg .mpg"),
        ("image", ".jpg .jpeg .png .gif .webp .bmp .tif .tiff .heic .svg .ico .raw .psd"),
        ("audio", ".mp3 .flac .wav .aac .ogg .m4a .wma .opus"),
        ("archive", ".zip .7z .rar .tar .gz .bz2 .xz .zst .cab"),
        ("disk_image", ".iso .img .vhd .vhdx .vmdk .qcow2 .dmg"),
        ("installer", ".msi .msix .msixbundle .appx .appxbundle .msp .apk"),
        ("document", ".pdf .doc .docx .xls .xlsx .ppt .pptx .odt .ods .rtf .txt .epub"),
        ("code", ".cs .fs .vb .c .h .cpp .hpp .rs .go .py .js .jsx .ts .tsx .java .html .css .json .xml .yaml .yml .toml .md .ps1 .sh .sql"),
        ("database", ".db .sqlite .sqlite3 .mdb .accdb .mdf .ldf .db3"),
        ("log", ".log .etl .evtx .dmp"),
        ("temp_cache", ".tmp .temp .cache .bak"),
        ("executable/library", ".exe .dll .sys .ocx .pdb .so .dylib .lib .a .wasm")
    ];
    static readonly Dictionary<string, string> categories = BuildCategories();
    static Dictionary<string, string> BuildCategories()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in groups) foreach (string ext in g.Extensions.Split(' ')) result.TryAdd(ext, g.Category);
        return result;
    }
    internal static string Category(string extension) => categories.GetValueOrDefault(extension, "other");
    internal static Dictionary<ExtensionKey, TypeTotal> Aggregate(Snapshot s, int root)
    {
        var result = new Dictionary<ExtensionKey, TypeTotal>(); var stack = new Stack<int>(); stack.Push(root);
        void Add(int i)
        {
            var key = ExtensionKey.From(s, i);
            if (!result.TryGetValue(key, out var total)) result.Add(key, total = new());
            total.Allocated += s.Nodes[i].Allocated; total.Files++;
            if (total.Largest < 0 || s.Nodes[i].Allocated > s.Nodes[total.Largest].Allocated) total.Largest = i;
        }
        while (stack.TryPop(out int i))
        {
            if ((s.Nodes[i].Flags & 1) == 0) { Add(i); continue; }
            foreach (int c in s.ChildNodes(i)) { if ((s.Nodes[c].Flags & 1) != 0) stack.Push(c); else Add(c); }
        }
        return result;
    }
}
