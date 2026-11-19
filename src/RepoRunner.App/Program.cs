using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using RepoRunner.App;
using RepoRunner.App.Components;
using RepoRunner.Core;

// RepoRunner's window is a page served to your own browser from this process.
// It listens on loopback only, and every request must carry the token issued at this launch.

var noBrowser = args.Contains("--no-browser");
var sessionFile = Path.Combine(Vault.DefaultHome, "ui.session");

// Already running? Open that instance instead of starting a second one.
if (await FindRunningInstance(sessionFile) is { } running)
{
    Console.WriteLine($"RepoRunner is already running: {running}");
    if (!noBrowser) OpenBrowser(running);
    return;
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddSingleton<VaultService>();
builder.Services.AddSingleton<SessionManager>();
builder.Services.AddSingleton<ProcessStore>();
builder.Services.AddSingleton<Resender>();

var app = builder.Build();
var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
var tokenBytes = Encoding.ASCII.GetBytes(token);

app.Use(async (context, next) =>
{
    // Loopback names only: stops a web page elsewhere from reaching this server through DNS tricks.
    if (context.Request.Host.Host is not ("127.0.0.1" or "localhost"))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    bool Matches(string? candidate) =>
        candidate is not null && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(candidate), tokenBytes);

    if (Matches(context.Request.Query["token"]))
    {
        context.Response.Cookies.Append("rr_ui", token, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/" });
        context.Response.Redirect(context.Request.Path); // drop the token from the address bar
        return;
    }
    if (!Matches(context.Request.Cookies["rr_ui"]))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "text/plain";
        await context.Response.WriteAsync("This page belongs to RepoRunner. Open it from the RepoRunner shortcut.");
        return;
    }
    await next();
});

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Lifetime.ApplicationStopping.Register(() =>
{
    app.Services.GetRequiredService<SessionManager>().StopAll();
    try { File.Delete(sessionFile); } catch (IOException) { }
});

await app.StartAsync();
var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
var url = $"{address}/?token={token}";

// Lets a second launch find this one. Protected for the current Windows user, like the vault key.
Directory.CreateDirectory(Vault.DefaultHome);
File.WriteAllBytes(sessionFile, KeyProtector.Wrap(Encoding.UTF8.GetBytes(url)));

Console.WriteLine("RepoRunner is running. Keep this window open; closing it stops RepoRunner and everything it started.");
Console.WriteLine($"Open: {url}");
if (!noBrowser) OpenBrowser(url);
await app.WaitForShutdownAsync();

static void OpenBrowser(string url)
{
    try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
    catch (Exception ex) { Console.WriteLine($"Could not open the browser: {ex.Message}"); }
}

static async Task<string?> FindRunningInstance(string sessionFile)
{
    if (!File.Exists(sessionFile)) return null;
    try
    {
        var url = Encoding.UTF8.GetString(KeyProtector.Unwrap(File.ReadAllBytes(sessionFile)));
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(2) };
        using var response = await http.GetAsync(url);
        return (int)response.StatusCode == 302 ? url : null;
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or CryptographicException or IOException)
    {
        return null;
    }
}
