

using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using PuppeteerSharp;

class Program
{
    private static readonly int port = 2207;
    private static readonly string host = Environment.MachineName;
    private static readonly string app = "LNAB";
    private static IBrowser? _browser;
    private static IPage? _page;
    private static string? _conn;

    // Wakes the processing loop immediately when a remote request arrives
    private static readonly SemaphoreSlim _kick = new(0, int.MaxValue);


    public static void SignalKick()
    {
        try { _kick.Release(); } catch { /* ignore */ }
    }
    // Listener URL used by external callers to reach this app
    private static string ListenerUrl => $"net.tcp://{host}:{port}/LNAB";




    // Make sure you have: using CoreWCF.NetTcp;

    // Needed at top of file:


    private static async Task StartNetTcpHostAsync(int listenPort)
    {
        var webHost = new WebHostBuilder()
            .UseKestrel()
            .UseNetTcp(listenPort) // ✅ on IWebHostBuilder (correct place)
            .ConfigureServices(services =>
            {
                services.AddServiceModelServices();
                services.AddServiceModelMetadata();

                // Register the concrete service, not the interface
                services.AddSingleton<Service1>();
            })
            .Configure(app =>
            {
                app.UseServiceModel(s =>
                {
                    s.AddService<Service1>();

                    var binding = new CoreWCF.NetTcpBinding(CoreWCF.SecurityMode.None);
                    var baseAddress = new Uri($"net.tcp://0.0.0.0:{listenPort}/LNQC"); // or /LNAB if that’s your path
                    s.AddServiceEndpoint<Service1, IService1>(binding, baseAddress);
                });

                // ❌ REMOVE: app.UseNetTcp(listenPort);
            })
            .Build();

        Console.WriteLine($"[CoreWCF] Listening at net.tcp://0.0.0.0:{listenPort}/LNQC");
        await webHost.RunAsync();
    }

    private static async Task RestartAsync()
    {
        Console.WriteLine("[RESTART] Closing browser and relaunching…");
        try { if (_page is not null && !_page.IsClosed) await _page.CloseAsync(); } catch { }
        try { if (_browser is not null) await _browser.CloseAsync(); } catch { }
        try { if (!string.IsNullOrEmpty(_conn)) await InActivateAsync(_conn!); } catch { }

        var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
        if (!string.IsNullOrEmpty(exe))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = exe, UseShellExecute = true });

        Environment.Exit(0);
    }


    public static void RestartApp()
    {
        _ = RestartAsync();
    }

    private static async Task ActivateAsync(string connStr)
    {
        try
        {
            await using var cn = new SqlConnection(connStr);
            await cn.OpenAsync();
            await using var cmd = new SqlCommand(@"
IF NOT EXISTS (SELECT 1 FROM dbo.RegisteredApps WHERE Host = @h AND Port = @p AND App = @a)
    INSERT INTO dbo.RegisteredApps (Host, Port, App) VALUES (@h, @p, @a);", cn);
            cmd.Parameters.AddWithValue("@h", host);
            cmd.Parameters.AddWithValue("@p", port);
            cmd.Parameters.AddWithValue("@a", app);
            await cmd.ExecuteNonQueryAsync();
            Console.WriteLine($"[ACTIVATE] Registered {host}:{port}/{app}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ACTIVATE-ERROR] {ex.Message}");
            throw;
        }
    }

    private static async Task InActivateAsync(string connStr)
    {
        try
        {
            await using var cn = new SqlConnection(connStr);
            await cn.OpenAsync();
            await using var cmd = new SqlCommand(@"
DELETE FROM dbo.RegisteredApps WHERE Host = @h AND Port = @p AND App = @a;", cn);
            cmd.Parameters.AddWithValue("@h", host);
            cmd.Parameters.AddWithValue("@p", port);
            cmd.Parameters.AddWithValue("@a", app);
            await cmd.ExecuteNonQueryAsync();
            Console.WriteLine($"[INACTIVATE] Deregistered {host}:{port}/{app}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[INACTIVATE-ERROR] {ex.Message}");
        }
    }
}
