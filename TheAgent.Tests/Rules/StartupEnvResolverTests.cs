using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xianix.Rules;

namespace TheAgent.Tests.Rules;

/// <summary>
/// Unit tests for <see cref="StartupEnvResolver.TryResolveFromRuleSets"/> — the pure,
/// in-memory part of the agent-startup env resolver. The knowledge-fetching entry
/// point (<see cref="StartupEnvResolver.TryResolveValueAsync"/>) reads from Xians
/// Knowledge via <see cref="RulesKnowledge.LoadAsync"/>, which is integration-tested
/// at runtime when the supervisor subagent handles its first chat message; this class
/// exercises the resolution rules in isolation so they can't silently regress.
///
/// The resolver only looks at <see cref="WebhookRuleSet.WithEnvs"/> (the rule-set-wide
/// common envs) — execution-level envs are intentionally out of scope for agent-process
/// credentials since the agent doesn't bind to any single execution. The container path
/// keeps honouring execution-level overrides via <see cref="WebhookRulesEvaluator"/>'s
/// merge.
/// </summary>
public class StartupEnvResolverTests
{
    private static readonly ILogger Logger = NullLogger.Instance;

    private static WebhookRuleSet RuleSetWithCommon(params EnvEntry[] commonEnvs) =>
        new()
        {
            WebhookName = "Default",
            WithEnvs    = commonEnvs.ToList(),
        };

    private static EnvEntry Env(string name, string value, bool constant = false, bool mandatory = false) =>
        new() { Name = name, Value = value, Constant = constant, Mandatory = mandatory };

    [Fact]
    public void TryResolveFromRuleSets_ConstantEntry_ReturnsLiteralValue()
    {
        var rules = new[]
        {
            RuleSetWithCommon(Env("ANTHROPIC-API-KEY", "sk-literal", constant: true)),
        };

        var resolved = StartupEnvResolver.TryResolveFromRuleSets(rules, "ANTHROPIC-API-KEY", Logger);

        Assert.Equal("sk-literal", resolved);
    }

    [Fact]
    public void TryResolveFromRuleSets_HostReference_ReadsFromProcessEnv()
    {
        const string hostVar = "XIANIX_TEST_ANTHROPIC_KEY";
        try
        {
            Environment.SetEnvironmentVariable(hostVar, "sk-from-host");
            var rules = new[]
            {
                RuleSetWithCommon(Env("ANTHROPIC-API-KEY", $"host.{hostVar}")),
            };

            var resolved = StartupEnvResolver.TryResolveFromRuleSets(rules, "ANTHROPIC-API-KEY", Logger);

            Assert.Equal("sk-from-host", resolved);
        }
        finally
        {
            Environment.SetEnvironmentVariable(hostVar, null);
        }
    }

    /// <summary>
    /// A <c>host.*</c> entry pointing at an unset env var falls back to <c>null</c> so
    /// the caller can take its own fallback path. We deliberately do NOT silently
    /// promote it to "look up the entry name on the host" — that would defeat the
    /// purpose of the explicit reference. The caller in <c>XianixAgent</c> then reads
    /// the host env directly via <c>EnvConfig.AnthropicApiKey</c>.
    /// </summary>
    [Fact]
    public void TryResolveFromRuleSets_HostReferenceToUnsetVar_ReturnsNull()
    {
        const string hostVar = "XIANIX_TEST_UNSET_KEY_THAT_DOES_NOT_EXIST";
        Environment.SetEnvironmentVariable(hostVar, null);
        var rules = new[]
        {
            RuleSetWithCommon(Env("ANTHROPIC-API-KEY", $"host.{hostVar}")),
        };

        var resolved = StartupEnvResolver.TryResolveFromRuleSets(rules, "ANTHROPIC-API-KEY", Logger);

        Assert.Null(resolved);
    }

    /// <summary>
    /// <c>secrets.*</c> requires the Xians tenant-scoped Secret Vault, which doesn't
    /// exist at agent startup (no tenant context). The resolver must therefore return
    /// <c>null</c> so the caller falls back to its host-env default — silently using a
    /// per-tenant secret as a process-wide credential would be incorrect.
    /// </summary>
    [Fact]
    public void TryResolveFromRuleSets_SecretReference_ReturnsNullAtStartup()
    {
        var rules = new[]
        {
            RuleSetWithCommon(Env("ANTHROPIC-API-KEY", "secrets.ANTHROPIC-API-KEY")),
        };

        var resolved = StartupEnvResolver.TryResolveFromRuleSets(rules, "ANTHROPIC-API-KEY", Logger);

        Assert.Null(resolved);
    }

    [Fact]
    public void TryResolveFromRuleSets_NoMatchingEntry_ReturnsNull()
    {
        var rules = new[]
        {
            RuleSetWithCommon(Env("GITHUB-TOKEN", "secrets.GITHUB-TOKEN")),
        };

        var resolved = StartupEnvResolver.TryResolveFromRuleSets(rules, "ANTHROPIC-API-KEY", Logger);

        Assert.Null(resolved);
    }

    /// <summary>
    /// First-wins across rule sets — matches <see cref="RulesEnvCatalog"/>'s policy
    /// and keeps a regression of "two rule sets both declare ANTHROPIC-API-KEY at the
    /// top level" deterministic.
    /// </summary>
    [Fact]
    public void TryResolveFromRuleSets_DuplicateAcrossRuleSets_FirstWins()
    {
        var rules = new[]
        {
            RuleSetWithCommon(Env("ANTHROPIC-API-KEY", "sk-first",  constant: true)),
            RuleSetWithCommon(Env("ANTHROPIC-API-KEY", "sk-second", constant: true)),
        };

        var resolved = StartupEnvResolver.TryResolveFromRuleSets(rules, "ANTHROPIC-API-KEY", Logger);

        Assert.Equal("sk-first", resolved);
    }

    /// <summary>
    /// Execution-level <c>with-envs</c> entries are deliberately ignored at startup —
    /// the agent process credential lives at the rule-set level by design.
    /// </summary>
    [Fact]
    public void TryResolveFromRuleSets_ExecutionLevelEntryIsIgnored()
    {
        var rules = new[]
        {
            new WebhookRuleSet
            {
                WebhookName = "Default",
                WithEnvs    = [],
                Executions  =
                [
                    new WebhookExecution
                    {
                        Name     = "block",
                        WithEnvs = [ Env("ANTHROPIC-API-KEY", "sk-exec", constant: true) ],
                    },
                ],
            },
        };

        var resolved = StartupEnvResolver.TryResolveFromRuleSets(rules, "ANTHROPIC-API-KEY", Logger);

        Assert.Null(resolved);
    }

    [Fact]
    public void TryResolveFromRuleSets_BareValueWithoutPrefix_ReturnsNull()
    {
        // Bare / typo'd values are rejected (no `host.`, `secrets.`, and constant is
        // false) — same loud-failure-beats-quiet-ambiguity rule as the container path,
        // except we degrade to "fall back to host env" rather than throwing.
        var rules = new[]
        {
            RuleSetWithCommon(Env("ANTHROPIC-API-KEY", "sk-bare")),
        };

        var resolved = StartupEnvResolver.TryResolveFromRuleSets(rules, "ANTHROPIC-API-KEY", Logger);

        Assert.Null(resolved);
    }

    [Fact]
    public void TryResolveFromRuleSets_ConstantWithEmptyValue_ReturnsNull()
    {
        var rules = new[]
        {
            RuleSetWithCommon(Env("ANTHROPIC-API-KEY", "", constant: true)),
        };

        var resolved = StartupEnvResolver.TryResolveFromRuleSets(rules, "ANTHROPIC-API-KEY", Logger);

        Assert.Null(resolved);
    }

    [Fact]
    public void TryResolveFromRuleSets_EmptyRuleSetsList_ReturnsNull()
    {
        var resolved = StartupEnvResolver.TryResolveFromRuleSets(
            Array.Empty<WebhookRuleSet>(), "ANTHROPIC-API-KEY", Logger);

        Assert.Null(resolved);
    }

    [Fact]
    public void TryResolveFromRuleSets_BlankEnvNamesInRuleSetAreSkipped()
    {
        var rules = new[]
        {
            RuleSetWithCommon(
                Env("",                 "sk-blank",  constant: true),
                Env("ANTHROPIC-API-KEY","sk-actual", constant: true)),
        };

        var resolved = StartupEnvResolver.TryResolveFromRuleSets(rules, "ANTHROPIC-API-KEY", Logger);

        Assert.Equal("sk-actual", resolved);
    }

    // Integration tests that hit the live <see cref="RulesKnowledge.LoadAsync"/>
    // reader are intentionally out of scope here: that path requires an active
    // <see cref="Xians.Lib.Agents.Core.XiansContext"/>, which only exists inside a
    // workflow execution. End-to-end coverage of the lazy lookup lives with the
    // supervisor subagent's first-message flow.
}
