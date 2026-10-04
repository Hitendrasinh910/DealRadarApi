namespace DealRadar.Api.Services;

public record MarginEvaluation(
    decimal MarginAmount,
    decimal MarginPercentage,
    string DealTag,
    bool ShouldTriggerAlert);

public static class DealMarginCalculator
{
    public static MarginEvaluation Evaluate(decimal targetPrice, decimal effectivePrice)
    {
        if (targetPrice <= 0)
            return new MarginEvaluation(0, 0, "No Target Set", false);

        // Positive MarginAmount = EffectivePrice is LOWER than TargetPrice (Profit)
        // Negative MarginAmount = EffectivePrice is HIGHER than TargetPrice
        decimal marginAmount = targetPrice - effectivePrice;
        decimal marginPct = Math.Round((marginAmount / targetPrice) * 100m, 2);

        // Rule 1: More than 10% profit -> 💥 LOOT DEAL (10%+)
        if (marginPct > 10.0m)
        {
            return new MarginEvaluation(marginAmount, marginPct, "💥 LOOT DEAL (10%+ Profit)", true);
        }

        // Rule 2: 5% to 10% profit -> 🔥 LOOT DEAL
        if (marginPct >= 5.0m && marginPct <= 10.0m)
        {
            return new MarginEvaluation(marginAmount, marginPct, "🔥 LOOT DEAL", true);
        }

        // Rule 3: 2% to 5% profit -> 💰 PROFIT DEAL
        if (marginPct >= 2.0m && marginPct < 5.0m)
        {
            return new MarginEvaluation(marginAmount, marginPct, "💰 PROFIT DEAL", true);
        }

        // Rule 4: Within ±2% of Target Price (e.g., -2% to +1.99%) -> 💳 CARD TO CASH DEAL (Small Profit)
        if (marginPct >= -2.0m && marginPct < 2.0m)
        {
            return new MarginEvaluation(marginAmount, marginPct, "💳 CARD TO CASH DEAL (Small Profit)", true);
        }

        // More than 2% expensive than Target Price -> Do not alert
        return new MarginEvaluation(marginAmount, marginPct, "❌ Above Target Price", false);
    }
}