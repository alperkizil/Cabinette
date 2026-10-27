using System.IO;
using Cabinette.App.Services.Ai;
using Cabinette.App.Services.Instance;
using Cabinette.Ipc;

namespace Cabinette.App.Tests;

/// <summary>
/// Round trips of the "ai" command through a real local channel: the instance pipe server (a private, uniquely named pipe,
/// current user only), the dispatcher with the "ai" gate registered as App does, the handler and a real library. No UI.
/// </summary>
public sealed class AiCommandPipeTests : AiCommandTestBase
{
    [Fact]
    public async Task A_tool_call_goes_over_the_pipe_and_its_result_comes_back()
    {
        var notes = await BuiltInAsync("notes");
        var gate = new AiCommandGate();
        var dispatcher = new InstanceMessageDispatcher();
        dispatcher.Register(InstanceCommands.Ai, gate.HandleAsync);
        var name = "Cabinette-test-" + Guid.NewGuid().ToString("N");
        await using var server = new InstancePipeServer(name, dispatcher.HandleLineAsync);
        server.Start();

        // Before the app is ready.
        var early = await InstancePipeClient.SendAsync(name, InstanceMessage.Ai(Client, AiTools.ListTags, new ListTagsArgs()), TimeSpan.FromSeconds(10));
        Assert.Equal("Cabinette is starting. Try again in a moment.", Failed(early!, AiErrorCodes.NotReady));

        gate.Open(Handler);
        var created = await InstancePipeClient.SendAsync(name, InstanceMessage.Ai(Client, AiTools.CreateNote, new CreateNoteArgs("over the pipe", notes.Id)), TimeSpan.FromSeconds(10));
        var note = Ok<FileResult>(created!).File;
        var listed = await InstancePipeClient.SendAsync(name, InstanceMessage.Ai(Client, AiTools.ListFiles, new ListFilesArgs(notes.Id)), TimeSpan.FromSeconds(10));
        var otherVersion = await InstancePipeClient.SendAsync(name, InstanceMessage.Ai(Client, AiTools.ListTags, new ListTagsArgs()) with { Version = AiProtocol.Version + 1 }, TimeSpan.FromSeconds(10));

        Assert.Equal("over the pipe", await File.ReadAllTextAsync(note.Path));
        Assert.Equal(note.Id, Assert.Single(Ok<ListFilesResult>(listed!).Files).Id);
        Failed(otherVersion!, AiErrorCodes.Unsupported);
        Assert.Equal([AiTools.CreateNote, AiTools.ListFiles, AiTools.ListTags], Log.Entries.Select(e => e.Tool));
        Assert.Equal(AiChangeKind.NoteCreated, Assert.Single(Changes.Reported).Kind);
    }
}
