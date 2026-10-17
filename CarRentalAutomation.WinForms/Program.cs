namespace CarRentalAutomation.WinForms;

using CarRentalAutomation.Core;
using CarRentalAutomation.Infrastructure;
using Microsoft.Extensions.Configuration;

static class Program
{
    /// <summary>
    ///  The main entry point for the application.
    /// </summary>
    [STAThread]
    static void Main()
    {
        // To customize application configuration such as set high DPI settings or default font,
        // see https://aka.ms/applicationconfiguration.
        ApplicationConfiguration.Initialize();
        try
        {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables(prefix: "CARRENTAL_")
            .Build();
        var connectionString = configuration.GetConnectionString("CarRental");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            MessageBox.Show("MySQL connection string is missing. Copy appsettings.example.json to CarRentalAutomation.WinForms/appsettings.json and update it.",
                "Database configuration", MessageBoxButtons.OK, MessageBoxIcon.Information);
            Application.Exit();
            return;
        }
        var userId = long.TryParse(configuration["Application:DemoUserId"], out var configuredUserId) ? configuredUserId : 1L;
        var database = new MySqlDatabase(connectionString);
        ICollectionRepository collections = new MySqlCollectionRepository(database, userId);
        ISiteCredentialRepository siteCredentials = new MySqlSiteCredentialRepository(database);
        var paymentAddress = configuration["Payments:ApiUrl"] ?? "http://127.0.0.1:5158";
        using var payments = new PaymentApiClient(paymentAddress);
        var attempts = new FileRentalAttemptStore(paymentAddress, userId);
        Application.Run(new MainForm(collections, database, siteCredentials, userId, payments, attempts));
        }
        catch (Exception ex)
        {
            var logPath = Path.Combine(AppContext.BaseDirectory, "startup-error.log");
            File.WriteAllText(logPath, ex.ToString());
            MessageBox.Show($"The app stopped during startup. Details: {logPath}\n\n{ex.Message}", "Startup error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }    
}
