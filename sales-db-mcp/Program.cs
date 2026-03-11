using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SalesDbMcp;
using SalesDbMcp.Tools;

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables();

// Prefer SALES_DB_CONNECTION_STRING env var over appsettings for connection string.
builder.Services.AddOptions<SalesDbOptions>()
    .Bind(builder.Configuration.GetSection(SalesDbOptions.SectionName))
    .PostConfigure(opts =>
    {
        var fromEnv = Environment.GetEnvironmentVariable("SALES_DB_CONNECTION_STRING");
        if (!string.IsNullOrWhiteSpace(fromEnv))
            opts.ConnectionString = fromEnv;
    });

// Configure all logs to go to stderr (stdout is used for the MCP protocol messages).
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

// Add the MCP services: the transport to use (stdio) and the tools to register.
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<SalesDbTools>();

await builder.Build().RunAsync();
