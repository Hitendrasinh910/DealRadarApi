using DealRadar.Api.Data;
using DealRadar.Api.Models;
using DealRadar.Api.Services;
using DealRadar.Api.Workers;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// 1. Register SQL Server DbContext
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

// 2. Register Scraper (Singleton), HttpClient, and Telegram Service
builder.Services.AddSingleton<SmartPriceScraperService>();
builder.Services.AddHttpClient();
builder.Services.AddHttpClient<TelegramNotificationService>();

// 3. ONLY run Background Worker locally if explicitly enabled (saves MonsterASP 256MB RAM!)
if (builder.Configuration.GetValue<bool>("RunBackgroundWorker") && !args.Contains("--cron-scan"))
{
    builder.Services.AddHostedService<PriceMonitorWorker>();
}

// 4. Enable CORS for BOTH localhost AND your live Vercel mobile/desktop URLs
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod());
});

builder.Services.AddMemoryCache();

var app = builder.Build();

// =========================================================================
// MODE 1: GITHUB ACTIONS 10-MINUTE CRON RUNNER (Runs once and exits cleanly)
// =========================================================================
if (args.Contains("--cron-scan"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var scraper = scope.ServiceProvider.GetRequiredService<SmartPriceScraperService>();
    var telegram = scope.ServiceProvider.GetRequiredService<TelegramNotificationService>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    logger.LogInformation("GitHub Actions Cron Scan started at {Time}", DateTime.UtcNow);

    var activeLinks = await db.ProductLinks
        .Include(l => l.MasterProduct)
        .Where(l => l.MasterProduct != null && l.MasterProduct.IsActive)
        .ToListAsync();

    foreach (var link in activeLinks)
    {
        try
        {
            var result = await scraper.FetchPriceAsync(link.WebsiteName, link.ProductUrl);
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

                db.PriceHistories.Add(new PriceHistory
                {
                    ProductLinkId = link.Id,
                    Price = effectivePrice,
                    RecordedAt = DateTime.UtcNow
                });

                if (margin.ShouldTriggerAlert)
                {
                    bool isNewDrop = !link.LastAlertedPrice.HasValue || effectivePrice < link.LastAlertedPrice.Value;
                    if (isNewDrop)
                    {
                        bool sent = await telegram.SendDealAlertAsync(link.MasterProduct, link, result, margin);
                        if (sent) link.LastAlertedPrice = effectivePrice;
                    }
                }
                else
                {
                    link.LastAlertedPrice = null;
                }
            }

            await db.SaveChangesAsync();
            await Task.Delay(Random.Shared.Next(2000, 4000));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error scraping Link ID {Id}", link.Id);
        }
    }

    logger.LogInformation("GitHub Actions Cron Scan completed. Exiting cleanly.");
    Environment.Exit(0);
}

// =========================================================================
// MODE 2: NORMAL WEB API SERVER (MonsterASP.NET)
// =========================================================================
app.UseSwagger();
app.UseSwaggerUI();

app.UseHttpsRedirection();
app.UseCors("AllowAll");
app.UseAuthorization();
app.MapControllers();

app.Run();
