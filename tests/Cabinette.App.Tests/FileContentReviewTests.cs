using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using Cabinette.App.Services.Ai;
using Cabinette.Ipc;

namespace Cabinette.App.Tests;

/// <summary>
/// Review of read_file (branch review/file-content-bughunt), fixed on fix/file-content: each F-test is the regression
/// test for its finding (its summary says what the finding was). The Coverage test is a stronger walk over the text pager
/// that found nothing (kept as proof).
/// </summary>
public sealed class FileContentReviewTests : IDisposable
{
    private readonly ContentFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private static string CmdExe => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    /// <summary>cmd.exe as a hidden child running <paramref name="command"/> (in <paramref name="workingDirectory"/>).</summary>
    private static ChildCommand Cmd(string command, string? workingDirectory = null)
        => new(CmdExe, ["/d", "/c", command], workingDirectory ?? Environment.SystemDirectory);

    private static Task<ChildProcessRunner.Result> Run(ChildCommand command, long maxOutputBytes, Action<int>? started = null, TimeSpan? timeout = null)
        => ChildProcessRunner.RunAsync(command.Program, command.Arguments, command.WorkingDirectory, timeout ?? TimeSpan.FromSeconds(30), maxOutputBytes,
            started, CancellationToken.None);

    // ------------------------------------------------------------------ F1: arguments silently lost (fixed)

    /// <summary>
    /// F1 was: ChildProcessRunner.RunAsync took a ProcessStartInfo but read only its ArgumentList, so a caller that filled
    /// the (equally public) Arguments string started the program with no arguments at all, which for Cabinette.exe is a
    /// normal app start. Now nothing that starts a child takes a ProcessStartInfo: the arguments are a list, given one by one.
    /// </summary>
    [Fact]
    public void F1_Nothing_that_starts_a_child_takes_a_ProcessStartInfo()
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        var offending = new[] { typeof(ChildProcessRunner), typeof(ChildProcess), typeof(IsolatedTextExtractor), typeof(ChildCommand) }
            .SelectMany(type => type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
            .Where(method => method.GetParameters().Any(parameter => Mentions(parameter.ParameterType, typeof(ProcessStartInfo))))
            .Select(method => $"{method.DeclaringType!.Name}.{method.Name}")
            .ToList();
        Assert.Empty(offending);
    }

    /// <summary>F1: the child gets exactly the arguments it is given (cmd.exe ran "exit 40", so they arrived).</summary>
    [Fact]
    public async Task F1_The_child_gets_exactly_the_arguments_given()
    {
        var run = await Run(Cmd("exit 40"), 1 << 20);
        Assert.Equal(40, run.ExitCode);
    }

    /// <summary>
    /// F1, the same risk from the other side: a Cabinette.exe whose arguments don't make it the extraction child is refused
    /// before anything starts. Checked by name (a Cabinette.exe that doesn't even exist is refused, not "not found") and
    /// with the real build (with only the safety arguments, so even without the check it couldn't be a real start).
    /// </summary>
    [Fact]
    public async Task F1_Cabinette_exe_without_a_child_flag_is_never_started()
    {
        var started = 0;
        var missing = new ChildCommand(Path.Combine(_folder.Path, "CABINETTE.EXE"), ["--tray"], Environment.SystemDirectory);
        await Assert.ThrowsAsync<ArgumentException>(() => Run(missing, 1 << 20, _ => started++));

        SafeChild.EnsureSameBuild();
        await Assert.ThrowsAsync<ArgumentException>(() => Run(SafeChild.WithArguments([], _folder.Path), 1 << 20, _ => started++, TimeSpan.FromSeconds(60)));

        Assert.Equal(0, started);
        Assert.False(Directory.Exists(SafeChild.DataDirectory(_folder.Path)), "Cabinette.exe started like the app (it made a data folder)");
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
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

    private static bool Mentions(Type type, Type target)
        => type == target ||
           (type.HasElementType && Mentions(type.GetElementType()!, target)) ||
           (type.IsGenericType && type.GetGenericArguments().Any(argument => Mentions(argument, target)));

    // ------------------------------------------------------------------ F2: the overflow test was hollow (fixed)

    /// <summary>
    /// F2 was: IsolatedTextExtractorTests.A_child_that_writes_too_much_is_stopped ran <c>type "C:\...\big.txt"</c>, whose
    /// inner quotes ChildProcess escapes CommandLineToArgvW-style (<c>\"</c>), which cmd.exe doesn't read: type never ran,
    /// and "stopped unexpectedly (exit code 1)" satisfied the test instead of the overflow. Now the command runs in the
    /// file's folder without quotes, the control proves it writes the whole file, and the overflow both kills the child
    /// (which would otherwise live a minute) and gives the overflow message.
    /// </summary>
    [Fact]
    public async Task F2_A_child_that_writes_too_much_is_killed_with_the_overflow_message()
    {
        _folder.Text("big.txt", new string('x', 200_000));

        var whole = await Run(Cmd("type big.txt", _folder.Path), 1 << 20);
        Assert.Equal(0, whole.ExitCode);
        Assert.Equal(200_000, whole.Output.Length);

        var pid = 0;
        var clock = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => Run(Cmd("type big.txt& ping -n 60 127.0.0.1 >nul", _folder.Path), 64 * 1024, id => pid = id));

        Assert.Contains("wrote more text", ex.Message);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"took {clock.Elapsed}");
        Assert.NotEqual(0, pid);
        Assert.True(WaitUntil(() => !IsRunning(pid)), "the child still runs");
    }

    // ------------------------------------------------------------------ F3: misleading refusal (fixed)

    /// <summary>
    /// F3 was: .lnk (a Links-type extension) and .class (a Code one) are always binary, but were read as text and refused
    /// with "has the extension of a text file, but its content is binary", both halves false. Now they, and compiled Python
    /// (.pyc, .pyo, type "other"), are refused with what they are, without the text claim.
    /// </summary>
    [Theory]
    [InlineData("shortcut.lnk", "is a Windows shortcut")]
    [InlineData("Main.class", "is compiled Java code")]
    [InlineData("module.cpython-312.pyc", "is compiled Python code")]
    [InlineData("module.pyo", "is compiled Python code")]
    public async Task F3_Always_binary_types_are_refused_as_what_they_are(string name, string what)
    {
        byte[] binary = [0x4C, 0x00, 0x00, 0x00, 0x01, 0x14, 0x02, 0x00, 0xCA, 0xFE, 0xBA, 0xBE, 0x00, 0x00];
        var path = _folder.Bytes(name, binary);
        var ex = await Assert.ThrowsAsync<FileContentRefusedException>(() => new FileContentReader(new FakeTextExtractor()).ReadAsync(ContentItems.For(path), 0));
        Assert.Contains($"{name} {what}", ex.Message);
        Assert.DoesNotContain("extension of a text file", ex.Message);
    }

    /// <summary>F3: refused by extension even when the bytes happen to look like text (never sniffed, never extracted).</summary>
    [Fact]
    public async Task F3_A_shortcut_is_refused_even_when_its_bytes_look_like_text()
    {
        var path = _folder.Text("plain.lnk", "just letters");
        var extractor = new FakeTextExtractor { Extensions = { ".lnk" } };
        var ex = await Assert.ThrowsAsync<FileContentRefusedException>(() => new FileContentReader(extractor).ReadAsync(ContentItems.For(path), 0));
        Assert.Contains("is a Windows shortcut", ex.Message);
        Assert.Empty(extractor.Calls);
    }

    // ------------------------------------------------------------------ F4: offset arithmetic overflow (fixed)

    /// <summary>
    /// F4 was: TextFilePages.ReadPage aligned the offset before clamping it, so for a UTF-16 or UTF-32 file an offset
    /// within (unit - 1) of long.MaxValue wrapped to a negative number and the AI got an IOException ("The parameter is
    /// incorrect"). Now it is clamped to the file first and gets the empty last page, like every other past-the-end offset.
    /// </summary>
    [Theory]
    [InlineData("utf-16le", long.MaxValue)]
    [InlineData("utf-16be", long.MaxValue)]
    [InlineData("utf-32le", long.MaxValue)]
    [InlineData("utf-32le", long.MaxValue - 2)]
    [InlineData("utf-32be", long.MaxValue - 1)]
    [InlineData("utf-8-bom", long.MaxValue)]
    public async Task F4_An_offset_near_long_MaxValue_gives_the_empty_last_page(string name, long offset)
    {
        var encoding = Named(name);
        var path = _folder.Bytes($"{name}.txt", [.. encoding.GetPreamble(), .. encoding.GetBytes("hello")]);

        using (var stream = TextFilePages.Open(path))
        {
            var format = TextFilePages.Sniff(stream);
            Assert.NotNull(format);
            var page = TextFilePages.ReadPage(stream, format, offset);
            Assert.Equal("", page.Text);
            Assert.Null(page.NextOffset);
            Assert.Equal(stream.Length, page.Total);
        }

        // The same through the reader, as read_file sees it.
        var result = await new FileContentReader(new FakeTextExtractor()).ReadAsync(ContentItems.For(path), offset);
        Assert.Equal("", result.Text);
        Assert.Null(result.NextOffset);
    }

    // ------------------------------------------------------------------ F6: overflow lost when the child exits first (fixed)

    /// <summary>
    /// F6 was: the overflow was checked only when the wait for the child was cancelled. A child that wrote too much and
    /// exited before the reader noticed went down the normal path, where the reader's empty result became an empty
    /// extraction with exit code 0. Here that order is fixed: the child has exited, and its output can only be read once
    /// the runner has seen the exit. The run must fail as "wrote more text" all the same.
    /// </summary>
    [Fact]
    public async Task F6_A_child_that_wrote_too_much_and_exited_first_still_fails()
    {
        var child = new ExitedChild(new byte[200]);
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => ChildProcessRunner.RunAsync(() => child, TimeSpan.FromSeconds(30), 100, null,
            CancellationToken.None));
        Assert.Contains("wrote more text", ex.Message);
        Assert.True(child.ExitSeen, "the runner didn't take the normal (exited) path");
        Assert.True(child.Disposed);
    }

    /// <summary>F6, the control: the same exited child within the limit gives its output.</summary>
    [Fact]
    public async Task F6_A_child_that_exited_first_within_the_limit_gives_its_output()
    {
        var child = new ExitedChild("hello"u8.ToArray());
        var run = await ChildProcessRunner.RunAsync(() => child, TimeSpan.FromSeconds(30), 100, null, CancellationToken.None);
        Assert.Equal(0, run.ExitCode);
        Assert.Equal("hello"u8.ToArray(), run.Output);
    }

    /// <summary>A child that has already exited with code 0; its output reads only after the runner waited for the exit.</summary>
    private sealed class ExitedChild(byte[] output) : IRunningChild
    {
        private readonly TaskCompletionSource _exitSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool ExitSeen => _exitSeen.Task.IsCompleted;

        public bool Disposed { get; private set; }

        public int Id => 4242;

        public Stream StandardOutput => field ??= new GatedStream(_exitSeen.Task, output);

        public Stream StandardError => field ??= new GatedStream(_exitSeen.Task, []);

        public int ExitCode => 0;

        public Task WaitForExitAsync(CancellationToken ct)
        {
            _exitSeen.TrySetResult();
            return Task.CompletedTask;
        }

        public void Kill()
        {
        }

        public void Dispose() => Disposed = true;
    }

    /// <summary>A read-only stream over <paramref name="bytes"/> that gives nothing before <paramref name="gate"/> completes.</summary>
    private sealed class GatedStream(Task gate, byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await gate.WaitAsync(cancellationToken);
            return _inner.Read(buffer.Span);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            gate.Wait();
            return _inner.Read(buffer, offset, count);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // ------------------------------------------------------------------ coverage: the pager, walked hard (no finding)

    /// <summary>
    /// Random texts (ASCII, 2-, 3- and 4-byte UTF-8, surrogate pairs, CRLF) in every BOM encoding, page sizes 4–16: a
    /// full walk concatenates to the text, every page moves on, and a page from <i>any</i> byte offset (also inside the
    /// BOM, inside characters and past the end) is a clean substring. Passed on the branch: the pager is sound.
    /// </summary>
    [Theory]
    [InlineData("utf-8")]
    [InlineData("utf-8-bom")]
    [InlineData("utf-16le")]
    [InlineData("utf-16be")]
    [InlineData("utf-32le")]
    [InlineData("utf-32be")]
    public void Coverage_Random_walks_over_the_text_pager_are_consistent(string encodingName)
    {
        var random = new Random(12345);
        var alphabet = new[] { "a", "é", "€", "😀", "𝄞", "\r\n", " ", "İ", "ğ", "中" };
        var encoding = Named(encodingName);
        byte[] preamble = encodingName == "utf-8" ? [] : encoding.GetPreamble();
        for (var round = 0; round < 40; round++)
        {
            var text = string.Concat(Enumerable.Range(0, random.Next(0, 60)).Select(_ => alphabet[random.Next(alphabet.Length)]));
            var bytes = (byte[])[.. preamble, .. encoding.GetBytes(text)];
            var path = _folder.Bytes($"walk{round}.txt", bytes);
            foreach (var pageBytes in new[] { 4, 5, 6, 7, 8, 9, 11, 16 })
            {
                var all = new StringBuilder();
                long? offset = 0;
                var guard = 0;
                while (offset is { } next)
                {
                    var page = Read(path, next, pageBytes);
                    Assert.True(page.NextOffset is null || page.NextOffset > next, $"stuck at {next} ({encodingName}, page {pageBytes}, text '{text}')");
                    Assert.Equal(bytes.Length, page.Total);
                    all.Append(page.Text);
                    offset = page.NextOffset;
                    Assert.True(++guard < 10_000);
                }

                Assert.True(text == all.ToString(), $"walk differs ({encodingName}, page {pageBytes}): '{text}' vs '{all}'");

                for (var o = 0; o <= bytes.Length + 2; o++)
                {
                    var page = Read(path, o, pageBytes);
                    Assert.True(page.Text!.Length == 0 || text.Contains(page.Text, StringComparison.Ordinal),
                        $"page at {o} is not part of the text ({encodingName}, page {pageBytes}): '{page.Text}' in '{text}'");
                    Assert.DoesNotContain('\uFFFD', page.Text);
                }
            }
        }
    }

    private static ReadFileResult Read(string path, long offset, int maxPageBytes)
    {
        using var stream = TextFilePages.Open(path);
        var format = TextFilePages.Sniff(stream);
        Assert.NotNull(format);
        return TextFilePages.ReadPage(stream, format, offset, maxPageBytes);
    }

    private static Encoding Named(string name) => name switch
    {
        "utf-8" or "utf-8-bom" => new UTF8Encoding(true),
        "utf-16le" => new UnicodeEncoding(false, true),
        "utf-16be" => new UnicodeEncoding(true, true),
        "utf-32le" => new UTF32Encoding(false, true),
        "utf-32be" => new UTF32Encoding(true, true),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };
}
