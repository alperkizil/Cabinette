using System.Collections.Frozen;
using Cabinette.Core.Models;

namespace Cabinette.Core.Categorization;

/// <summary>A built-in category seeded on first run.</summary>
/// <param name="Key">Stable key ("audio", "code.python"). Child keys are "parent.child".</param>
/// <param name="Name">Default display name, also the default vault folder name.</param>
/// <param name="ParentKey">Key of the parent built-in category, or null for top level.</param>
/// <param name="IconKey">Default icon to generate (see the icon generator in Cabinette.Windows).</param>
/// <param name="Extensions">Lower-case extensions without dots that auto-categorize into this category.</param>
/// <param name="Keeps">
/// Extensions this category keeps when files are added to it, although they auto-categorize elsewhere: E-books › PDF keeps the
/// PDFs you put there, while new PDFs go to Documents › PDF (decided 2026-10-24). Not part of the extension → category index.
/// </param>
public sealed record BuiltInCategoryDefinition(
    string Key, string Name, string? ParentKey, string IconKey, IReadOnlyList<string> Extensions, IReadOnlyList<string>? Keeps = null);

/// <summary>Built-in categories and the extension → category mapping (SPEC.md Appendix A). Not user-editable.</summary>
public static class ExtensionMap
{
    public const string OtherKey = "other";
    public const string NotesKey = "notes";
    public const string LinksKey = "links";
    public const string ImagesKey = "images";
    public const string DeletedKey = "deleted";

    /// <summary>Icon key for user-created categories and folders added from disk.</summary>
    public const string FolderIconKey = "folder";

    /// <summary>All built-in categories in seeding order. "deleted" is the system category.</summary>
    public static IReadOnlyList<BuiltInCategoryDefinition> BuiltInCategories { get; } =
    [
        new("audio", "Audio", null, "audio", ["mp3", "wav", "flac", "aac", "m4a", "ogg", "opus", "wma", "aiff", "aif", "mid", "midi", "amr"]),
        new("video", "Video", null, "video", ["mp4", "mkv", "avi", "mov", "wmv", "webm", "flv", "m4v", "mpg", "mpeg", "3gp", "mts", "m2ts"]),
        new("images", "Images", null, "images", ["jpg", "jpeg", "png", "gif", "bmp", "webp", "tif", "tiff", "heic", "heif", "avif", "ico", "raw", "cr2", "nef", "arw", "dng"]),
        new("text", "Text", null, "text", ["txt", "md", "markdown", "log", "nfo"]),
        new("documents", "Documents", null, "documents", ["odt", "rtf", "xps", "oxps", "pages"]),
        new("documents.pdf", "PDF", "documents", "documents.pdf", ["pdf"]),
        new("documents.word", "Word", "documents", "documents.word", ["doc", "docx", "docm", "dot", "dotx", "dotm"]),
        new("spreadsheets", "Spreadsheets", null, "spreadsheets", ["xls", "xlsx", "xlsm", "ods", "csv", "tsv", "numbers"]),
        new("presentations", "Presentations", null, "presentations", ["ppt", "pptx", "odp", "key"]),
        new("code", "Code", null, "code", ["go", "rs", "rb", "php", "swift", "kt", "kts", "lua", "sql", "r", "json", "yaml", "yml", "xml", "toml", "dart", "scala"]),
        new("code.python", "Python", "code", "code.python", ["py", "pyw", "pyi", "ipynb"]),
        new("code.java", "Java", "code", "code.java", ["java", "class", "gradle"]),
        new("code.csharp", "C#", "code", "code.csharp", ["cs", "csproj", "sln", "xaml", "razor", "resx"]),
        new("code.cpp", "C/C++", "code", "code.cpp", ["c", "h", "cpp", "hpp", "cc", "cxx", "hxx"]),
        new("code.jsts", "JS/TS", "code", "code.jsts", ["js", "mjs", "cjs", "ts", "tsx", "jsx"]),
        new("code.web", "Web", "code", "code.web", ["html", "htm", "css", "scss", "sass", "less", "vue", "svelte"]),
        new("code.shell", "Shell", "code", "code.shell", ["sh", "bash", "zsh", "ps1", "psm1", "bat", "cmd"]),
        new("executable", "Executable", null, "executable", ["msix", "msixbundle", "appx", "appxbundle", "com", "jar", "apk", "dll"]),
        new("executable.msi", "MSI", "executable", "executable.msi", ["msi"]),
        new("executable.exe", "Exe", "executable", "executable.exe", ["exe"]),
        new("archives", "Archives", null, "archives", ["zip", "7z", "rar", "tar", "gz", "tgz", "bz2", "xz", "zst", "cab"]),
        new("ebooks", "E-books", null, "ebooks", ["mobi", "azw", "azw3", "fb2", "djvu", "cbz", "cbr"]),
        new("ebooks.epub", "EPUB", "ebooks", "ebooks.epub", ["epub"]),
        new("ebooks.pdf", "PDF", "ebooks", "ebooks.pdf", [], Keeps: ["pdf"]),
        new("fonts", "Fonts", null, "fonts", ["ttf", "otf", "woff", "woff2", "fon"]),
        new("design", "Design", null, "design", ["psd", "ai", "xd", "fig", "sketch", "svg", "eps", "indd", "afdesign", "afphoto", "kra", "xcf", "ora"]),
        new("models3d", "3D models", null, "models3d", ["obj", "fbx", "stl", "blend", "gltf", "glb", "3ds", "dae", "step", "stp", "3mf", "ply"]),
        new("diskimages", "Disk images", null, "diskimages", ["iso", "img", "vhd", "vhdx", "vmdk", "dmg", "wim"]),
        new(NotesKey, "Notes", null, "notes", ["note", "enex", "org"]),
        new(LinksKey, "Links", null, "links", ["url", "website", "lnk", "webloc"]),
        new(OtherKey, "Other", null, "other", []),
        new(DeletedKey, "Deleted", null, "deleted", []),
    ];

    private static readonly FrozenDictionary<string, string> CategoryKeyByExtension = BuildExtensionIndex();

    private static readonly FrozenDictionary<string, BuiltInCategoryDefinition> DefinitionByKey =
        BuiltInCategories.ToFrozenDictionary(d => d.Key, StringComparer.Ordinal);

    /// <summary>Most specific built-in category key for an extension (e.g. "py" → "code.python"); "other" when unknown.</summary>
    public static string GetCategoryKey(string? extension)
    {
        var ext = Normalize(extension);
        return ext.Length > 0 && CategoryKeyByExtension.TryGetValue(ext, out var key) ? key : OtherKey;
    }

    /// <summary>Top-level type key for an extension (e.g. "py" → "code"); "other" when unknown.</summary>
    public static string GetTypeKey(string? extension)
    {
        var key = GetCategoryKey(extension);
        var dot = key.IndexOf('.');
        return dot < 0 ? key : key[..dot];
    }

    /// <summary>
    /// Top-level type key of an item. Notes, pasted images and links made in Cabinette get theirs from their kind
    /// (a note is a .md file, but it is a note, not text); other files from their extension.
    /// </summary>
    public static string GetTypeKey(ItemKind kind, string? extension) => kind switch
    {
        ItemKind.Note => NotesKey,
        ItemKind.PastedImage => ImagesKey,
        ItemKind.Link => LinksKey,
        _ => GetTypeKey(extension),
    };

    public static BuiltInCategoryDefinition? GetDefinition(string key)
        => DefinitionByKey.TryGetValue(key, out var def) ? def : null;

    /// <summary>Lower-case, trimmed, without a leading wildcard or dots ("*.PDF" → "pdf").</summary>
    public static string Normalize(string? extension)
        => string.IsNullOrWhiteSpace(extension) ? "" : extension.Trim().TrimStart('*', '.').Trim().ToLowerInvariant();

    private static FrozenDictionary<string, string> BuildExtensionIndex()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var def in BuiltInCategories)
        {
            foreach (var ext in def.Extensions)
            {
                // ExtensionMapTests guarantees each extension appears once ("ts" is TypeScript, not video).
                map.TryAdd(ext, def.Key);
            }
        }

        return map.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
