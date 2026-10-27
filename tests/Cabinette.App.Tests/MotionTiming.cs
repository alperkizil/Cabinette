namespace Cabinette.App.Tests;

/// <summary>
/// Tests that let real WPF animation clocks run and check them within short windows (60–430 ms). They pass alone, but with
/// the whole suite running in parallel on a busy machine a window could be missed, so they never run in parallel with
/// other tests.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MotionTiming
{
    public const string Name = "Motion timing";
}
