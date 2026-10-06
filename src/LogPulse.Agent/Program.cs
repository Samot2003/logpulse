using LogPulse.Agent;
using LogPulse.Agent.Options;
using Microsoft.Extensions.Hosting.WindowsServices;

// A Windows service starts in System32: read appsettings.json and resolve relative paths from the install folder.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : null,
});

// No-op unless the process was started by the Windows Service Control Manager.
builder.Services.AddWindowsService(service => service.ServiceName = "LogPulseAgent");
builder.Services.AddLogPulseAgent(builder.Configuration);

var host = builder.Build();
host.EnsureValidAgentConfiguration();
await host.RunAsync();
