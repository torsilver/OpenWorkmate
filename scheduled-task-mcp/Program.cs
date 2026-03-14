using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ScheduledTaskMcp;
using ScheduledTaskMcp.Tools;

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables();

builder.Services.AddOptions<ScheduledTaskOptions>()
    .Bind(builder.Configuration.GetSection(ScheduledTaskOptions.SectionName))
    .PostConfigure(opts =>
    {
        var fromEnv = Environment.GetEnvironmentVariable("SCHEDULED_TASKS_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(fromEnv))
            opts.Directory = fromEnv;
        if (string.IsNullOrWhiteSpace(opts.Directory))
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            opts.Directory = Path.Combine(appData, "OfficeCopilot", "ScheduledTasks");
        }
    });

builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<ScheduledTaskTools>();

await builder.Build().RunAsync();
