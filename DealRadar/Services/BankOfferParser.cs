using System.Text.RegularExpressions;

namespace DealRadar.Api.Services;

public record ParsedCardOffer(
    string BankName,
    decimal DiscountAmount,
    bool IsEmiOnly,
    string RawOfferText);

public static class BankOfferParser
{
    private static readonly string[] KnownBanks =
        ["HDFC", "SBI", "ICICI", "Axis", "Kotak", "BOB", "Baroda", "IDFC", "Yes Bank", "AU Bank", "Amex", "HSBC", "OneCard", "RBL", "IndusInd", "Federal"];

    public static List<ParsedCardOffer> ParseOffers(IEnumerable<string> rawOfferTexts, decimal listedPrice, string websiteName)
    {
        var results = new List<ParsedCardOffer>();
        string store = websiteName.ToLowerInvariant();

        foreach (var rawBlock in rawOfferTexts.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            var lines = rawBlock.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

            foreach (var rawLine in lines)
            {
                var text = Regex.Replace(rawLine, @"\s+", " ").Trim();
                if (text.Length < 10 || text.Length > 350) continue;

                // =========================================================================
                // REQUIREMENT 1: EXCLUDE FLIPKART AXIS ON FLIPKART & AMAZON ICICI ON AMAZON
                // =========================================================================
                if (store.Contains("flipkart") && Regex.IsMatch(text, @"Flipkart\s*Axis", RegexOptions.IgnoreCase))
                {
                    continue; // Skip Flipkart Axis co-branded cashback card
                }

                if (store.Contains("amazon") && Regex.IsMatch(text, @"Amazon\s*(Pay)?\s*ICICI", RegexOptions.IgnoreCase))
                {
                    continue; // Skip Amazon Pay ICICI co-branded cashback card
                }

                // Skip non-bank noise
                if (Regex.IsMatch(text, @"\b(Exchange|interest\s*savings|Pay\s*Later|GST\s*invoice|Spotify|YouTube)\b", RegexOptions.IgnoreCase))
                    continue;

                // Detect Bank
                string bank = KnownBanks.FirstOrDefault(b =>
                    Regex.IsMatch(text, $@"\b{Regex.Escape(b)}\b", RegexOptions.IgnoreCase)) ?? "Select Bank Cards";

                bool isCardOrBankOffer = bank != "Select Bank Cards" ||
                    Regex.IsMatch(text, @"(Bank\s*Offer|Credit\s*Card|Debit\s*Card|Card\s*Txn|Card\s*Transaction|Instant\s*Bank\s*Discount|select\s*Credit\s*Cards)", RegexOptions.IgnoreCase);

                if (!isCardOrBankOffer) continue;

                // Check Minimum Purchase Value if stated
                var minOrderMatch = Regex.Match(text, @"(?:Min(?:imum)?\s*(?:purchase|order|txn|transaction|cart)\s*(?:value|amount)?(?:\s*of)?|orders\s*above)\s*(?:₹|Rs\.?|INR)\s*([\d,]+)", RegexOptions.IgnoreCase);
                if (minOrderMatch.Success && decimal.TryParse(minOrderMatch.Groups[1].Value.Replace(",", ""), out var minOrder))
                {
                    if (listedPrice < minOrder) continue;
                }

                decimal discount = ExtractDiscountAmount(text, listedPrice);

                // Sanity check: Discount must be at least ₹100 and <= 35% of product price
                if (discount >= 100 && discount <= listedPrice * 0.35m)
                {
                    bool isEmiOnly = Regex.IsMatch(text, @"\bEMI\b", RegexOptions.IgnoreCase) &&
                                     !Regex.IsMatch(text, @"Non[\s-]*EMI|Credit\s*Card\s*and\s*EMI", RegexOptions.IgnoreCase);

                    results.Add(new ParsedCardOffer(bank, discount, isEmiOnly, text));
                }
            }
        }

        return results
            .GroupBy(x => new { x.BankName, x.DiscountAmount })
            .Select(g => g.First())
            .OrderByDescending(x => x.DiscountAmount)
            .ToList();
    }

    private static decimal ExtractDiscountAmount(string text, decimal listedPrice)
    {
        // 1. Percentage with Cap: "10% instant discount up to ₹1,500"
        var pctMatch = Regex.Match(text, @"(\d+(?:\.\d+)?)\s*%\s*(?:instant\s*)?(?:discount|off|cashback).*?(?:up\s*to|upto|max(?:imum)?)\s*(?:₹|Rs\.?|INR)\s*([\d,]+(?:\.\d+)?)", RegexOptions.IgnoreCase);
        if (pctMatch.Success &&
            decimal.TryParse(pctMatch.Groups[1].Value, out var pct) &&
            decimal.TryParse(pctMatch.Groups[2].Value.Replace(",", ""), out var cap))
        {
            return Math.Min(Math.Round(listedPrice * (pct / 100m)), Math.Floor(cap));
        }

        // 2. Flat Rupee: "₹1,500 Off", "Flat ₹2000 Discount", "Save ₹1250"
        var flatMatch = Regex.Match(text, @"(?:₹|Rs\.?|INR)\s*([\d,]+(?:\.\d+)?)\s*(?:Instant\s*)?(?:Off|Discount|Cashback|Savings)", RegexOptions.IgnoreCase);
        if (flatMatch.Success && decimal.TryParse(flatMatch.Groups[1].Value.Replace(",", ""), out var amt1))
        {
            return Math.Floor(amt1);
        }

        var afterMatch = Regex.Match(text, @"(?:Instant\s*Discount|Discount|Flat|Up\s*to|Upto|Save|Extra)\s*(?:of\s*)?(?:₹|Rs\.?|INR)\s*([\d,]+(?:\.\d+)?)", RegexOptions.IgnoreCase);
        if (afterMatch.Success && decimal.TryParse(afterMatch.Groups[1].Value.Replace(",", ""), out var amt2))
        {
            return Math.Floor(amt2);
        }

        return 0;
    }

    public static decimal ParseCouponDiscount(string? rawCouponText, decimal listedPrice)
    {
        if (string.IsNullOrWhiteSpace(rawCouponText)) return 0;
        var rupeeMatch = Regex.Match(rawCouponText, @"(?:₹|Rs\.?|INR)\s*([\d,]+(?:\.\d+)?)", RegexOptions.IgnoreCase);
        if (rupeeMatch.Success && decimal.TryParse(rupeeMatch.Groups[1].Value.Replace(",", ""), out var val))
            return Math.Floor(val);

        return 0;
    }
}