using DealRadar.Api.Data;
using DealRadar.Api.Models;
using DealRadar.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace DealRadar.Api.Workers;

public class PriceMonitorWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SmartPriceScraperService _scraper;
    private readonly ILogger<PriceMonitorWorker> _logger;
    private readonly TimeSpan _interval = TimeSpan.FromMinutes(10);

    public PriceMonitorWorker(
        IServiceScopeFactory scopeFactory,
        SmartPriceScraperService scraper,
        ILogger<PriceMonitorWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _scraper = scraper;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 1. Wait 15 seconds after API startup before the first run
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);

        // 2. Run immediately on startup
        await RunScanCycleAsync(stoppingToken);

        // 3. Repeat every 10 minutes without time drift using .NET 8 PeriodicTimer
        using var timer = new PeriodicTimer(_interval);
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunScanCycleAsync(stoppingToken);
        }
    }

    private async Task RunScanCycleAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting scheduled 10-minute price scan at {Time}", DateTime.Now);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var telegram = scope.ServiceProvider.GetRequiredService<TelegramNotificationService>();

            var activeLinks = await db.ProductLinks
                .Include(l => l.MasterProduct)
                .Where(l => l.MasterProduct != null && l.MasterProduct.IsActive)
                .ToListAsync(cancellationToken);

            if (activeLinks.Count == 0)
            {
                _logger.LogInformation("No active product links found to monitor.");
                return;
            }

            foreach (var link in activeLinks)
            {
                if (cancellationToken.IsCancellationRequested) break;

                try
                {
                    var result = await _scraper.FetchPriceAsync(link.WebsiteName, link.ProductUrl);
                    link.LastCheckedAt = DateTime.UtcNow;
                    link.IsInStock = result.IsInStock;

                    if (result.IsSuccess && result.IsInStock && result.EffectivePrice.HasValue && link.MasterProduct != null)
                    {
                        decimal effectivePrice = result.EffectivePrice.Value;
                        var margin = DealMarginCalculator.Evaluate(link.MasterProduct.TargetPrice, effectivePrice);

                        link.CurrentPrice = result.ListedPrice;
                        link.CouponDiscount = result.CouponDiscount;
                        link.MaxCardDiscount = result.MaxCardDiscount;
                        link.EffectivePrice = effectivePrice;
                        link.CardOfferSummary = string.Join(" | ",
                            result.CardOffers.Take(3).Select(c => $"{c.BankName}: -₹{c.DiscountAmount:N0}"));
                        link.MarginAmount = margin.MarginAmount;
                        link.MarginPercentage = margin.MarginPercentage;
                        link.DealTag = margin.DealTag;

                        // Explicitly set RecordedAt to prevent SQL datetime overflow
                        db.PriceHistories.Add(new PriceHistory
                        {
                            ProductLinkId = link.Id,
                            Price = effectivePrice,
                            RecordedAt = DateTime.UtcNow
                        });

                        if (margin.ShouldTriggerAlert)
                        {
                            bool isNewDrop = true; //!link.LastAlertedPrice.HasValue || effectivePrice < link.LastAlertedPrice.Value;
                            if (isNewDrop)
                            {
                                bool sent = await telegram.SendDealAlertAsync(link.MasterProduct, link, result, margin);
                                if (sent)
                                {
                                    link.LastAlertedPrice = effectivePrice;
                                }
                            }
                        }
                        else
                        {
                            link.LastAlertedPrice = null;
                        }
                    }

                    await db.SaveChangesAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to process link ID {LinkId} ({Url})", link.Id, link.ProductUrl);
                }

                // Random delay (4 to 8 seconds) between links to avoid bot IP blocks
                await Task.Delay(Random.Shared.Next(4000, 8000), cancellationToken);
            }

            _logger.LogInformation("Completed 10-minute price scan at {Time}", DateTime.Now);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error in PriceMonitorWorker cycle.");
        }
    }
}