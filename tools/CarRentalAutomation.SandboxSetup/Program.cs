namespace CarRentalAutomation.SandboxSetup;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0] == "--configure")
            {
                if (SandboxConfiguration.HasEnvironmentCredentials)
                {
                    Console.WriteLine("Server environment credentials are set. Use --check to check that pair. To configure User Secrets instead, unset BOTH IYZICO_SANDBOX_API_KEY and IYZICO_SANDBOX_SECRET_KEY and reopen this tool. Values have not been printed.");
                    return 2;
                }
                ApplicationConfiguration.Initialize();
                using var dialog = new CredentialDialog();
                Application.Run(dialog);
                return dialog.Verified ? 0 : 2;
            }
            if (args.Length == 1 && args[0] == "--check")
                return CheckAsync().GetAwaiter().GetResult(); // Console path only: there is no WinForms message loop here.

            Console.WriteLine("Stage 1 Sandbox setup: use --configure for local masked key entry, or --check to validate the saved server configuration. Never pass key values as command arguments.");
            return 2;
        }
        catch
        {
            Console.WriteLine("The Sandbox setup tool could not finish. Check that the .NET SDK is installed and your local User Secrets are readable. Secret values and exception details have not been logged.");
            return 2;
        }
    }

    private static async Task<int> CheckAsync()
    {
        var configuration = SandboxConfiguration.Load();
        Console.WriteLine(configuration.Message);
        Console.WriteLine("Configuration source: " + configuration.Source + "; endpoint: " + SandboxConfiguration.BaseUrl);
        if (!configuration.IsValid) return 2;
        using var client = SandboxAuthenticationProbe.CreateClient();
        var result = await new SandboxAuthenticationProbe(client).CheckAsync(configuration.Credentials!);
        PrintResult(result);
        return result.Succeeded ? 0 : 3;
    }

    internal static void PrintResult(ProbeResult result)
    {
        Console.WriteLine(result.Message);
        Console.WriteLine("Diagnostic reference: " + result.Reference);
        Console.Out.Flush();
    }
}
