using System.Security.Principal;
using Cabinette.App.Services.Instance;
using Cabinette.Ipc;

namespace Cabinette.App.Tests;

public class InstanceNamesTests
{
    private const string Sid = "S-1-5-21-1111111111-2222222222-3333333333-1001";

    [Fact]
    public void Names_contain_the_user_sid_and_an_8_digit_hash_of_the_data_folder()
    {
        var identity = InstanceNames.For(Sid, @"C:\Users\you\AppData\Local\Cabinette");

        Assert.Matches($"^Cabinette-{Sid}-[0-9a-f]{{8}}$", identity.BaseName);
        Assert.Equal(@"Local\" + identity.BaseName, identity.MutexName);
        Assert.Equal(identity.BaseName, identity.PipeName);
        Assert.DoesNotContain('\\', identity.PipeName);
    }

    [Theory]
    [InlineData(@"C:\Users\you\AppData\Local\Cabinette\")]
    [InlineData(@"c:\users\YOU\appdata\local\cabinette")]
    [InlineData("C:/Users/you/AppData/Local/Cabinette")]
    [InlineData(@" ""C:\Users\you\AppData\Local\Cabinette"" ")]
    [InlineData(@"C:\Users\you\AppData\Local\Temp\..\Cabinette")]
    public void Spellings_of_the_same_folder_give_the_same_names(string spelling)
    {
        Assert.Equal(
            InstanceNames.For(Sid, @"C:\Users\you\AppData\Local\Cabinette"),
            InstanceNames.For(Sid, spelling));
    }

    [Fact]
    public void A_dev_data_folder_never_collides_with_the_real_one()
    {
        var real = InstanceNames.For(Sid, @"C:\Users\you\AppData\Local\Cabinette");
        var dev = InstanceNames.For(Sid, @"C:\Temp\cabinette-dev");

        Assert.NotEqual(real.MutexName, dev.MutexName);
        Assert.NotEqual(real.PipeName, dev.PipeName);
    }

    [Fact]
    public void Other_users_get_other_names()
    {
        Assert.NotEqual(
            InstanceNames.For(Sid, @"D:\Data").BaseName,
            InstanceNames.For("S-1-5-21-1111111111-2222222222-3333333333-1002", @"D:\Data").BaseName);
    }

    [Fact]
    public void The_current_user_sid_is_used()
    {
        using var current = WindowsIdentity.GetCurrent();

        Assert.Contains(current.User!.Value, InstanceNames.ForCurrentUser(@"C:\Temp\x").BaseName);
    }

    [Fact]
    public void Hash8_is_stable()
    {
        Assert.Equal(InstanceNames.Hash8("ABC"), InstanceNames.Hash8("ABC"));
        // SHA-256("abc") = ba7816bf 8f01cfea ...
        Assert.Equal("ba7816bf", InstanceNames.Hash8("abc"));
    }
}

/// <summary>Talks to a real named pipe (a private, uniquely named one, current user only). No UI, no app state.</summary>
public class InstancePipeTests
{
    [Fact]
    public async Task A_second_instance_can_activate_the_first()
    {
        var name = "Cabinette-test-" + Guid.NewGuid().ToString("N");
        var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = new InstanceMessageDispatcher();
        dispatcher.Register(InstanceCommands.Activate, _ =>
        {
            activated.TrySetResult();
            return Task.FromResult(InstanceReply.Success);
        });

        await using var server = new InstancePipeServer(name, dispatcher.HandleLineAsync);
        server.Start();

        var reply = await InstancePipeClient.SendAsync(name, InstanceMessage.Activate(), TimeSpan.FromSeconds(10));

        Assert.NotNull(reply);
        Assert.True(reply.Ok);
        await activated.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Unknown_commands_are_answered_and_the_server_keeps_serving()
    {
        var name = "Cabinette-test-" + Guid.NewGuid().ToString("N");
        var dispatcher = new InstanceMessageDispatcher();
        dispatcher.Register(InstanceCommands.Activate, _ => Task.FromResult(InstanceReply.Success));
        await using var server = new InstancePipeServer(name, dispatcher.HandleLineAsync);
        server.Start();

        var unknown = await InstancePipeClient.SendAsync(name, new InstanceMessage { Command = "add", Mode = "copy", Paths = [@"C:\a.txt"] }, TimeSpan.FromSeconds(10));
        var activate = await InstancePipeClient.SendAsync(name, InstanceMessage.Activate(), TimeSpan.FromSeconds(10));

        Assert.False(unknown!.Ok);
        Assert.True(activate!.Ok);
    }

    [Fact]
    public async Task Without_a_running_instance_the_client_gives_up_quietly()
    {
        var reply = await InstancePipeClient.SendAsync("Cabinette-test-" + Guid.NewGuid().ToString("N"), InstanceMessage.Activate(), TimeSpan.FromMilliseconds(300));

        Assert.Null(reply);
    }

    [Fact]
    public void Only_one_guard_per_mutex_name()
    {
        var name = @"Local\Cabinette-test-" + Guid.NewGuid().ToString("N");
        using var first = SingleInstanceGuard.TryAcquire(name);
        Assert.NotNull(first);

        // A second process would fail; on another thread of this process the mutex is not ours either.
        SingleInstanceGuard? second = null;
        var thread = new Thread(() => second = SingleInstanceGuard.TryAcquire(name));
        thread.Start();
        thread.Join();
        Assert.Null(second);

        first.Release();
        SingleInstanceGuard? third = null;
        var again = new Thread(() =>
        {
            third = SingleInstanceGuard.TryAcquire(name);
            third?.Release();
        });
        again.Start();
        again.Join();
        Assert.NotNull(third);
    }
}
