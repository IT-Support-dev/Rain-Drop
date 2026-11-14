using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using RainDrop.AgentService;

// ชื่อ Service: RainDropAgentSvc
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "RainDropAgentSvc");
builder.Services.AddHostedService<Worker>();

// เขียน log ลง Windows Event Log (ดูได้ใน Event Viewer > Application)
builder.Logging.AddEventLog(settings => settings.SourceName = "Rain Drop Agent Service");

var host = builder.Build();
host.Run();
