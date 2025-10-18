using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PresentMon.BridgeService;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "PresentMonBridgeService");
builder.Services.AddSingleton<PresentMonSessionManager>();
builder.Services.AddHostedService<BridgeWorker>();

await builder.Build().RunAsync();
