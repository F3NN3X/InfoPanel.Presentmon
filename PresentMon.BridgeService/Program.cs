using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PresentMon.BridgeService;
using PresentMon.BridgeService.Logging;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.SetMinimumLevel(LogLevel.Debug);
builder.Logging.AddEventLog();

var logFilePath = Path.Combine(AppContext.BaseDirectory, "PresentMonBridgeService.log");
builder.Logging.AddProvider(new FileLoggerProvider(logFilePath, LogLevel.Debug));
builder.Services.AddWindowsService(options => options.ServiceName = "PresentMonBridgeService");
builder.Services.AddSingleton<PresentMonSessionManager>();
builder.Services.AddHostedService<BridgeWorker>();

await builder.Build().RunAsync();
