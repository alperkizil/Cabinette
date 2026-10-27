using System.IO;
using System.Text;
using Cabinette.App.Services.Instance;
using Cabinette.Ipc;

namespace Cabinette.App.Tests;

public class InstanceProtocolTests
{
    [Fact]
    public void Activate_serializes_to_one_compact_json_line()
    {
        Assert.Equal("""{"command":"activate"}""", InstanceProtocol.Serialize(InstanceMessage.Activate()));
    }

    [Fact]
    public void Add_message_round_trips_and_never_contains_a_raw_line_break()
    {
        var message = new InstanceMessage
        {
            Command = InstanceCommands.Add,
            Mode = "pointer",
            Paths = [@"C:\Users\you\Desktop\report ""final"".pdf", "D:\\odd\nname.txt", @"E:\Fotoğraflar\çay.jpg"],
        };

        var line = InstanceProtocol.Serialize(message);

        Assert.DoesNotContain('\n', line);
        Assert.True(InstanceProtocol.TryParseMessage(line, out var parsed, out _));
        Assert.Equal("add", parsed.Command);
        Assert.Equal("pointer", parsed.Mode);
        Assert.Equal(message.Paths, parsed.Paths);
    }

    [Fact]
    public void Commands_are_trimmed_and_lower_cased_and_unknown_fields_are_ignored()
    {
        Assert.True(InstanceProtocol.TryParseMessage("""{"Command":"  ACTIVATE ","extra":42,"nested":{"a":[1]}}""", out var message, out _));
        Assert.Equal(InstanceCommands.Activate, message.Command);
        Assert.Null(message.Paths);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("""{"command":""}""")]
    [InlineData("""{"mode":"copy"}""")]
    [InlineData("""["activate"]""")]
    [InlineData("""{"command":"add","paths":"C:\\one.txt"}""")]
    [InlineData("""{"command":17}""")]
    public void Malformed_lines_are_rejected(string line)
    {
        Assert.False(InstanceProtocol.TryParseMessage(line, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void Blank_paths_are_dropped()
    {
        Assert.True(InstanceProtocol.TryParseMessage("""{"command":"add","mode":"COPY","paths":["C:\\a.txt","",null,"  "]}""", out var message, out _));
        Assert.Equal("copy", message.Mode);
        Assert.Equal([@"C:\a.txt"], message.Paths);
    }

    [Fact]
    public void Replies_are_compact_and_parse_back()
    {
        Assert.Equal("""{"ok":true}""", InstanceProtocol.Serialize(InstanceReply.Success));
        var failed = InstanceProtocol.TryParseReply(InstanceProtocol.Serialize(InstanceReply.Failed("unknown command 'x'")));
        Assert.NotNull(failed);
        Assert.False(failed.Ok);
        Assert.Equal("unknown command 'x'", failed.Error);
        Assert.Null(InstanceProtocol.TryParseReply("garbage"));
        Assert.Null(InstanceProtocol.TryParseReply(null));
    }
}

public class InstanceDispatcherTests
{
    [Fact]
    public async Task Unknown_commands_get_an_error_reply_instead_of_throwing()
    {
        var dispatcher = new InstanceMessageDispatcher();

        var reply = await dispatcher.DispatchAsync(new InstanceMessage { Command = "format-c-drive" });

        Assert.False(reply.Ok);
        Assert.Contains("unknown command", reply.Error);
    }

    /// <summary>The running app registers "add" (ExplorerStartup.RegisterAddCommand); a bare dispatcher doesn't handle it.</summary>
    [Fact]
    public async Task Add_is_only_handled_once_registered()
    {
        var dispatcher = new InstanceMessageDispatcher();
        Assert.False(dispatcher.IsRegistered(InstanceCommands.Add));

        var line = await dispatcher.HandleLineAsync("""{"command":"add","mode":"copy","paths":["C:\\a.txt"]}""");

        Assert.False(InstanceProtocol.TryParseReply(line)!.Ok);
    }

    [Fact]
    public async Task Registered_handlers_receive_the_message()
    {
        var dispatcher = new InstanceMessageDispatcher();
        InstanceMessage? received = null;
        dispatcher.Register(InstanceCommands.Activate, message =>
        {
            received = message;
            return Task.FromResult(InstanceReply.Success);
        });

        var line = await dispatcher.HandleLineAsync("""{"command":"Activate"}""");

        Assert.True(InstanceProtocol.TryParseReply(line)!.Ok);
        Assert.Equal(InstanceCommands.Activate, received?.Command);
    }

    [Fact]
    public async Task A_failing_handler_becomes_an_error_reply()
    {
        var dispatcher = new InstanceMessageDispatcher();
        dispatcher.Register(InstanceCommands.Activate, _ => throw new InvalidOperationException("boom"));

        var reply = await dispatcher.DispatchAsync(InstanceMessage.Activate());

        Assert.False(reply.Ok);
        Assert.DoesNotContain("boom", reply.Error);
    }

    [Fact]
    public async Task Malformed_lines_get_an_error_reply()
    {
        var dispatcher = new InstanceMessageDispatcher();

        var reply = InstanceProtocol.TryParseReply(await dispatcher.HandleLineAsync("{oops"));

        Assert.NotNull(reply);
        Assert.False(reply.Ok);
    }
}

public class LineReaderTests
{
    [Fact]
    public async Task Splits_lines_across_reads_and_trims_carriage_returns()
    {
        var reader = new LineReader(new ChunkedStream(Encoding.UTF8.GetBytes("first\r\nsecond\nthird"), chunk: 3), 1024);

        Assert.Equal("first", await reader.ReadLineAsync(default));
        Assert.Equal("second", await reader.ReadLineAsync(default));
        Assert.Equal("third", await reader.ReadLineAsync(default));
        Assert.Null(await reader.ReadLineAsync(default));
    }

    [Fact]
    public async Task Multi_byte_characters_split_between_reads_survive()
    {
        var reader = new LineReader(new ChunkedStream(Encoding.UTF8.GetBytes("Fotoğraf çay ☕\n"), chunk: 1), 1024);

        Assert.Equal("Fotoğraf çay ☕", await reader.ReadLineAsync(default));
    }

    [Fact]
    public async Task Lines_over_the_limit_are_refused()
    {
        var reader = new LineReader(new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 5000) + "\n")), 100);

        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadLineAsync(default));
    }

    /// <summary>Returns at most <c>chunk</c> bytes per read, like a pipe delivering data in pieces.</summary>
    private sealed class ChunkedStream(byte[] data, int chunk) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, chunk));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(buffer.Length, chunk)], cancellationToken);
    }
}
