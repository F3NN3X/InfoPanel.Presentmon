using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PresentMon.BridgeService;

public sealed class BridgeWorker : BackgroundService
{
    private readonly ILogger<BridgeWorker> _logger;
    private readonly PresentMonSessionManager _sessionManager;

    public BridgeWorker(ILogger<BridgeWorker> logger, PresentMonSessionManager sessionManager)
    {
        _logger = logger;
        _sessionManager = sessionManager;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("PresentMon bridge worker starting.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _sessionManager.AcceptAndProcessClientAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while processing client connection.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            }
        }

        _logger.LogInformation("PresentMon bridge worker stopping.");
    }
}
