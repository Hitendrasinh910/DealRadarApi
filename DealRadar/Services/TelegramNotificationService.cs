using System.Text;
using System.Text.Json;
using DealRadar.Api.Models;

namespace DealRadar.Api.Services;

public class TelegramNotificationService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<TelegramNotificationService> _logger;
    private readonly string _botToken;
    private readonly List<string> _chatIds;

    public TelegramNotificationService(
        HttpClient httpClient,
        IConfiguration config,
        ILogger<TelegramNotificationService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;

        // Clean token: trim spaces and remove accidental "bot" prefix if present
        var rawToken = (config["Telegram:BotToken"] ?? "").Trim();
        if (rawToken.StartsWith("bot", StringComparison.OrdinalIgnoreCase) && rawToken.Contains(':'))
        {
            rawToken = rawToken.Substring(3);
        }

        _botToken = rawToken;

        // Parse any comma-separated ChatIds from config + ALWAYS include 8874381531
        var rawChatIdConfig = (config["Telegram:ChatId"] ?? "").Trim();
        _chatIds = rawChatIdConfig
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Append("8874381531") // Always sends to 8874381531 as well
            .Distinct()
            .ToList();
    }

    public async Task<bool> SendDealAlertAsync(
        MasterProduct product,
        ProductLink link,
        ScrapeResult result,
        MarginEvaluation margin)
    {
        if (string.IsNullOrWhiteSpace(_botToken) ||
            !_chatIds.Any() ||
            _botToken.Contains("PASTE_YOUR", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Telegram credentials not configured.");
            return false;
        }

        decimal effectivePrice = result.EffectivePrice ?? result.ListedPrice ?? 0;

        string movementIcon = product.MovementType.ToLowerInvariant() switch
        {
            var s when s.Contains("fast") => "🚀 Fast Move",
            var s when s.Contains("average") || s.Contains("avarage") => "⚡ Average Move",
            _ => "🐢 Slow Movement"
        };

        var cardLines = result.CardOffers.Any()
            ? string.Join("\n", result.CardOffers.Take(3).Select(c => $"   • {c.BankName}: -₹{c.DiscountAmount:N0}{(c.IsEmiOnly ? " (EMI)" : "")}"))
            : "   • None detected";

        var message = $"""
        {margin.DealTag}

        📱 <b>Product:</b> {product.ProductName}
        📂 <b>Category:</b> {product.Category}
        📦 <b>Movement:</b> {movementIcon}
        🏪 <b>Store:</b> {link.WebsiteName}

        💰 <b>Listed Price:</b> ₹{result.ListedPrice:N0}
        🏷️ <b>Coupon:</b> -₹{result.CouponDiscount:N0}
        💳 <b>Best Card Discount:</b> -₹{result.MaxCardDiscount:N0}
        {cardLines}

        ✅ <b>Effective Buy Price:</b> ₹{effectivePrice:N0}
        🎯 <b>Target Price:</b> ₹{product.TargetPrice:N0}
        📈 <b>Margin:</b> ₹{margin.MarginAmount:N0} ({margin.MarginPercentage:0.##}%)

        ⏰ <b>Checked At:</b> {DateTime.UtcNow.AddHours(5.5):dd MMM hh:mm tt}

        🛒 <a href="{link.ProductUrl}"><b>Click Here to Buy Now</b></a>
        """;

        return await SendMessageToAllAsync(message);
    }

    public async Task<bool> SendMessageToAllAsync(string htmlMessage)
    {
        var url = $"https://api.telegram.org/bot{_botToken}/sendMessage";
        bool anySuccess = false;

        foreach (var chatId in _chatIds)
        {
            var payload = new
            {
                chat_id = chatId,
                text = htmlMessage,
                parse_mode = "HTML",
                disable_web_page_preview = false
            };

            try
            {
                var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync(url, content);

                if (!response.IsSuccessStatusCode)
                {
                    var err = await response.Content.ReadAsStringAsync();
                    _logger.LogError("Telegram API Error for ChatId {ChatId}: {Err}", chatId, err);
                }
                else
                {
                    anySuccess = true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send Telegram message to ChatId {ChatId}.", chatId);
            }
        }

        return anySuccess;
    }
}
