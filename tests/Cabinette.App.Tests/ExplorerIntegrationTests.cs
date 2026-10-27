using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Cabinette.App.Services;
using Cabinette.App.Services.Explorer;
using Cabinette.App.Services.Instance;
using Cabinette.Core.Categorization;
using Cabinette.Core.Models;
using Cabinette.Core.Services;
using Cabinette.Ipc;
using Cabinette.Windows;

namespace Cabinette.App.Tests;

/// <summary>COM's <c>-Embedding</c> flag and the restart arguments.</summary>
public class EmbeddingFlagTests
{
    [Theory]
    [InlineData("-Embedding")]
    [InlineData("/Embedding")]
    [InlineData("-EMBEDDING")]
    [InlineData("/embedding")]
    public void Coms_flag_is_recognized_in_both_spellings(string flag)
    {
        var options = CommandLineOptions.Parse([flag]);

        Assert.True(options.IsEmbedding);
        Assert.True(options.StartQuietly);
        Assert.False(options.StartInTray);
        Assert.False(options.IsDevRun);
        Assert.True(CommandLineOptions.IsEmbeddingArgument(flag));
    }

    [Theory]
    [InlineData("--embedding")]
    [InlineData("Embedding")]
    [InlineData("-Embeddings")]
    [InlineData("-Embed")]
    public void Look_alikes_are_not_the_flag(string arg)
    {
        Assert.False(CommandLineOptions.Parse([arg]).IsEmbedding);
        Assert.False(CommandLineOptions.IsEmbeddingArgument(arg));
    }

    [Fact]
    public void A_dev_entry_starts_the_dev_run_with_its_data_dir_then_comes_flag()
    {
        // LocalServer32 = "<exe>" --data-dir "<dir>", and COM appends -Embedding.
        var options = CommandLineOptions.Parse(["--data-dir", @"C:\Temp\cabinette-dev", "-Embedding"]);

        Assert.True(options.IsEmbedding);
        Assert.Equal(@"C:\Temp\cabinette-dev", options.DataDirectory);
        Assert.True(options.IsDevRun);
        Assert.False(options.IsSandboxed);
    }

    [Fact]
    public void Quiet_starts()
    {
        Assert.False(CommandLineOptions.Parse([]).StartQuietly);
        Assert.True(CommandLineOptions.Parse(["--tray"]).StartQuietly);
        Assert.True(CommandLineOptions.Parse(["--tray", "-Embedding"]).StartQuietly);
    }

    [Fact]
    public void A_restart_drops_the_quiet_flags_only()
    {
        Assert.Equal(
            ["--data-dir", @"C:\x", "--theme", "dark"],
            AppLifetime.RestartArguments(["--data-dir", @"C:\x", "-Embedding", "--TRAY", "/Embedding", "--theme", "dark"]));
    }
}

/// <summary>Which role a process takes, and which COM classes it serves.</summary>
public class ExplorerStartupDecisionTests
{
    [Theory]
    [InlineData(new string[0], true, StartupRole.Primary)]
    [InlineData(new[] { "-Embedding" }, true, StartupRole.Primary)]
    [InlineData(new[] { "--tray" }, true, StartupRole.Primary)]
    [InlineData(new string[0], false, StartupRole.ActivateRunning)]
    [InlineData(new[] { "--tray" }, false, StartupRole.ExitQuietly)]
    [InlineData(new[] { "-Embedding" }, false, StartupRole.ForwardExplorerDrop)]
    [InlineData(new[] { "/Embedding" }, false, StartupRole.ForwardExplorerDrop)]
    [InlineData(new[] { "--tray", "-Embedding" }, false, StartupRole.ForwardExplorerDrop)]
    [InlineData(new[] { "--data-dir", @"C:\Temp\dev", "-Embedding" }, false, StartupRole.ForwardExplorerDrop)]
    public void Forward_or_handle(string[] args, bool isPrimary, StartupRole expected)
    {
        Assert.Equal(expected, ExplorerStartup.Decide(isPrimary, CommandLineOptions.Parse(args)));
    }

    [Theory]
    [InlineData(new string[0], ExplorerClasses.Installed)]
    [InlineData(new[] { "--tray" }, ExplorerClasses.Installed)]
    [InlineData(new[] { "-Embedding" }, ExplorerClasses.Installed)]
    [InlineData(new[] { "--data-dir", @"C:\Temp\dev" }, ExplorerClasses.Dev)]
    [InlineData(new[] { "--data-dir", @"C:\Temp\dev", "-Embedding" }, ExplorerClasses.Dev)]
    [InlineData(new[] { "--sample-data" }, ExplorerClasses.None)]
    [InlineData(new[] { "--screenshot", "x.png" }, ExplorerClasses.None)]
    [InlineData(new[] { "--data-dir", @"C:\Temp\dev", "--screenshot", "x.png" }, ExplorerClasses.None)]
    public void Dev_runs_serve_the_dev_classes_and_sandboxed_runs_none(string[] args, ExplorerClasses expected)
    {
        Assert.Equal(expected, ExplorerStartup.ClassesFor(CommandLineOptions.Parse(args)));
    }

    [Fact]
    public void The_servers_use_the_contract_ids()
    {
        Assert.Null(ExplorerStartup.CreateServer(ExplorerClasses.None));

        using var installed = Assert.IsType<ExplorerDropServer>(ExplorerStartup.CreateServer(ExplorerClasses.Installed));
        Assert.Equal(ExplorerIntegration.CopyDropTargetClsid, installed.CopyClsid);
        Assert.Equal(ExplorerIntegration.PointerDropTargetClsid, installed.PointerClsid);
        Assert.False(installed.IsRegistered);

        using var dev = Assert.IsType<ExplorerDropServer>(ExplorerStartup.CreateServer(ExplorerClasses.Dev));
        Assert.Equal(ExplorerIntegration.DevCopyDropTargetClsid, dev.CopyClsid);
        Assert.Equal(ExplorerIntegration.DevPointerDropTargetClsid, dev.PointerClsid);
    }

    /// <summary>
    /// A copy started as administrator holds the mutex with security this account's normal processes can't open: that
    /// still means "Cabinette is running" (so an Explorer entry's copy forwards instead of opening the library twice).
    /// </summary>
    [Fact]
    public void A_mutex_this_account_cannot_open_counts_as_running()
    {
        var name = @"Local\Cabinette-test-" + Guid.NewGuid().ToString("N");
        var security = new MutexSecurity();
        security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), MutexRights.FullControl, AccessControlType.Allow));
        using var elevatedLike = MutexAcl.Create(initiallyOwned: false, name, out var created, security);
        Assert.True(created);

        SingleInstanceGuard? guard = null;
        var thread = new Thread(() => guard = SingleInstanceGuard.TryAcquire(name));
        thread.Start();
        thread.Join();

        Assert.Null(guard);
    }
}

/// <summary>The local channel's "add" command, as the running copy handles it.</summary>
public class ExplorerAddMessageTests
{
    [Fact]
    public void An_add_message_serializes_like_the_protocol_says()
    {
        Assert.Equal(
            """{"command":"add","mode":"pointer","paths":["C:\\a.txt","D:\\Photos"]}""",
            InstanceProtocol.Serialize(InstanceModes.AddMessage(ItemMode.Pointer, [@"C:\a.txt", @"D:\Photos"])));
        Assert.Equal("copy", InstanceModes.AddMessage(ItemMode.Copy, [@"C:\a.txt"]).Mode);

        // Other characters are escaped, so they round-trip whatever the console code page is.
        Assert.True(InstanceProtocol.TryParseMessage(InstanceProtocol.Serialize(InstanceModes.AddMessage(ItemMode.Copy, [@"D:\Fotoğraflar\çay.jpg"])), out var parsed, out _));
        Assert.Equal([@"D:\Fotoğraflar\çay.jpg"], parsed.Paths);
    }

    [Fact]
    public void A_valid_message_becomes_a_drop()
    {
        Assert.True(InstanceProtocol.TryParseMessage("""{"command":"add","mode":" Pointer ","paths":["C:\\a.txt","\\\\nas\\share\\b.pdf","c:\\A.TXT"]}""", out var message, out _));

        Assert.True(ExplorerStartup.TryReadAddMessage(message, out var drop, out var error));
        Assert.Equal("", error);
        Assert.Equal(ItemMode.Pointer, drop.Mode);
        Assert.Equal([@"C:\a.txt", @"\\nas\share\b.pdf"], drop.Paths);
    }

    [Theory]
    [InlineData("""{"command":"add","paths":["C:\\a.txt"]}""")]
    [InlineData("""{"command":"add","mode":"move","paths":["C:\\a.txt"]}""")]
    [InlineData("""{"command":"add","mode":"copy"}""")]
    [InlineData("""{"command":"add","mode":"copy","paths":[]}""")]
    [InlineData("""{"command":"add","mode":"copy","paths":["  "]}""")]
    [InlineData("""{"command":"add","mode":"copy","paths":["relative\\a.txt"]}""")]
    [InlineData("""{"command":"add","mode":"copy","paths":["C:\\ok.txt","C:a.txt"]}""")]
    [InlineData("""{"command":"add","mode":"copy","paths":["C:\\bad|name.txt"]}""")]
    public void Invalid_messages_are_refused_whole(string line)
    {
        Assert.True(InstanceProtocol.TryParseMessage(line, out var message, out _));

        Assert.False(ExplorerStartup.TryReadAddMessage(message, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public async Task The_handler_takes_valid_messages_the_same_way_as_a_drop()
    {
        var dispatcher = new InstanceMessageDispatcher();
        var accepted = new List<ExplorerDrop>();
        ExplorerStartup.RegisterAddCommand(dispatcher, drop =>
        {
            accepted.Add(drop);
            return true;
        });

        var ok = InstanceProtocol.TryParseReply(await dispatcher.HandleLineAsync("""{"command":"add","mode":"copy","paths":["C:\\a.txt"]}"""));
        var bad = InstanceProtocol.TryParseReply(await dispatcher.HandleLineAsync("""{"command":"add","mode":"copy","paths":["a.txt"]}"""));

        Assert.True(ok!.Ok);
        Assert.False(bad!.Ok);
        var drop = Assert.Single(accepted);
        Assert.Equal(ItemMode.Copy, drop.Mode);
        Assert.Equal([@"C:\a.txt"], drop.Paths);
    }

    [Fact]
    public async Task A_closed_queue_is_reported_back()
    {
        var dispatcher = new InstanceMessageDispatcher();
        ExplorerStartup.RegisterAddCommand(dispatcher, _ => false);

        var reply = InstanceProtocol.TryParseReply(await dispatcher.HandleLineAsync("""{"command":"add","mode":"copy","paths":["C:\\a.txt"]}"""));

        Assert.False(reply!.Ok);
    }

    /// <summary>A real (private, uniquely named) pipe: what the forwarding copy sends arrives as one drop.</summary>
    [Fact]
    public async Task A_forwarded_selection_arrives_over_the_pipe()
    {
        var name = "Cabinette-test-" + Guid.NewGuid().ToString("N");
        var received = new TaskCompletionSource<ExplorerDrop>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = new InstanceMessageDispatcher();
        ExplorerStartup.RegisterAddCommand(dispatcher, received.TrySetResult);
        await using var server = new InstancePipeServer(name, dispatcher.HandleLineAsync);
        server.Start();
        string[] paths = [.. Enumerable.Range(0, 2_000).Select(i => $@"C:\Users\you\Pictures\Holiday 2026\IMG_{i:D5}.jpg")];

        var reply = await InstancePipeClient.SendAsync(name, InstanceModes.AddMessage(ItemMode.Pointer, paths), TimeSpan.FromSeconds(10));

        Assert.True(reply!.Ok);
        var drop = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ItemMode.Pointer, drop.Mode);
        Assert.Equal(paths, drop.Paths);
    }
}

/// <summary>Selections wait until the library is open with a vault; first run without a vault adds nothing.</summary>
public class ExplorerDropQueueTests
{
    private static ExplorerDrop Drop(string name, ItemMode mode = ItemMode.Copy) => new(mode, [$@"C:\{name}"]);

    [Fact]
    public void Selections_wait_for_the_library_then_go_in_arrival_order()
    {
        var queue = new ExplorerDropQueue();
        var handled = new List<ExplorerDrop>();

        Assert.True(queue.Enqueue(Drop("1")));
        Assert.True(queue.Enqueue(Drop("2", ItemMode.Pointer)));
        Assert.Equal(2, queue.PendingCount);
        Assert.Empty(handled);

        queue.Open(drop =>
        {
            handled.Add(drop);
            return Task.CompletedTask;
        });
        Assert.Equal([@"C:\1", @"C:\2"], handled.Select(d => d.Paths[0]));
        Assert.Equal(ItemMode.Pointer, handled[1].Mode);
        Assert.Equal(0, queue.PendingCount);

        // Once open, each new selection goes straight on.
        Assert.True(queue.Enqueue(Drop("3")));
        Assert.Equal(3, handled.Count);
        Assert.True(queue.IsOpen);
    }

    [Fact]
    public void First_run_closed_without_a_vault_adds_nothing()
    {
        var queue = new ExplorerDropQueue();
        var handled = new List<ExplorerDrop>();
        queue.Enqueue(Drop("1"));
        queue.Enqueue(Drop("2"));

        Assert.Equal(2, queue.Close());

        Assert.False(queue.Enqueue(Drop("3")));
        queue.Open(drop =>
        {
            handled.Add(drop);
            return Task.CompletedTask;
        });
        Assert.Empty(handled);
        Assert.Equal(0, queue.PendingCount);
        Assert.True(queue.IsClosed);
        Assert.False(queue.IsOpen);
    }

    [Fact]
    public void Quitting_refuses_later_selections()
    {
        var queue = new ExplorerDropQueue();
        var handled = 0;
        queue.Open(_ =>
        {
            handled++;
            return Task.CompletedTask;
        });
        queue.Enqueue(Drop("1"));

        Assert.Equal(0, queue.Close());
        Assert.False(queue.Enqueue(Drop("2")));
        Assert.Equal(1, handled);
    }

    [Fact]
    public void A_failing_handler_never_reaches_the_caller()
    {
        var queue = new ExplorerDropQueue();
        var calls = 0;
        queue.Open(_ =>
        {
            calls++;
            return calls == 1 ? throw new InvalidOperationException("sync failure") : Task.FromException(new InvalidOperationException("async failure"));
        });

        Assert.True(queue.Enqueue(Drop("1")));
        Assert.True(queue.Enqueue(Drop("2")));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Selections_from_many_threads_are_all_handled_once()
    {
        var queue = new ExplorerDropQueue();
        var handled = new System.Collections.Concurrent.ConcurrentBag<string>();
        var senders = Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < 50; i++)
            {
                queue.Enqueue(Drop($"{t}-{i}"));
            }
        })).ToList();

        queue.Open(drop =>
        {
            handled.Add(drop.Paths[0]);
            return Task.CompletedTask;
        });
        await Task.WhenAll(senders);

        Assert.Equal(400, handled.Count);
        Assert.Equal(400, handled.Distinct().Count());
    }
}

/// <summary>The copy COM starts while another Cabinette runs: take the drop, pass it on, exit.</summary>
public class ExplorerDropForwarderTests
{
    private static readonly ExplorerDrop Selection = new(ItemMode.Pointer, [@"C:\a.txt", @"D:\Folder"]);

    [Fact]
    public async Task A_drop_is_passed_on_and_the_classes_are_revoked()
    {
        var server = new FakeServer();
        var sent = new List<ExplorerDrop>();
        server.AfterRegister = () => _ = Task.Run(async () =>
        {
            await Task.Delay(20);
            server.Raise(Selection);
        });
        var forwarder = new ExplorerDropForwarder(server, drop =>
        {
            lock (sent)
            {
                sent.Add(drop);
            }

            return Task.FromResult(true);
        }, TimeSpan.FromSeconds(10));

        var outcome = await forwarder.RunAsync();

        Assert.Equal(ForwardOutcome.Forwarded, outcome);
        Assert.Same(Selection, Assert.Single(sent));
        Assert.Equal(1, server.RegisterCalls);
        Assert.True(server.RevokeCalls >= 1);
        Assert.False(server.IsRegistered);
        Assert.False(server.HasHandlers);
    }

    [Fact]
    public async Task No_drop_in_time_ends_quietly()
    {
        var server = new FakeServer();
        var forwarder = new ExplorerDropForwarder(server, _ => throw new InvalidOperationException("nothing to send"), TimeSpan.FromMilliseconds(100));

        Assert.Equal(ForwardOutcome.NoDrop, await forwarder.RunAsync());
        Assert.False(server.IsRegistered);
        Assert.False(server.HasHandlers);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_refused_or_failed_forward_is_reported(bool throws)
    {
        var server = new FakeServer();
        server.AfterRegister = () => server.Raise(Selection);
        var forwarder = new ExplorerDropForwarder(
            server,
            _ => throws ? Task.FromException<bool>(new IOException("pipe broke")) : Task.FromResult(false),
            TimeSpan.FromSeconds(10));

        Assert.Equal(ForwardOutcome.Failed, await forwarder.RunAsync());
        Assert.False(server.IsRegistered);
    }

    [Fact]
    public async Task A_slow_forward_is_waited_for_even_past_the_drop_timeout()
    {
        var server = new FakeServer();
        server.AfterRegister = () => server.Raise(Selection);
        var forwarder = new ExplorerDropForwarder(server, async _ =>
        {
            await Task.Delay(300);
            return true;
        }, TimeSpan.FromMilliseconds(50));

        Assert.Equal(ForwardOutcome.Forwarded, await forwarder.RunAsync());
    }

    [Fact]
    public async Task Without_registration_nothing_can_arrive()
    {
        var server = new FakeServer { RegisterError = new COMException("CoRegisterClassObject failed", unchecked((int)0x80004005)) };
        var forwarder = new ExplorerDropForwarder(server, _ => Task.FromResult(true), TimeSpan.FromSeconds(10));

        Assert.Equal(ForwardOutcome.NotRegistered, await forwarder.RunAsync());
        Assert.False(server.HasHandlers);
    }

    private sealed class FakeServer : IExplorerDropServer
    {
        private EventHandler<ExplorerDrop>? _dropped;

        public bool IsRegistered { get; private set; }

        public int RegisterCalls { get; private set; }

        public int RevokeCalls { get; private set; }

        public Exception? RegisterError { get; init; }

        public Action? AfterRegister { get; set; }

        public bool HasHandlers => _dropped is not null;

        public event EventHandler<ExplorerDrop>? Dropped
        {
            add => _dropped += value;
            remove => _dropped -= value;
        }

        public event EventHandler<Exception>? DropFailed
        {
            add { }
            remove { }
        }

        public void Register()
        {
            RegisterCalls++;
            if (RegisterError is not null)
            {
                throw RegisterError;
            }

            IsRegistered = true;
            AfterRegister?.Invoke();
        }

        public void Revoke()
        {
            RevokeCalls++;
            IsRegistered = false;
        }

        public void Dispose() => Revoke();

        public void Raise(ExplorerDrop drop) => _dropped?.Invoke(this, drop);
    }
}

/// <summary>The toast texts for an Explorer selection.</summary>
public sealed class ExplorerAddTextTests : IDisposable
{
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;

    public ExplorerAddTextTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
    }

    public void Dispose() => CultureInfo.CurrentCulture = _culture;

    private static readonly Dictionary<long, string> Names = new() { [1] = "Images", [2] = "Documents", [3] = "Holiday", [4] = "2024" };

    private static string Name(long id) => Names.GetValueOrDefault(id, "");

    private static LibraryItem Item(long id, long home, string name = "a.jpg") => new() { Id = id, FileName = name, FullPath = $@"C:\Vault\{name}", HomeCategoryId = home };

    [Fact]
    public void Files_added_to_one_category_like_the_vault_monitor_says_it()
    {
        var result = new AddResult { Added = [Item(1, 1), Item(2, 1), Item(3, 1)] };

        var message = ExplorerAddText.Build(ItemMode.Copy, result, Name, includeSuccess: true)!;

        Assert.Equal("Copy to Cabinette", message.Title);
        Assert.Equal(["3 files added to Images"], message.Lines);
        Assert.Equal(1, message.CategoryId);
    }

    [Fact]
    public void A_folder_becomes_a_category_and_says_so()
    {
        var result = new AddResult { Added = [Item(1, 3), Item(2, 4), Item(3, 4)], CreatedCategoryIds = [3, 4] };

        var message = ExplorerAddText.Build(ItemMode.Pointer, result, Name, includeSuccess: true)!;

        Assert.Equal("Add to Cabinette (Pointer)", message.Title);
        Assert.Equal(["3 files added to 2024 and Holiday", "New categories: Holiday and 2024"], message.Lines);
        Assert.Null(message.CategoryId);
        Assert.Equal("3 files added to 2024 and Holiday\nNew categories: Holiday and 2024", message.Body);
    }

    [Fact]
    public void Duplicates_and_failures_are_listed()
    {
        var one = new AddResult
        {
            Added = [Item(1, 2, "b.pdf")],
            Duplicates = [new DuplicateInfo(@"C:\src\a.jpg", Item(9, 1, "a.jpg"), false)],
            Failures = [new AddFailure(@"C:\src\locked.docx", "It's open in another program.")],
        };
        Assert.Equal(
            ["1 file added to Documents", "“a.jpg” is already in Cabinette (in: Images)", "Couldn't add “locked.docx”: It's open in another program."],
            ExplorerAddText.Build(ItemMode.Copy, one, Name, includeSuccess: true)!.Lines);

        var many = new AddResult
        {
            Duplicates = [new DuplicateInfo(@"C:\a", Item(8, 1), false), new DuplicateInfo(@"C:\b", Item(9, 1), true)],
            Failures = [new AddFailure(@"C:\x\", "gone"), new AddFailure(@"C:\y", "gone")],
        };
        Assert.Equal(
            ["2 files were already in Cabinette", "2 files couldn't be added"],
            ExplorerAddText.Build(ItemMode.Copy, many, Name, includeSuccess: true)!.Lines);
    }

    [Fact]
    public void With_notifications_off_only_failures_are_reported()
    {
        var fine = new AddResult { Added = [Item(1, 1)], Duplicates = [new DuplicateInfo(@"C:\a", Item(9, 1), false)] };
        Assert.Null(ExplorerAddText.Build(ItemMode.Copy, fine, Name, includeSuccess: false));

        var failed = fine with { Failures = [new AddFailure(@"C:\gone.txt", "The file wasn't found.")] };
        var message = ExplorerAddText.Build(ItemMode.Copy, failed, Name, includeSuccess: false)!;
        Assert.Equal(["Couldn't add “gone.txt”: The file wasn't found."], message.Lines);
        Assert.Null(message.CategoryId);
    }

    [Fact]
    public void Nothing_at_all_still_answers()
    {
        Assert.Equal(["Nothing was added"], ExplorerAddText.Build(ItemMode.Copy, new AddResult(), Name, includeSuccess: true)!.Lines);
        Assert.Equal(["The vault can't be reached."], ExplorerAddText.Failed(ItemMode.Pointer, "The vault can't be reached.").Lines);
    }

    [Fact]
    public void Unknown_category_names_still_count_the_files()
    {
        var message = ExplorerAddText.Build(ItemMode.Copy, new AddResult { Added = [Item(1, 1), Item(2, 77), Item(3, 77)] }, Name, includeSuccess: true)!;

        Assert.Equal(["1 file added to Images", "2 files added"], message.Lines);
    }
}

/// <summary>
/// An Explorer selection through <see cref="FileIntake"/> into a real library in a temp folder: one batch, auto-categorized,
/// in the entry's mode whatever the default add mode is, recorded as added by Explorer and so unread. The window isn't on
/// screen here, so the outcome goes to the toast reporter.
/// </summary>
public sealed class ExplorerIntakeTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Cabinette.App.Tests", "explorer-" + Guid.NewGuid().ToString("N"));
    private ILibraryService? _library;

    [Fact]
    public async Task A_pointer_selection_with_a_folder_is_one_auto_categorized_unread_batch()
    {
        var (library, intake) = await OpenAsync();
        var photo = Source("photo.jpg", "jpeg bytes");
        var notes = Source("notes.txt", "some text");
        Source(@"Holiday\beach.png", "png bytes");
        Source(@"Holiday\2024\hotel.pdf", "pdf bytes");
        var reporter = new RecordingReporter();

        await intake.AddFromExplorerAsync(new ExplorerDrop(ItemMode.Pointer, [photo, notes, Path.Combine(_root, "source", "Holiday")]), reporter);

        var (mode, result) = Assert.Single(reporter.Reports);
        Assert.Equal(ItemMode.Pointer, mode);
        Assert.Empty(result.Failures);
        Assert.Equal(4, result.Added.Count);
        Assert.All(result.Added, item =>
        {
            Assert.Equal(AddedBy.Explorer, item.AddedBy);
            Assert.Equal(ItemMode.Pointer, item.Mode);
        });
        Assert.Single(result.Added.Select(i => i.BatchId).Distinct());

        var categories = await library.GetAllCategoriesAsync();
        var images = categories.Single(c => c.BuiltInKey == ExtensionMap.ImagesKey);
        var holiday = categories.Single(c => c.Name == "Holiday" && c.ParentId is null);
        Assert.Contains(categories, c => c.Name == "2024" && c.ParentId == holiday.Id); // subfolders become subcategories (default)
        Assert.Equal(images.Id, result.Added.Single(i => i.FileName == "photo.jpg").HomeCategoryId);
        Assert.Equal(1, (await library.GetCategorySummaryAsync(images.Id))!.UnreadCount);
        Assert.Equal(2, (await library.GetCategorySummaryAsync(holiday.Id))!.UnreadCount);
    }

    [Fact]
    public async Task Copy_to_cabinette_copies_even_when_the_default_add_mode_is_pointer()
    {
        var (library, intake) = await OpenAsync();
        await library.SaveSettingsAsync(await library.GetSettingsAsync() with { DefaultAddMode = ItemMode.Pointer });
        var file = Source("report.pdf", "pdf bytes");
        var reporter = new RecordingReporter();

        await intake.AddFromExplorerAsync(new ExplorerDrop(ItemMode.Copy, [file]), reporter);

        var item = Assert.Single(Assert.Single(reporter.Reports).Result.Added);
        Assert.Equal(ItemMode.Copy, item.Mode);
        Assert.StartsWith(Path.Combine(_root, "vault"), item.FullPath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(file)); // the original stays
    }

    [Fact]
    public async Task A_batch_that_fails_as_a_whole_is_reported_as_a_failure()
    {
        var (library, intake) = await OpenAsync();
        await library.DisposeAsync(); // any library call now fails
        var reporter = new RecordingReporter();

        await intake.AddFromExplorerAsync(new ExplorerDrop(ItemMode.Copy, [Source("a.txt", "x")]), reporter);

        Assert.Empty(reporter.Reports);
        Assert.Single(reporter.Failures);
    }

    private async Task<(ILibraryService Library, FileIntake Intake)> OpenAsync()
    {
        Directory.CreateDirectory(_root);
        _library = await LibraryFactory.OpenAsync(new LibraryOptions { DataDirectory = Path.Combine(_root, "data") }, new NoRecycleBin());
        await _library.InitializeVaultAsync(Path.Combine(_root, "vault"));
        var directory = new CategoryDirectory(_library);
        var intake = new FileIntake(
            _library,
            new ClipboardReader(),
            new NotificationService(),
            new DialogService(_library, new CategoryIconService(_library, new FakeIconGenerator()), directory),
            new ShellNavigator(),
            directory);
        return (_library, intake);
    }

    private string Source(string relativePath, string content)
    {
        var path = Path.Combine(_root, "source", relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public async ValueTask DisposeAsync()
    {
        if (_library is not null)
        {
            try
            {
                await _library.DisposeAsync();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(100);
            }
        }
    }

    private sealed class RecordingReporter : IExplorerAddReporter
    {
        public List<(ItemMode Mode, AddResult Result)> Reports { get; } = [];

        public List<string> Failures { get; } = [];

        public bool ReportsOutsideWindow => true;

        public Task ReportAsync(ItemMode mode, AddResult result)
        {
            Reports.Add((mode, result));
            return Task.CompletedTask;
        }

        public void ReportFailure(ItemMode mode, string message) => Failures.Add(message);
    }

    private sealed class NoRecycleBin : Cabinette.Core.Abstractions.IRecycleBin
    {
        public bool CanRecycle(string path) => false;

        public void SendToRecycleBin(IReadOnlyCollection<string> paths) => throw new InvalidOperationException("Not used here.");
    }
}
