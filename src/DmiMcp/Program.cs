// Env: PORT (default 3000), MCP_PATH (default /mcp; set a hard-to-guess path when public).
using DmiMcp;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://0.0.0.0:{Environment.GetEnvironmentVariable("PORT") ?? "3000"}");

builder.Services.AddSingleton(new HttpClient
{
    // Forecast point queries regularly take 20+ seconds.
    Timeout = TimeSpan.FromSeconds(90),
    DefaultRequestHeaders = { { "User-Agent", "dmi-mcp/1.0" } },
});
builder.Services.AddSingleton<DmiClient>();
builder.Services
    .AddMcpServer()
    .WithHttpTransport(o => o.Stateless = true)
    .WithToolsFromAssembly();

var app = builder.Build();
app.MapGet("/health", () => "ok");
app.MapMcp(Environment.GetEnvironmentVariable("MCP_PATH") ?? "/mcp");
app.Run();
