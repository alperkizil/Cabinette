using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using Cabinette.App.Services;
using Cabinette.App.Services.Ai;
using Cabinette.Windows;

namespace Cabinette.App.Tests;

/// <summary>A test that needs the IFilter of a type only some PCs have: skipped, with the reason, when it isn't installed.</summary>
public sealed class IFilterFactAttribute : FactAttribute
{
    public IFilterFactAttribute(string extension)
    {
        if (IFilterRegistry.FindFilter(extension) is null)
        {
            Skip = $"No IFilter for .{extension} files is installed on this PC.";
        }
    }
}

/// <summary>The hidden <c>--extract-text</c> flags and what the child does with them (in this process, no child).</summary>
public sealed class TextExtractionChildTests
{
    [Fact]
    public void Parses_the_child_flags()
    {
        var options = CommandLineOptions.Parse(["--extract-text", @"C:\Docs\Report.pdf", "--max-chars", "262145"]);
        Assert.True(options.IsTextExtraction);
        Assert.Equal(@"C:\Docs\Report.pdf", options.ExtractTextPath);
        Assert.Equal(262145, options.ExtractMaxChars);
        Assert.False(CommandLineOptions.Parse([]).IsTextExtraction);
        Assert.False(CommandLineOptions.Parse(["--tray"]).IsTextExtraction);
        Assert.False(CommandLineOptions.Parse(["--data-dir", @"C:\Temp\dev"]).IsTextExtraction);
    }

    [Theory]
    [InlineData("--extract-text")]
    [InlineData("--EXTRACT-TEXT", @"C:\x.txt")]
    [InlineData("--extract-text=C:\\x.txt", "--max-chars", "5")]
    [InlineData("--max-chars", "5")]
    [InlineData("--max-chars=5", "--extract-text", @"C:\x.txt")]
    [InlineData("--tray", "--extract-text", @"C:\x.txt", "--max-chars", "abc")]
    public void Any_child_flag_makes_a_child_even_when_malformed(params string[] args)
        => Assert.True(CommandLineOptions.Parse(args).IsTextExtraction);

    [Theory]
    [InlineData(null, 10)]
    [InlineData("", 10)]
    [InlineData("relative.txt", 10)]
    [InlineData(@"C:\x.txt", null)]
    [InlineData(@"C:\x.txt", -1)]
    public async Task A_malformed_request_is_refused(string? path, int? maxChars)
    {
        using var stdout = new MemoryStream();
        using var stderr = new MemoryStream();
        var extractor = new FakeTextExtractor();
        Assert.Equal(TextExtractionChild.BadArguments, await TextExtractionChild.RunAsync(path, maxChars, extractor, stdout, stderr));
        Assert.Empty(extractor.Calls);
        Assert.Equal(0, stdout.Length);
        Assert.Contains("--extract-text", Encoding.UTF8.GetString(stderr.ToArray()));
    }

    [Fact]
    public async Task Writes_the_text_as_utf8_and_says_whether_there_is_more()
    {
        var extractor = new FakeTextExtractor { Texts = { [@"C:\docs\a.pdf"] = "Çağrı 😀 text" } };
        using var stdout = new MemoryStream();
        Assert.Equal(TextExtractionChild.Complete, await TextExtractionChild.RunAsync(@"C:\docs\a.pdf", 100, extractor, stdout, Stream.Null));
        Assert.Equal("Çağrı 😀 text", new UTF8Encoding(false, true).GetString(stdout.ToArray()));
        Assert.False(stdout.ToArray().AsSpan().StartsWith(Encoding.UTF8.Preamble), "no byte-order mark");

        using var truncated = new MemoryStream();
        Assert.Equal(TextExtractionChild.Truncated, await TextExtractionChild.RunAsync(@"C:\docs\a.pdf", 4, extractor, truncated, Stream.Null));
        Assert.Equal("Çağr", Encoding.UTF8.GetString(truncated.ToArray()));
    }

    [Fact]
    public async Task Maps_failures_to_exit_codes()
    {
        async Task<(int Code, string Error)> Run(FakeTextExtractor extractor)
        {
            using var stderr = new MemoryStream();
            var code = await TextExtractionChild.RunAsync(@"C:\docs\a.pdf", 10, extractor, Stream.Null, stderr);
            return (code, Encoding.UTF8.GetString(stderr.ToArray()));
        }

        Assert.Equal(TextExtractionChild.NoExtractor, (await Run(new FakeTextExtractor { ReturnNull = true })).Code);
        Assert.Equal((TextExtractionChild.Unreadable, "locked"), await Run(new FakeTextExtractor { Throw = new IOException("locked") }));
        Assert.Equal(TextExtractionChild.Unreadable, (await Run(new FakeTextExtractor { Throw = new UnauthorizedAccessException("no") })).Code);
        Assert.Equal((TextExtractionChild.Rejected, "password"), await Run(new FakeTextExtractor { Throw = new InvalidDataException("password") }));
        var failed = await Run(new FakeTextExtractor { Throw = new InvalidCastException("boom") });
        Assert.Equal(TextExtractionChild.Failed, failed.Code);
        Assert.Contains("boom", failed.Error);
    }
}

/// <summary>
/// The child process: the launcher's exit-code contract, timeout and kill (with cmd.exe as a fake child, hidden), and the
/// real Cabinette.exe child (always with a temp --data-dir and --screenshot, so even a broken child mode could neither
/// show a window nor touch the real data folder).
/// </summary>
public sealed class IsolatedTextExtractorTests : IDisposable
{
    private readonly ContentFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    // ------------------------------------------------------------------ the contract, with a fake child

    [Theory]
    [InlineData("echo hello& exit 0", false)]
    [InlineData("echo hello& exit 40", true)]
    public async Task Reads_the_text_and_whether_it_was_cut(string command, bool truncated)
    {
        var result = await Fake(command).ExtractAsync(Dummy(), 100);
        Assert.NotNull(result);
        Assert.Equal("hello", result.Text.TrimEnd());
        Assert.Equal(truncated, result.Truncated);
    }

    [Fact]
    public async Task Maps_the_exit_codes_to_exceptions()
    {
        Assert.Null(await Fake("exit 41").ExtractAsync(Dummy(), 100));
        var io = await Assert.ThrowsAsync<IOException>(() => Fake("echo it is locked 1>&2& exit 42").ExtractAsync(Dummy(), 100));
        Assert.Equal("it is locked", io.Message);
        var rejected = await Assert.ThrowsAsync<InvalidDataException>(() => Fake("echo bad file 1>&2& exit 43").ExtractAsync(Dummy(), 100));
        Assert.Equal("bad file", rejected.Message);
        var crashed = await Assert.ThrowsAsync<InvalidDataException>(() => Fake("exit 7").ExtractAsync(Dummy(), 100));
        Assert.Contains("stopped unexpectedly", crashed.Message);
    }

    /// <summary>
    /// "type big.txt" in the file's folder, without quotes: a quoted path inside the argument would be escaped
    /// CommandLineToArgvW-style, which cmd.exe doesn't read (type would never run). The control proves the command writes.
    /// </summary>
    [Fact]
    public async Task A_child_that_writes_too_much_is_stopped()
    {
        _folder.Text("big.txt", new string('x', 200_000));
        var whole = await Fake("type big.txt", workingDirectory: _folder.Path).ExtractAsync(Dummy(), 100_000);
        Assert.NotNull(whole);
        Assert.Equal(new string('x', 200_000), whole.Text);

        // At most 64 KB for 0 characters; the ping would keep the child alive for a minute if it weren't killed.
        var extractor = Fake("type big.txt& ping -n 60 127.0.0.1 >nul", workingDirectory: _folder.Path);
        var pid = 0;
        extractor.Started = id => pid = id;
        var clock = Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => extractor.ExtractAsync(Dummy(), 0));

        Assert.Contains("wrote more text", ex.Message);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"took {clock.Elapsed}");
        Assert.True(WaitUntil(() => !IsRunning(pid)), "the child still runs");
    }

    [Fact]
    public async Task A_child_that_cannot_start_is_an_extraction_failure()
    {
        var extractor = new IsolatedTextExtractor((_, _) => new ChildCommand(Path.Combine(_folder.Path, "missing.exe"), [], null), null, _ => true);
        await Assert.ThrowsAsync<InvalidDataException>(() => extractor.ExtractAsync(Dummy(), 10));
    }

    [Fact]
    public async Task A_hanging_child_is_killed_with_its_children_after_the_timeout()
    {
        var pingsBefore = PingIds();
        var extractor = Fake("ping -n 60 127.0.0.1 >nul", TimeSpan.FromSeconds(1));
        var pid = 0;
        extractor.Started = id => pid = id;
        var clock = Stopwatch.StartNew();

        await Assert.ThrowsAsync<TimeoutException>(() => extractor.ExtractAsync(Dummy(), 10));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"took {clock.Elapsed}");
        Assert.NotEqual(0, pid);
        Assert.True(WaitUntil(() => !IsRunning(pid)), "the child still runs");
        Assert.True(WaitUntil(() => !PingIds().Except(pingsBefore).Any()), "its child (ping) still runs");
    }

    [Fact]
    public async Task A_cancelled_extraction_kills_the_child()
    {
        var extractor = Fake("ping -n 60 127.0.0.1 >nul", TimeSpan.FromSeconds(30));
        var pid = 0;
        extractor.Started = id => pid = id;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var clock = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => extractor.ExtractAsync(Dummy(), 10, cts.Token));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"took {clock.Elapsed}");
        Assert.True(WaitUntil(() => !IsRunning(pid)), "the child still runs");
    }

    [Fact]
    public async Task Starts_no_child_for_a_type_without_a_filter_or_a_missing_file()
    {
        var started = 0;
        var noFilter = new IsolatedTextExtractor((_, _) => throw new InvalidOperationException("no child expected"), null, _ => false)
        {
            Started = _ => started++,
        };
        Assert.Null(await noFilter.ExtractAsync(Dummy(), 10));

        var missing = new IsolatedTextExtractor((_, _) => throw new InvalidOperationException("no child expected"), null, _ => true);
        await Assert.ThrowsAsync<FileNotFoundException>(() => missing.ExtractAsync(Path.Combine(_folder.Path, "gone.pdf"), 10));
        Assert.Equal(0, started);
    }

    [Fact]
    public void The_app_runs_the_child_with_the_flags_it_parses()
    {
        var command = IsolatedTextExtractor.ChildCommandFor(@"C:\App\Cabinette.exe", @"C:\Docs\a b.pdf", 300);
        var options = CommandLineOptions.Parse(command.Arguments);
        Assert.Equal(@"C:\App\Cabinette.exe", command.Program);
        Assert.True(options.IsTextExtraction);
        Assert.Equal(@"C:\Docs\a b.pdf", options.ExtractTextPath);
        Assert.Equal(300, options.ExtractMaxChars);
        Assert.Equal(Environment.SystemDirectory, command.WorkingDirectory);
        Assert.EndsWith("Cabinette.exe", IsolatedTextExtractor.DefaultExecutablePath(), StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ the real child (Cabinette.exe --extract-text)

    [Theory]
    [InlineData("--extract-text")]
    [InlineData("--extract-text", "relative.txt", "--max-chars", "5")]
    [InlineData("--extract-text=C:\\Windows\\win.ini", "--max-chars", "5")]
    [InlineData("--max-chars", "5")]
    [InlineData("--EXTRACT-TEXT", "C:\\Windows\\win.ini", "--max-chars", "-1")]
    public async Task A_malformed_child_command_line_exits_with_an_error_and_starts_nothing(params string[] args)
    {
        SafeChild.EnsureSameBuild();
        var run = await SafeChild.RunAsync(SafeChild.WithArguments(args, _folder.Path), TimeSpan.FromSeconds(60));

        Assert.Equal(TextExtractionChild.BadArguments, run.ExitCode);
        Assert.Empty(run.Output);
        Assert.False(Directory.Exists(SafeChild.DataDirectory(_folder.Path)), "the child started like the app (it made a data folder)");
    }

    [Fact]
    public async Task The_real_child_extracts_html()
    {
        var path = _folder.Text("page.html", "<html><body><p>Hello <b>child</b> process çğü</p></body></html>");
        var result = await SafeChild.Extractor(_folder.Path).ExtractAsync(path, 10_000);

        Assert.NotNull(result);
        Assert.Contains("Hello child process çğü", result.Text);
        Assert.False(result.Truncated);
        Assert.False(Directory.Exists(SafeChild.DataDirectory(_folder.Path)));
    }

    [Fact]
    public async Task The_real_child_stops_at_max_chars()
    {
        var path = Path.Combine(_folder.Path, "unicode.txt");
        File.WriteAllText(path, "Çağrı 😀 İşık and more text", new UnicodeEncoding(false, true));
        var result = await SafeChild.Extractor(_folder.Path).ExtractAsync(path, 8);

        Assert.NotNull(result);
        Assert.True(result.Truncated);
        Assert.Equal("Çağrı 😀", result.Text);
    }

    [Fact]
    public async Task The_real_child_reports_a_locked_file_as_unreadable()
    {
        var path = _folder.Text("locked.txt", "secret");
        using var lockStream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await Assert.ThrowsAsync<IOException>(() => SafeChild.Extractor(_folder.Path).ExtractAsync(path, 100));
    }

    [IFilterFact("pdf")]
    public async Task The_real_child_extracts_pdf()
    {
        var path = _folder.Bytes("hello.pdf", TestDocuments.Pdf("Hello Cabinette PDF"));
        var result = await SafeChild.Extractor(_folder.Path).ExtractAsync(path, 10_000);
        Assert.NotNull(result);
        Assert.Contains("Hello Cabinette PDF", result.Text);
    }

    // Office documents (docx, xlsx, pptx) in the sandboxed child: FileContentSandboxTests.

    [IFilterFact("docx")]
    public async Task The_real_child_rejects_a_damaged_docx()
    {
        var path = _folder.Text("broken.docx", "not a zip package");
        await Assert.ThrowsAsync<InvalidDataException>(() => SafeChild.Extractor(_folder.Path).ExtractAsync(path, 10_000));
    }

    // ------------------------------------------------------------------ helpers

    private string Dummy()
    {
        var path = Path.Combine(_folder.Path, "dummy.pdf");
        if (!File.Exists(path))
        {
            File.WriteAllText(path, "%PDF-1.4");
        }

        return path;
    }

    /// <summary>cmd.exe running <paramref name="command"/> as the child: hidden, no window (CreateNoWindow).</summary>
    private static IsolatedTextExtractor Fake(string command, TimeSpan? timeout = null, string? workingDirectory = null) => new(
        (_, _) => new ChildCommand(Path.Combine(Environment.SystemDirectory, "cmd.exe"), ["/d", "/c", command], workingDirectory ?? Environment.SystemDirectory),
        timeout ?? TimeSpan.FromSeconds(30),
        _ => true);

    private static HashSet<int> PingIds()
    {
        var processes = Process.GetProcessesByName("PING");
        try
        {
            return processes.Select(p => p.Id).ToHashSet();
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool WaitUntil(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(10))
            {
                return false;
            }

            Thread.Sleep(50);
        }

        return true;
    }
}

/// <summary>Minimal documents for the extraction tests.</summary>
internal static partial class TestDocuments
{
    /// <summary>A one-page PDF with one line of Helvetica text.</summary>
    public static byte[] Pdf(string line)
    {
        var content = $"BT /F1 24 Tf 72 700 Td ({line}) Tj ET";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };

        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            pdf.Append($"{offset:D10} 00000 n \n");
        }

        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    /// <summary>A minimal Word document with one paragraph per line (with a styles part, which Office's filter requires).</summary>
    public static byte[] Docx(params string[] paragraphs)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "[Content_Types].xml",
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/><Override PartName="/word/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"/></Types>""");
            Add(zip, "_rels/.rels",
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>""");
            Add(zip, "word/_rels/document.xml.rels",
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>""");
            Add(zip, "word/styles.xml",
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:styles xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"/>""");
            var body = string.Concat(paragraphs.Select(p => $"<w:p><w:r><w:t>{System.Security.SecurityElement.Escape(p)}</w:t></w:r></w:p>"));
            Add(zip, "word/document.xml",
                $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>{body}</w:body></w:document>""");
        }

        return buffer.ToArray();
    }

    private static void Add(ZipArchive zip, string name, string text)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(text);
    }
}
