using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TheAgent;
using Xianix.Activities;

namespace Xianix.Rules;

/// <summary>
/// Resolves agent-process-level environment variables from rule-set-wide
/// <c>with-envs</c> entries declared at the top of <c>rules.json</c>. Used by
/// callers that today read straight from the host env (e.g.
/// <see cref="SupervisorSubagent"/>'s Anthropic key) so they can opt into a
/// "rules.json first, host env fallback" lookup without duplicating the parse logic.
///
/// Why bother? With rule-set-level common <c>with-envs</c>, an operator can declare
/// shared credentials once at the top of <c>rules.json</c> instead of repeating them
/// on every execution. That contract is honoured at container-start time by
/// <see cref="WebhookRulesEvaluator"/>'s merge. This resolver extends the same
/// "rules.json is the manifest of every credential this agent needs" contract to
/// agent-process values that don't go through the container pipeline — chiefly the
/// supervisor subagent's Anthropic key.
///
/// Source of truth: <strong>the uploaded Xians Knowledge document</strong>, read via
/// the canonical <see cref="RulesKnowledge.LoadAsync"/> reader. This matches every
/// other rules-consumer in the agent and keeps the knowledge-fetch logic in one place
/// (no embedded-resource reads, no duplicated <see cref="System.Text.Json"/> options).
///
/// Because the lookup goes through <see cref="Xians.Lib.Agents.Core.XiansContext.CurrentAgent"/>,
/// it can only be called from a workflow / agent execution context. Callers that
/// need a startup-time value (e.g. an Anthropic client constructor) must therefore
/// defer the lookup to first-use — see <see cref="SupervisorSubagent"/> for the
/// lazy-init pattern.
///
/// Resolution rules per <c>with-envs</c> entry:
/// <list type="bullet">
///   <item><description><c>"constant": true</c> — value is taken verbatim.</description></item>
///   <item><description><c>host.VAR_NAME</c> — looked up in the agent host process env
///     via <see cref="EnvConfig.Get"/> (which handles the dash/underscore alias).</description></item>
///   <item><description><c>secrets.SECRET-KEY</c> — <em>not</em> resolved here. The Xians
///     Secret Vault is tenant-scoped and a chat-process credential shouldn't depend on
///     a tenant scope, so a warning is logged and the resolver returns <c>null</c> so
///     the caller falls back to its host-env default.</description></item>
///   <item><description>Anything else (bare names, unknown prefixes) — same as a missing
///     entry: a warning is logged and <c>null</c> is returned. Loud failure beats quiet
///     ambiguity for credentials (mirrors <c>ContainerActivities.ResolveEnvValueAsync</c>).</description></item>
/// </list>
/// First-wins dedup across rule sets, ordinal: if two rule sets both declare an
/// entry by the same name the first wins, matching <see cref="RulesEnvCatalog"/>'s
/// policy.
/// </summary>
public static class StartupEnvResolver
{
    /// <summary>
    /// Attempts to resolve the value of a rule-set-level <c>with-envs</c> entry by
    /// name. Returns <c>null</c> when the entry is absent, unresolvable in the
    /// agent-process context (e.g. <c>secrets.*</c>), or when <c>rules.json</c>
    /// can't be parsed — callers should chain a host-env fallback after this.
    /// The lookup is <em>ordinal</em> — kebab-case env names
    /// (<c>ANTHROPIC-API-KEY</c>) are matched verbatim, same as everywhere else
    /// in the rules pipeline.
    /// </summary>
    /// <param name="envName">The env-var name as it appears in the <c>name</c> field
    /// of a <c>with-envs</c> entry, e.g. <c>"ANTHROPIC-API-KEY"</c>.</param>
    /// <param name="logger">Optional logger for diagnostic warnings (missing entry,
    /// unresolvable form). Pass <see cref="NullLogger.Instance"/> to stay silent.</param>
    /// <returns>Resolved non-empty string, or <c>null</c> if the caller should fall
    /// back to its default source.</returns>
    public static async Task<string?> TryResolveValueAsync(string envName, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(envName);
        logger ??= NullLogger.Instance;

        var ruleSets = await RulesKnowledge.LoadAsync(logger).ConfigureAwait(false);
        if (ruleSets is null || ruleSets.Count == 0)
            return null;

        return TryResolveFromRuleSets(ruleSets, envName, logger);
    }

    /// <summary>
    /// Pure resolution over already-deserialised rule sets, exposed for unit tests so
    /// the find + resolve behaviour can be exercised without a Xians Knowledge fixture.
    /// <see cref="TryResolveValueAsync"/> calls this after loading the document.
    /// </summary>
    internal static string? TryResolveFromRuleSets(
        IReadOnlyList<WebhookRuleSet> ruleSets, string envName, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(ruleSets);
        ArgumentException.ThrowIfNullOrWhiteSpace(envName);
        ArgumentNullException.ThrowIfNull(logger);

        var entry = FindRuleSetEnv(ruleSets, envName);
        if (entry is null)
        {
            logger.LogDebug(
                "No rule-set-level 'with-envs' entry named '{EnvName}' in rules.json — " +
                "falling back to host env.", envName);
            return null;
        }

        return Resolve(entry, envName, logger);
    }

    private static EnvEntry? FindRuleSetEnv(IReadOnlyList<WebhookRuleSet> ruleSets, string envName)
    {
        foreach (var set in ruleSets)
        {
            foreach (var env in set.WithEnvs)
            {
                if (string.IsNullOrWhiteSpace(env.Name)) continue;
                if (string.Equals(env.Name, envName, StringComparison.Ordinal))
                    return env;
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves a single <see cref="EnvEntry"/> for the agent-process path. Mirrors the
    /// container-side <c>ContainerActivities.ResolveEnvValueAsync</c> classification
    /// but consciously diverges on the unsupported branches: instead of throwing a
    /// non-retryable activation failure, this method logs and returns <c>null</c> so
    /// the caller can fall back to its host-env default. The container path is
    /// strict because a missing credential there means a run is silently misconfigured;
    /// here the host env is an entirely legitimate alternative source, so refusing to
    /// continue would be needlessly noisy.
    /// </summary>
    private static string? Resolve(EnvEntry entry, string envName, ILogger logger)
    {
        if (entry.Constant)
        {
            if (string.IsNullOrEmpty(entry.Value))
            {
                logger.LogWarning(
                    "Rule-set-level 'with-envs' entry '{EnvName}' has constant=true with " +
                    "an empty value — falling back to host env.", envName);
                return null;
            }
            return entry.Value;
        }

        var form = EnvValueForm.Parse(entry.Value);
        switch (form.Kind)
        {
            case EnvValueKind.Host:
                var hostValue = EnvConfig.Get(form.Identifier);
                if (string.IsNullOrEmpty(hostValue))
                {
                    logger.LogWarning(
                        "Rule-set-level 'with-envs' entry '{EnvName}' references host env " +
                        "'{HostVar}' which is not set — falling back to host env lookup of " +
                        "the entry name itself.", envName, form.Identifier);
                    return null;
                }
                return hostValue;

            case EnvValueKind.Secret:
                logger.LogWarning(
                    "Rule-set-level 'with-envs' entry '{EnvName}' uses 'secrets.{Key}', which " +
                    "is tenant-scoped. The agent-process resolver does not fetch tenant " +
                    "secrets — falling back to host env. If you need a per-tenant value " +
                    "here, supply the host env at the agent host level instead.",
                    envName, form.Identifier);
                return null;

            case EnvValueKind.EmptyHost:
            case EnvValueKind.EmptySecret:
            case EnvValueKind.Invalid:
            default:
                logger.LogWarning(
                    "Rule-set-level 'with-envs' entry '{EnvName}' has an unresolvable value " +
                    "form '{Value}' (expected 'host.VAR', 'secrets.KEY', or \"constant\": true). " +
                    "Falling back to host env.", envName, entry.Value);
                return null;
        }
    }
}
