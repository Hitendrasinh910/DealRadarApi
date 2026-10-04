namespace DealRadar.Api.Models;

public class CreateMasterProductDto
{
    public string ProductName { get; set; } = string.Empty;
    public string Category { get; set; } = "Mobile";
    public decimal TargetPrice { get; set; }

    // Allowed values for Angular dropdown: "Fast Move", "Average Move", "Slow Movement"
    public string MovementType { get; set; } = "Fast Move";

    public List<CreateProductLinkDto> Links { get; set; } = new();
}

public class CreateProductLinkDto
{
    public string WebsiteName { get; set; } = string.Empty;
    public string ProductUrl { get; set; } = string.Empty;
}