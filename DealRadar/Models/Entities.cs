using System.Text.Json.Serialization;

namespace DealRadar.Api.Models;

public class MasterProduct
{
    public int Id { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public decimal TargetPrice { get; set; }

    // NEW: Dropdown values -> "Fast Move", "Average Move", "Slow Movement"
    public string MovementType { get; set; } = "Fast Move";

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<ProductLink> Links { get; set; } = new();
}

public class ProductLink
{
    public int Id { get; set; }
    public int MasterProductId { get; set; }

    [JsonIgnore]
    public MasterProduct? MasterProduct { get; set; }

    public string WebsiteName { get; set; } = string.Empty;
    public string ProductUrl { get; set; } = string.Empty;
    public decimal? CurrentPrice { get; set; }
    public decimal CouponDiscount { get; set; }
    public decimal MaxCardDiscount { get; set; }
    public decimal? EffectivePrice { get; set; }
    public string? CardOfferSummary { get; set; }

    // NEW: Margin & Automatic Tag Fields
    public decimal? MarginAmount { get; set; }
    public decimal? MarginPercentage { get; set; }
    public string? DealTag { get; set; }

    public decimal? LastAlertedPrice { get; set; }
    public bool IsInStock { get; set; } = true;
    public DateTime? LastCheckedAt { get; set; }
}

public class PriceHistory
{
    public int Id { get; set; }
    public int ProductLinkId { get; set; }
    public decimal Price { get; set; }
    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;
}