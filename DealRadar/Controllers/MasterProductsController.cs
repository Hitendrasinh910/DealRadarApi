using DealRadar.Api.Data;
using DealRadar.Api.Models;
using DealRadar.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DealRadar.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class MasterProductsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly SmartPriceScraperService _scraper;
    private readonly TelegramNotificationService _telegram;

    public MasterProductsController(
        AppDbContext db,
        SmartPriceScraperService scraper,
        TelegramNotificationService telegram)
    {
        _db = db;
        _scraper = scraper;
        _telegram = telegram;
    }

    // 1. GET: api/MasterProducts (Get all products with their website links)
    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var products = await _db.MasterProducts
            .Include(p => p.Links)
            .OrderByDescending(p => p.Id)
            .ToListAsync();

        return Ok(products);
    }

    // 2. POST: api/MasterProducts (Add Master Product + Website Links)
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateMasterProductDto dto)
    {
        // Inside Create([FromBody] CreateMasterProductDto dto):
        var product = new MasterProduct
        {
            ProductName = dto.ProductName,
            Category = dto.Category,
            TargetPrice = dto.TargetPrice,
            MovementType = string.IsNullOrWhiteSpace(dto.MovementType) ? "Fast Move" : dto.MovementType,
            IsActive = true
        };

        foreach (var linkDto in dto.Links)
        {
            var website = !string.IsNullOrWhiteSpace(linkDto.WebsiteName)
                ? linkDto.WebsiteName
                : DetectWebsite(linkDto.ProductUrl);

            product.Links.Add(new ProductLink
            {
                WebsiteName = website,
                ProductUrl = linkDto.ProductUrl
            });
        }

        _db.MasterProducts.Add(product);
        await _db.SaveChangesAsync();

        return Ok(product);
    }

    // 3. POST: api/MasterProducts/{id}/check-prices (Fetch live prices & alert if below TargetPrice)
    [HttpPost("{id:int}/check-prices")]
    public async Task<IActionResult> CheckProductPricesNow(int id)
    {
        var product = await _db.MasterProducts
            .Include(p => p.Links)
            .FirstOrDefaultAsync(p => p.Id == id);

        if (product == null)
            return NotFound(new { message = "MasterProduct not found" });

        var checkResults = new List<object>();

        foreach (var link in product.Links)
        {
            var res = await _scraper.FetchPriceAsync(link.WebsiteName, link.ProductUrl);
            bool alertSent = false;

            link.LastCheckedAt = DateTime.UtcNow;
            link.IsInStock = res.IsInStock;

            if (res.IsSuccess && res.IsInStock && res.EffectivePrice.HasValue)
            {
                decimal effectivePrice = res.EffectivePrice.Value;
                var margin = DealMarginCalculator.Evaluate(product.TargetPrice, effectivePrice);

                link.CurrentPrice = res.ListedPrice;
                link.CouponDiscount = res.CouponDiscount;
                link.MaxCardDiscount = res.MaxCardDiscount;
                link.EffectivePrice = effectivePrice;
                link.CardOfferSummary = string.Join(" | ", res.CardOffers.Take(3).Select(c => $"{c.BankName}: -₹{c.DiscountAmount:N0}"));

                // Save Margin & Tag
                link.MarginAmount = margin.MarginAmount;
                link.MarginPercentage = margin.MarginPercentage;
                link.DealTag = margin.DealTag;

                _db.PriceHistories.Add(new PriceHistory
                {
                    ProductLinkId = link.Id,
                    Price = effectivePrice
                });

                if (margin.ShouldTriggerAlert)
                {
                    alertSent = await _telegram.SendDealAlertAsync(product, link, res, margin);
                    if (alertSent) link.LastAlertedPrice = effectivePrice;
                }
            }

            checkResults.Add(new
            {
                website = link.WebsiteName,
                movementType = product.MovementType,
                listedPrice = res.ListedPrice,
                couponDiscount = res.CouponDiscount,
                maxCardDiscount = res.MaxCardDiscount,
                effectivePrice = res.EffectivePrice,
                targetPrice = product.TargetPrice,
                marginAmount = link.MarginAmount,
                marginPercentage = link.MarginPercentage,
                dealTag = link.DealTag,
                cardOffers = res.CardOffers,
                telegramSent = alertSent,
                error = res.ErrorMessage
            });
        }

        await _db.SaveChangesAsync();
        return Ok(new { product.ProductName, product.TargetPrice, results = checkResults });
    }

    // 4. DELETE: api/MasterProducts/{id}
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var product = await _db.MasterProducts.FindAsync(id);
        if (product == null) return NotFound();

        _db.MasterProducts.Remove(product);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id)
    {
        var product = await _db.MasterProducts
            .Include(p => p.Links)
            .FirstOrDefaultAsync(p => p.Id == id);

        if (product == null) return NotFound();

        return Ok(product);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] CreateMasterProductDto dto)
    {
        var product = await _db.MasterProducts
            .Include(p => p.Links)
            .FirstOrDefaultAsync(p => p.Id == id);

        if (product == null) return NotFound();

        product.ProductName = dto.ProductName;
        product.Category = dto.Category;
        product.TargetPrice = dto.TargetPrice;
        product.MovementType = string.IsNullOrWhiteSpace(dto.MovementType) ? "Fast Move" : dto.MovementType;

        // Clear existing links and re-add for simplicity
        _db.ProductLinks.RemoveRange(product.Links);
        
        var newLinks = new List<ProductLink>();
        foreach (var linkDto in dto.Links)
        {
            var website = !string.IsNullOrWhiteSpace(linkDto.WebsiteName)
                ? linkDto.WebsiteName
                : DetectWebsite(linkDto.ProductUrl);

            newLinks.Add(new ProductLink
            {
                WebsiteName = website,
                ProductUrl = linkDto.ProductUrl,
                MasterProductId = id
            });
        }
        product.Links = newLinks;

        await _db.SaveChangesAsync();
        return Ok(product);
    }


    private static string DetectWebsite(string url)
    {
        var lower = url.ToLowerInvariant();
        if (lower.Contains("amazon.") || lower.Contains("amzn.")) return "Amazon";
        if (lower.Contains("flipkart.") || lower.Contains("fkrt.")) return "Flipkart";
        if (lower.Contains("reliancedigital.")) return "RelianceDigital";
        if (lower.Contains("myntra.")) return "Myntra";
        if (lower.Contains("bigbasket.")) return "BigBasket";
        if (lower.Contains("swiggy.")) return "SwiggyInstamart";
        if (lower.Contains("croma.")) return "Croma";
        if (lower.Contains("vijaysales.")) return "VijaySales";
        if (lower.Contains("jiomart.")) return "JioMart";
        if (lower.Contains("tataneu.") || lower.Contains("tatacliq.")) return "TataNeu";
        return "Unknown";
    }
}