using Cabinette.Mcp;

// A harmless console child for cabinette-mcp's launcher tests:
//   sleep <seconds>        waits, then exits.
//   start <exe> [args...]  waits for one line on stdin (the test puts this process into a job first), starts <exe>
//                          through cabinette-mcp's DetachedProcessStarter, prints "<pid> <placement>" and exits.
if (args is ["sleep", var seconds])
{
    Thread.Sleep(TimeSpan.FromSeconds(int.Parse(seconds, System.Globalization.CultureInfo.InvariantCulture)));
    return 0;
}

if (args is ["start", var exe, .. var rest])
{
    Console.In.ReadLine();
    var starter = new DetachedProcessStarter();
    var pid = starter.Start(exe, rest, AppContext.BaseDirectory);
    Console.Out.WriteLine($"{pid} {starter.LastPlacement}");
    return 0;
}

Console.Error.WriteLine("usage: sleep <seconds> | start <exe> [args...]");
return 2;
