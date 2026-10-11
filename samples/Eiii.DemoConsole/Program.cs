namespace Eiii.DemoConsole;

internal static class Program
{
    private static async Task Main()
    {
        await using var system = new DemoSystem(new DemoOptions());
        system.Start();
        await new ConsoleUi(system).RunAsync();
    }
}
