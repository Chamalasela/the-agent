namespace Xianix.Workflows;

/// <summary>
/// Estimates Claude API cost from token counts for paths that only get token usage back
/// (the supervisor conversation turn talks to the Anthropic API directly, which — unlike
/// the Claude Code executor envelope — returns no USD figure). Container executions keep
/// using their authoritative executor-reported cost; this estimator is a fallback for the
/// token-only conversation layer.
///
/// <para>Rates are list prices per million tokens (USD) and are matched by model family
/// substring so minor version suffixes don't need a table entry. Prompt-cache writes are
/// billed at 1.25× the base input rate (5-minute cache) and cache reads at 0.10×, per
/// Anthropic's published multipliers. Unknown models return <see langword="null"/> so the
/// caller simply omits cost rather than reporting a misleading number.</para>
/// </summary>
internal static class ModelPricing
{
    private readonly record struct Rates(decimal InputPerMTok, decimal OutputPerMTok);

    private const decimal CacheWriteMultiplier = 1.25m; // 5-minute prompt cache write
    private const decimal CacheReadMultiplier  = 0.10m; // prompt cache hit
    private const decimal PerMillion           = 1_000_000m;

    /// <summary>
    /// Returns the estimated cost in USD, or <see langword="null"/> when the model is not in
    /// the pricing table or no token counts are available.
    /// </summary>
    public static double? EstimateCostUsd(
        string model,
        long? inputTokens,
        long? outputTokens,
        long? cacheReadTokens,
        long? cacheCreationTokens)
    {
        if (ResolveRates(model) is not { } rates)
            return null;

        if (inputTokens is null && outputTokens is null &&
            cacheReadTokens is null && cacheCreationTokens is null)
            return null;

        var cacheWriteRate = rates.InputPerMTok * CacheWriteMultiplier;
        var cacheReadRate  = rates.InputPerMTok * CacheReadMultiplier;

        var cost =
            (inputTokens         ?? 0) * rates.InputPerMTok  +
            (outputTokens        ?? 0) * rates.OutputPerMTok +
            (cacheCreationTokens ?? 0) * cacheWriteRate      +
            (cacheReadTokens     ?? 0) * cacheReadRate;

        return (double)(cost / PerMillion);
    }

    private static Rates? ResolveRates(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return null;

        var m = model.ToLowerInvariant();

        // Opus 4.x: $15 / $75 per MTok.
        if (m.Contains("opus"))
            return new Rates(15.00m, 75.00m);

        // Sonnet 3.5 / 3.7 / 4 / 4.5: $3 / $15 per MTok.
        if (m.Contains("sonnet"))
            return new Rates(3.00m, 15.00m);

        if (m.Contains("haiku"))
        {
            // Haiku 3 / 3.5 are markedly cheaper than the 4.x line.
            if (m.Contains("haiku-3") || m.Contains("haiku3") || m.Contains("3-5") || m.Contains("3.5"))
                return new Rates(0.80m, 4.00m);

            // Haiku 4 / 4.5: $1 / $5 per MTok.
            return new Rates(1.00m, 5.00m);
        }

        return null;
    }
}
