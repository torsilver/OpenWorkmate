using AccurateDataMcp;
using AccurateDataMcp.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables();

builder.Services.AddOptions<AccurateDataOptions>()
    .Bind(builder.Configuration.GetSection(AccurateDataOptions.SectionName))
    .PostConfigure(opts =>
    {
        var fromEnv = Environment.GetEnvironmentVariable("ACCURATE_DATA_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(fromEnv))
            opts.Directory = fromEnv;
        if (string.IsNullOrWhiteSpace(opts.Directory))
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            opts.Directory = Path.Combine(appData, "OfficeCopilot", "AccurateData");
        }
    });

builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<AccurateDataTools>();

await builder.Build().RunAsync();
