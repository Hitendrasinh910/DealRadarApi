using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace DealRadar.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly IMemoryCache _cache;
    private readonly HttpClient _httpClient;
    private readonly string _botToken;
    private readonly string _chatId;
    private readonly string _expectedUsername;

    public AuthController(
        IMemoryCache cache,
        HttpClient httpClient,
        IConfiguration config)
    {
        _cache = cache;
        _httpClient = httpClient;
        _botToken = (config["Telegram:BotToken"] ?? "").Trim();
        _chatId = (config["Telegram:ChatId"] ?? "").Trim();
        // Set your Bot Username or Admin Username in appsettings.json
        _expectedUsername = config["Auth:AdminUsername"] ?? "DealRadarBot";
    }

    [HttpPost("send-otp")]
    public async Task<IActionResult> SendOtp([FromBody] SendOtpRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Username))
            return BadRequest(new { message = "Username is required." });

        // Clean @ symbol if entered: "@DealRadarBot" -> "DealRadarBot"
        string enteredUsername = request.Username.Trim().TrimStart('@');
        string cleanExpected = _expectedUsername.Trim().TrimStart('@');

        if (!string.Equals(enteredUsername, cleanExpected, StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { message = "Invalid Username." });
        }

        // Generate 4-digit OTP
        string otp = Random.Shared.Next(1000, 9999).ToString();

        // Store OTP in cache for 5 minutes
        string cacheKey = $"OTP_{cleanExpected.ToLowerInvariant()}";
        _cache.Set(cacheKey, otp, TimeSpan.FromMinutes(5));

        // Send OTP via Telegram Bot
        string telegramMessage = $"""
            🔐 <b>DealRadar Admin Login</b>

            Your 4-digit Login OTP is: <b>{otp}</b>

            ⏰ Valid for 5 minutes. Do not share this with anyone.
            """;

        var url = $"https://api.telegram.org/bot{_botToken}/sendMessage";
        var payload = new
        {
            chat_id = _chatId,
            text = telegramMessage,
            parse_mode = "HTML"
        };

        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var response = await _httpClient.PostAsync(url, content);

        if (!response.IsSuccessStatusCode)
        {
            return StatusCode(500, new { message = "Failed to send OTP to Telegram bot. Check your BotToken/ChatId." });
        }

        return Ok(new { success = true, message = "4-digit OTP sent to your Telegram bot!" });
    }

    [HttpPost("verify-otp")]
    public IActionResult VerifyOtp([FromBody] VerifyOtpRequest request)
    {
        string enteredUsername = (request.Username ?? "").Trim().TrimStart('@');
        string cleanExpected = _expectedUsername.Trim().TrimStart('@');

        string cacheKey = $"OTP_{cleanExpected.ToLowerInvariant()}";

        if (!_cache.TryGetValue(cacheKey, out string? cachedOtp) || string.IsNullOrWhiteSpace(cachedOtp))
        {
            return BadRequest(new { message = "OTP has expired. Please click 'Send OTP' again." });
        }

        if (cachedOtp != request.Otp?.Trim())
        {
            return BadRequest(new { message = "Invalid OTP entered." });
        }

        // Remove OTP once successfully verified so it cannot be reused
        _cache.Remove(cacheKey);

        // Return a mock auth token that Angular stores in localStorage
        return Ok(new
        {
            success = true,
            token = Guid.NewGuid().ToString("N"),
            message = "Login successful!"
        });
    }
}

public class SendOtpRequest
{
    public string Username { get; set; } = string.Empty;
}

public class VerifyOtpRequest
{
    public string Username { get; set; } = string.Empty;
    public string Otp { get; set; } = string.Empty;
}