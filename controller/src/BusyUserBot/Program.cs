namespace BusyUserBot;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        bool dryRun = args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase);
        bool fakeAi = args.Contains("--fake-ai", StringComparer.OrdinalIgnoreCase);

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(dryRun, fakeAi));
    }
}
