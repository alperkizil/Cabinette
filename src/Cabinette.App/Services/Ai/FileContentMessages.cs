using System.Globalization;
using Cabinette.Core.Categorization;

namespace Cabinette.App.Services.Ai;

/// <summary>
/// Why read_file refused a file, for the AI (the <see cref="FileContentRefusedException"/> messages). These go to the AI
/// client, which works in English, so they aren't localized.
/// </summary>
internal static class FileContentMessages
{
    public const string WhatCanBeRead = "Cabinette can read text files, documents that Windows can extract text from, and images.";

    /// <summary>A file of a type that is never text (audio, video, archives, programs, disk images, fonts) or binary.</summary>
    public static string NotReadable(string fileName, string typeKey, string extension) => typeKey switch
    {
        "audio" => $"{fileName} is an audio file. {WhatCanBeRead}",
        "video" => $"{fileName} is a video file. {WhatCanBeRead}",
        "archives" => $"{fileName} is an archive. {WhatCanBeRead}",
        "executable" => $"{fileName} is a program. {WhatCanBeRead}",
        "diskimages" => $"{fileName} is a disk image. {WhatCanBeRead}",
        "fonts" => $"{fileName} is a font file. {WhatCanBeRead}",
        "models3d" => $"{fileName} is a 3D model in a binary format. {WhatCanBeRead}",
        "design" => $"{fileName} is a design file in a binary format, and no text extractor for {TypeName(extension)} is installed on this PC. {WhatCanBeRead}",
        "documents" or "spreadsheets" or "presentations" or "ebooks" => NoExtractor(fileName, extension),
        _ => $"{fileName} is a binary file, not text. {WhatCanBeRead}",
    };

    /// <summary>A file of a type that is always binary although its category is text-like (shortcuts, compiled code).</summary>
    public static string AlwaysBinary(string fileName, string extension) => ExtensionMap.Normalize(extension) switch
    {
        "lnk" => $"{fileName} is a Windows shortcut (a link to another file), not text. {WhatCanBeRead}",
        "class" => $"{fileName} is compiled Java code, not text. {WhatCanBeRead}",
        "pyc" or "pyo" => $"{fileName} is compiled Python code, not text. {WhatCanBeRead}",
        _ => $"{fileName} is a binary file, not text. {WhatCanBeRead}",
    };

    /// <summary>A document (PDF, Office, e-book) whose type has no IFilter on this PC.</summary>
    public static string NoExtractor(string fileName, string extension)
        => $"No text extractor for {TypeName(extension)} is installed on this PC, so Cabinette can't read {fileName}. {WhatCanBeRead}";

    /// <summary>A file whose extension says text (or a note) but whose content is binary.</summary>
    public static string BinaryDespiteExtension(string fileName)
        => $"{fileName} has the extension of a text file, but its content is binary, so it can't be read as text. {WhatCanBeRead}";

    public static string ExtractionFailed(string fileName, string reason)
        => $"Windows' text extractor couldn't read {fileName}: {reason}";

    public static string ExtractionTimedOut(string fileName, string extension)
        => $"Windows' text extractor for {TypeName(extension)} took too long on {fileName} and was stopped.";

    public static string BeyondExtractionLimit(string fileName, int maxChars)
        => $"Cabinette reads at most the first {maxChars.ToString("N0", CultureInfo.InvariantCulture)} characters of the text of {fileName}.";

    /// <param name="codecMayBeMissing">The type needs an image codec that Windows doesn't always have (heic, avif, raw).</param>
    public static string ImageNotDecodable(string fileName, string extension, bool codecMayBeMissing) => codecMayBeMissing
        ? $"{fileName} can't be decoded as an image: it may be damaged, or no image decoder for {TypeName(extension)} is installed on this PC."
        : $"{fileName} can't be decoded as an image; the file may be damaged.";

    private static string TypeName(string extension)
    {
        var ext = ExtensionMap.Normalize(extension);
        return ext.Length > 0 ? $".{ext} files" : "this type of file";
    }
}
