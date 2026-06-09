using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;
using Xianix.Workflows;

namespace Xianix.Orchestrator;

/// <summary>
/// Suppresses duplicate webhook executions that carry the same fingerprint within a
/// configurable time window. This guards against platforms (e.g. Azure DevOps) that
/// occasionally deliver the same event twice in rapid succession.
/// </summary>
public interface IWebhookDeduplicationGuard
{
    /// <summary>
    /// Returns <c>true</c> if the request is a duplicate that should be suppressed;
    /// <c>false</c> if it is new and should be processed (and has now been recorded).
    /// </summary>
    bool IsDuplicate(ProcessingRequest request);
}

public sealed class WebhookDeduplicationGuard : IWebhookDeduplicationGuard
{
    // Keyed by fingerprint, value is the UTC timestamp when it was first seen.
    private readonly ConcurrentDictionary<string, DateTime> _seen = new(StringComparer.Ordinal);
    private readonly TimeSpan _window;
    private readonly ILogger<WebhookDeduplicationGuard> _logger;

    /// <param name="window">How long to suppress identical events. Defaults to 30 seconds.</param>
    public WebhookDeduplicationGuard(ILogger<WebhookDeduplicationGuard> logger, TimeSpan? window = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _window = window ?? TimeSpan.FromSeconds(30);
    }

    public bool IsDuplicate(ProcessingRequest request)
    {
        var fingerprint = BuildFingerprint(request);
        var now = DateTime.UtcNow;

        // Evict stale entries lazily on each call to avoid unbounded growth.
        EvictExpired(now);

        var firstSeen = _seen.GetOrAdd(fingerprint, _ => now);

        if (firstSeen == now)
        {
            // We just recorded it — this is the first occurrence.
            return false;
        }

        if (now - firstSeen < _window)
        {
            _logger.LogWarning(
                "Duplicate webhook suppressed — tenant={TenantId} block='{Block}' inputs=[{Inputs}] " +
                "was already triggered {Elapsed:F1}s ago (window={Window}s).",
                request.TenantId,
                request.ExecutionBlockName,
                FormatInputs(request.Inputs),
                (now - firstSeen).TotalSeconds,
                _window.TotalSeconds);
            return true;
        }

        // Window has expired: replace the old timestamp and let this one through.
        _seen[fingerprint] = now;
        return false;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string BuildFingerprint(ProcessingRequest request)
    {
        var sb = new StringBuilder();
        sb.Append(request.TenantId);
        sb.Append(':');
        sb.Append(request.ExecutionBlockName ?? request.Name);
        sb.Append(':');

        // Sort inputs for a stable key regardless of insertion order.
        foreach (var kv in request.Inputs.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            sb.Append(kv.Key);
            sb.Append('=');
            sb.Append(kv.Value?.ToString() ?? string.Empty);
            sb.Append(',');
        }

        return sb.ToString();
    }

    private static string FormatInputs(IReadOnlyDictionary<string, object?> inputs) =>
        string.Join(", ", inputs.OrderBy(k => k.Key, StringComparer.Ordinal)
                                .Select(kv => $"{kv.Key}={kv.Value}"));

    private void EvictExpired(DateTime now)
    {
        foreach (var key in _seen.Keys)
        {
            if (_seen.TryGetValue(key, out var ts) && now - ts >= _window)
                _seen.TryRemove(key, out _);
        }
    }
}
