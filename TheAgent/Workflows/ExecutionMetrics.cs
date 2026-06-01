using Xianix.Activities;
using Xianix.Rules;
using Xians.Lib.Agents.Core;

namespace Xianix.Workflows;

/// <summary>
/// Single source of truth for reporting container-execution metrics to Xians. Both the
/// webhook path (<see cref="ProcessingWorkflow"/>) and the chat path
/// (<see cref="ClaudeCodeChatWorkflow"/>) funnel through here so the two paths emit an
/// identical metric/metadata schema and can never drift apart.
///
/// <para>Categories are kept separate per path (<c>webhook-executions</c> vs
/// <c>chat-executions</c>) so each can be charted independently, but every category gets
/// the same shape: <c>called</c>/<c>succeeded</c>/<c>failed</c> counts plus
/// <c>cost</c>/<c>duration</c>. Cross-path totals (<c>cost</c>, <c>tokens</c>,
/// <c>plugin-usage</c>) are emitted once with the same keys regardless of path.</para>
/// </summary>
internal static class ExecutionMetrics
{
    public const string ModelName = "claude";

    public const string WebhookCategory = "webhook-executions";
    public const string ChatCategory    = "chat-executions";

    public const string WebhookSource = "webhook";
    public const string ChatSource    = "chat";

    /// <summary>Cross-path category counting how often each plugin is used.</summary>
    public const string PluginUsageCategory = "plugin-usage";

    /// <summary>
    /// Builds and reports the full metric set for one completed container execution.
    /// Deterministic (safe to call from workflow code) and does not catch — callers wrap
    /// this in their own try/catch so the path-specific logger carries the failure context.
    /// </summary>
    public static Task ReportAsync(ExecutionMetricsContext ctx, ContainerExecutionResult result)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(result);

        var succeeded = result.Succeeded ? 1 : 0;
        var failed    = result.Succeeded ? 0 : 1;

        var builder = XiansContext.Metrics
            .ForModel(ModelName)
            .WithCustomIdentifier(ctx.CustomIdentifier)
            .WithMetadata(BuildMetadata(ctx, result));

        // ── Category-level totals (identical shape for every execution path) ──
        builder = builder
            .WithMetric(ctx.Category, "called",    1,         "count")
            .WithMetric(ctx.Category, "succeeded", succeeded, "count")
            .WithMetric(ctx.Category, "failed",    failed,    "count");

        if (result.CostUsd.HasValue)
            builder = builder.WithMetric(ctx.Category, "cost", result.CostUsd.Value, "usd");

        if (result.DurationSeconds.HasValue)
            builder = builder.WithMetric(ctx.Category, "duration", result.DurationSeconds.Value, "seconds");

        // ── Optional per-block breakdown (named webhook executions) ──
        // Lets a dashboard drill into individual rules.json execution blocks
        // (e.g. "azuredevops-pull-request-review" vs "github-pr-review").
        if (!string.IsNullOrWhiteSpace(ctx.BlockName))
        {
            var block = ctx.BlockName!;
            builder = builder
                .WithMetric(ctx.Category, block,                1,         "count")
                .WithMetric(ctx.Category, $"{block}.succeeded", succeeded, "count")
                .WithMetric(ctx.Category, $"{block}.failed",    failed,    "count");

            if (result.CostUsd.HasValue)
                builder = builder.WithMetric(ctx.Category, $"{block}.cost", result.CostUsd.Value, "usd");

            if (result.DurationSeconds.HasValue)
                builder = builder.WithMetric(ctx.Category, $"{block}.duration", result.DurationSeconds.Value, "seconds");
        }

        // ── Cross-path plugin usage ──
        foreach (var plugin in DistinctPluginNames(ctx.Plugins))
            builder = builder.WithMetric(PluginUsageCategory, plugin, 1, "count");

        // ── Shared cross-path totals (cost + tokens), same keys for every path ──
        if (result.CostUsd.HasValue)
            builder = builder.WithMetric("cost", "usd", result.CostUsd.Value, "usd");

        if (result.InputTokens.HasValue)
            builder = builder.WithMetric("tokens", "input", result.InputTokens.Value, "tokens");

        if (result.OutputTokens.HasValue)
            builder = builder.WithMetric("tokens", "output", result.OutputTokens.Value, "tokens");

        if (result.CacheReadTokens.HasValue)
            builder = builder.WithMetric("tokens", "cache_read", result.CacheReadTokens.Value, "tokens");

        if (result.CacheCreationTokens.HasValue)
            builder = builder.WithMetric("tokens", "cache_creation", result.CacheCreationTokens.Value, "tokens");

        return builder.ReportAsync();
    }

    private static Dictionary<string, string> BuildMetadata(
        ExecutionMetricsContext ctx, ContainerExecutionResult result)
    {
        var metadata = new Dictionary<string, string>
        {
            ["source"]               = ctx.Source,
            ["tenant_id"]            = ctx.TenantId,
            ["repository_url"]       = ctx.RepositoryUrl,
            ["repository_name"]      = ctx.RepositoryName,
            ["git_ref"]              = ctx.GitRef,
            ["platform"]             = ctx.Platform,
            ["prompt"]               = ctx.Prompt,
            ["execution_block_name"] = ctx.BlockName ?? string.Empty,
            ["plugins"]              = string.Join(",", DistinctPluginNames(ctx.Plugins)),
            ["exit_code"]            = result.ExitCode.ToString(),
            ["session_id"]           = result.SessionId ?? string.Empty,
        };

        if (ctx.ExtraMetadata is not null)
            foreach (var (key, value) in ctx.ExtraMetadata)
                metadata[key] = value;

        return metadata;
    }

    private static IEnumerable<string> DistinctPluginNames(IReadOnlyList<PluginEntry> plugins) =>
        plugins
            .Select(p => p.PluginName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal);
}

/// <summary>
/// Path-agnostic description of a single execution, used by <see cref="ExecutionMetrics"/>
/// to build a consistent metric/metadata payload. Webhook and chat callers populate the
/// same shape so neither path can quietly diverge from the other.
/// </summary>
internal sealed record ExecutionMetricsContext
{
    /// <summary>Metric category — <see cref="ExecutionMetrics.WebhookCategory"/> or <see cref="ExecutionMetrics.ChatCategory"/>.</summary>
    public required string Category { get; init; }

    /// <summary>Coarse origin tag for metadata — <see cref="ExecutionMetrics.WebhookSource"/> or <see cref="ExecutionMetrics.ChatSource"/>.</summary>
    public required string Source { get; init; }

    /// <summary>Custom identifier passed to <c>WithCustomIdentifier</c> (webhook name, or "chat").</summary>
    public required string CustomIdentifier { get; init; }

    public string TenantId       { get; init; } = string.Empty;
    public string RepositoryUrl  { get; init; } = string.Empty;
    public string RepositoryName { get; init; } = string.Empty;
    public string GitRef         { get; init; } = string.Empty;
    public string Platform       { get; init; } = string.Empty;
    public string Prompt         { get; init; } = string.Empty;

    /// <summary>Optional rules.json execution block name; drives the per-block metric breakdown.</summary>
    public string? BlockName { get; init; }

    public IReadOnlyList<PluginEntry> Plugins { get; init; } = [];

    /// <summary>Path-specific metadata merged in last (e.g. <c>participant_id</c>, <c>webhook_name</c>).</summary>
    public IReadOnlyDictionary<string, string>? ExtraMetadata { get; init; }
}
