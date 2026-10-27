using System.Runtime.CompilerServices;
using Cabinette.App.Services;
using Cabinette.App.Services.Ai;

namespace Cabinette.App;

/// <summary>
/// The entry point (instead of the one WPF generates from App.xaml). The hidden text-extraction child
/// (<c>--extract-text</c>, see <see cref="TextExtractionChild"/>) is decided here, before WPF, single instance, logging
/// or any window exist: IFilters then run in a process with nothing else loaded, and a malformed child command line
/// can never turn into a normal start.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Any(CommandLineOptions.IsTextExtractionArgument))
        {
            return TextExtractionChild.Run(args);
        }

        return RunApp();
    }

    /// <summary>The normal start. Kept out of <see cref="Main"/> so that the child never loads WPF.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunApp()
    {
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
