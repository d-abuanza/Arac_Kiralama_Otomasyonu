using System.Diagnostics;
using System.Text.Json;
using CarRentalAutomation.WinForms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

internal static class Program
{
    private const string Fixture = """
        <!doctype html><html><head><title>Local host regression fixture</title></head>
        <body><h1>Local browser host check</h1><p>This local page does not contact Yolcu360.</p>
        <input id="fixture-input"><button id="fixture-button">Local fixture button</button>
        <script>
        window.fixtureEvents = 0; window.fixtureTicks = 0;
        document.getElementById('fixture-input').addEventListener('input', () => window.fixtureEvents++);
        document.getElementById('fixture-button').addEventListener('click', () => window.fixtureEvents++);
        setInterval(() => window.fixtureTicks++, 50);
        </script></body></html>
        """;

    [STAThread]
    private static int Main()
    {
        ApplicationConfiguration.Initialize();
        var exitCode = 1;
        var tempRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "CarRentalBackgroundSessionChecks"));
        var profile = Path.GetFullPath(Path.Combine(tempRoot, Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(profile);
        using var owner = new Form
        {
            Text = "Local background host regression owner", StartPosition = FormStartPosition.Manual,
            Bounds = new Rectangle(SystemInformation.VirtualScreen.Left - 500, SystemInformation.VirtualScreen.Top, 400, 300),
            ShowInTaskbar = false
        };
        owner.Shown += async (_, _) =>
        {
            BackgroundBrowserHost? host = null;
            CoreWebView2Environment? environment = null;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            using var heartbeat = new System.Windows.Forms.Timer { Interval = 40 };
            var uiTicks = 0;
            var checks = 0;
            heartbeat.Tick += (_, _) => uiTicks++;
            heartbeat.Start();
            try
            {
                var view = new WebView2 { Name = "localFixtureWebView", Dock = DockStyle.Fill };
                host = new BackgroundBrowserHost(view);
                host.StartInBackground(owner);
                var hostHandle = host.Handle;
                Check(host.Visible && !host.Bounds.IntersectsWith(SystemInformation.VirtualScreen), "Background host must remain shown beyond the virtual desktop.");
                Check(!host.ShowInTaskbar && host.Owner == owner && host.WindowState == FormWindowState.Normal, "Background host must stay owned, normal and off the taskbar.");
                Check(view.Visible && view.Parent == host && Descendants(owner).OfType<WebView2>().Count() == 0, "Browser must have one background parent and no main-form child.");
                environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profile).WaitAsync(timeout.Token);
                await view.EnsureCoreWebView2Async(environment).WaitAsync(timeout.Token);
                var core = view.CoreWebView2;
                var viewHandle = view.Handle;
                var browserProcess = core.BrowserProcessId;
                var processFailures = 0;
                core.ProcessFailed += (_, _) => processFailures++;
                core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += (_, e) =>
                {
                    if (Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                        e.Response = environment.CreateWebResourceResponse(null, 403, "Local test blocks network", "Content-Type: text/plain");
                };
                var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void Completed(object? sender, CoreWebView2NavigationCompletedEventArgs e)
                {
                    if (e.IsSuccess) loaded.TrySetResult();
                    else loaded.TrySetException(new InvalidOperationException("Local fixture navigation failed: " + e.WebErrorStatus));
                }
                core.NavigationCompleted += Completed;
                try { core.NavigateToString(Fixture); await loaded.Task.WaitAsync(timeout.Token); }
                finally { core.NavigationCompleted -= Completed; }
                await core.ExecuteScriptAsync("window.fixtureRetained='synthetic-retained-state'; const input=document.getElementById('fixture-input'); input.value='synthetic-input'; input.dispatchEvent(new Event('input',{bubbles:true})); document.getElementById('fixture-button').click();").WaitAsync(timeout.Token);
                await Task.Delay(250, timeout.Token);
                var before = await SnapshotAsync(core, timeout.Token);
                Check(before.Ready && before.Interactable && before.Events == 2 && before.Ticks > 0, "DOM, hit testing, events and timers must work while the host is off screen.");
                Check(uiTicks >= 2, "The WinForms message loop must remain responsive during browser initialization.");

                var cancellations = 0;
                host.VerificationCancelled += () => cancellations++;
                host.ShowVerification();
                Check(host.IsShowingVerification && host.Visible && host.Bounds.IntersectsWith(SystemInformation.VirtualScreen), "Verification must bring the existing host into the desktop.");
                await Task.Delay(150, timeout.Token);
                Check(host.Handle == hostHandle && view.Handle == viewHandle && ReferenceEquals(view.CoreWebView2, core), "Showing verification must preserve the native handles and CoreWebView2 instance.");
                host.ReturnToBackground();
                Check(!host.IsShowingVerification && host.Visible && !host.Bounds.IntersectsWith(SystemInformation.VirtualScreen), "Returning must park the same shown host outside the desktop.");
                var after = await SnapshotAsync(core, timeout.Token);
                Check(after.Retained && after.InputRetained && after.Events == before.Events && after.Ticks > before.Ticks, "Browser document state must survive foreground and background transitions.");
                Check(view.CoreWebView2.BrowserProcessId == browserProcess && Descendants(host).OfType<WebView2>().Count() == 1 && !Descendants(owner).OfType<WebView2>().Any(), "Transitions must not create or reparent a second browser.");

                host.ShowVerification();
                host.Close();
                Check(cancellations == 1 && !host.IsDisposed && !view.IsDisposed && !host.IsShowingVerification, "User close must signal cancellation and retain the browser.");
                Check(!host.Bounds.IntersectsWith(SystemInformation.VirtualScreen) && (await SnapshotAsync(core, timeout.Token)).Retained, "Cancellation must park a still functioning browser.");
                Check(processFailures == 0, "No browser process may fail during host transitions.");
                host.CloseAfterShutdown();
                Check(host.IsDisposed && view.IsDisposed, "Final shutdown must dispose the host and its browser.");
                Console.WriteLine($"Passed {checks} real STA WebView2 background-host checks using a local synthetic page and isolated temporary profile. No Yolcu360, MySQL, SMS or CAPTCHA was accessed.");
                exitCode = 0;

                void Check(bool condition, string message)
                {
                    if (!condition) throw new InvalidOperationException(message);
                    checks++;
                }
            }
            catch (Exception ex) { Console.Error.WriteLine("Background-host check failed: " + ex.Message); }
            finally
            {
                heartbeat.Stop();
                host?.CloseAfterShutdown();
                // Only the uniquely generated test profile can be removed.
                if (!profile.StartsWith(tempRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Unexpected test profile path.");
                for (var attempt = 0; attempt < 10; attempt++)
                {
                    try { Directory.Delete(profile, recursive: true); break; }
                    catch (IOException) { await Task.Delay(150); }
                    catch (UnauthorizedAccessException) { await Task.Delay(150); }
                }
                if (Directory.Exists(profile)) Console.WriteLine("Temporary WebView profile is still locked and remains under the test TEMP directory.");
                owner.Close();
            }
        };
        Application.Run(owner);
        return exitCode;
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private sealed record Snapshot(bool Ready, bool Interactable, int Events, int Ticks, bool Retained, bool InputRetained);
    private static async Task<Snapshot> SnapshotAsync(CoreWebView2 core, CancellationToken token)
    {
        const string script = "(() => { const input=document.getElementById('fixture-input'),r=input.getBoundingClientRect(); return {ready:document.readyState==='complete',interactable:r.width>0&&r.height>0&&document.elementFromPoint(r.x+r.width/2,r.y+r.height/2)===input,events:window.fixtureEvents,ticks:window.fixtureTicks,retained:window.fixtureRetained==='synthetic-retained-state',inputRetained:input.value==='synthetic-input'}; })()";
        var json = await core.ExecuteScriptAsync(script).WaitAsync(token);
        using var parsed = JsonDocument.Parse(json);
        var state = parsed.RootElement;
        return new Snapshot(state.GetProperty("ready").GetBoolean(), state.GetProperty("interactable").GetBoolean(), state.GetProperty("events").GetInt32(), state.GetProperty("ticks").GetInt32(), state.GetProperty("retained").GetBoolean(), state.GetProperty("inputRetained").GetBoolean());
    }
}
