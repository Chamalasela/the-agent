using Xianix.Rules;

namespace TheAgent.Tests.Rules;

/// <summary>
/// Tests the rule-wide env aggregator used by chat-driven dispatches. A chat run has no
/// matched <see cref="WebhookExecution"/> so we treat <c>rules.json</c> as the manifest of
/// "every credential this agent ever needs" and ship the platform-relevant subset every
/// time. The platform filter must keep a github run from inheriting Azure DevOps'
/// mandatory PAT (and vice versa), and platform-agnostic executions must always
/// contribute their envs.
/// </summary>
public class RulesEnvCatalogTests
{
    private static WebhookRuleSet RuleSetWith(params WebhookExecution[] executions) =>
        new() { WebhookName = "Default", Executions = executions.ToList() };

    private static WebhookRuleSet RuleSetWithCommon(
        IEnumerable<EnvEntry> commonEnvs,
        params WebhookExecution[] executions) =>
        new()
        {
            WebhookName = "Default",
            WithEnvs    = commonEnvs.ToList(),
            Executions  = executions.ToList(),
        };

    private static EnvEntry Env(string name, string value, bool mandatory = false) =>
        new() { Name = name, Value = value, Mandatory = mandatory };

    private static WebhookExecution Execution(
        string platform,
        params (string name, string value, bool mandatory)[] envs) =>
        new()
        {
            Name     = $"{platform}-exec",
            Platform = platform,
            WithEnvs = envs.Select(e => new EnvEntry
            {
                Name      = e.name,
                Value     = e.value,
                Mandatory = e.mandatory,
            }).ToList(),
        };

    [Fact]
    public void BuildEnvList_GitHubRun_DropsAzureDevOpsExecutionsEnvs()
    {
        var rules = new[]
        {
            RuleSetWith(
                Execution("github",      ("GITHUB-TOKEN",      "secrets.GITHUB-TOKEN",      true)),
                Execution("azuredevops", ("AZURE-DEVOPS-TOKEN","secrets.AZURE-DEVOPS-TOKEN",true))),
        };

        var picked = RulesEnvCatalog.BuildEnvList(rules, "github")
            .Select(e => e.Name)
            .ToArray();

        Assert.Equal(new[] { "GITHUB-TOKEN" }, picked);
    }

    [Fact]
    public void BuildEnvList_AzureDevOpsRun_DropsGitHubExecutionsEnvs()
    {
        var rules = new[]
        {
            RuleSetWith(
                Execution("github",      ("GITHUB-TOKEN",      "secrets.GITHUB-TOKEN",      true)),
                Execution("azuredevops", ("AZURE-DEVOPS-TOKEN","secrets.AZURE-DEVOPS-TOKEN",true))),
        };

        var picked = RulesEnvCatalog.BuildEnvList(rules, "azuredevops")
            .Select(e => e.Name)
            .ToArray();

        Assert.Equal(new[] { "AZURE-DEVOPS-TOKEN" }, picked);
    }

    [Fact]
    public void BuildEnvList_PlatformAgnosticExecution_AlwaysIncluded()
    {
        var rules = new[]
        {
            RuleSetWith(
                Execution("github", ("GITHUB-TOKEN", "secrets.GITHUB-TOKEN", true)),
                Execution(platform: "", ("CUSTOM-API-KEY", "secrets.CUSTOM", false))),
        };

        var picked = RulesEnvCatalog.BuildEnvList(rules, "github")
            .Select(e => e.Name)
            .OrderBy(n => n)
            .ToArray();

        Assert.Equal(new[] { "CUSTOM-API-KEY", "GITHUB-TOKEN" }, picked);
    }

    [Fact]
    public void BuildEnvList_AggregatesAcrossDifferentExecutionsAndRuleSets()
    {
        var rules = new[]
        {
            RuleSetWith(
                Execution("github", ("GITHUB-TOKEN",  "secrets.GITHUB-TOKEN",  true)),
                Execution("github", ("CUSTOM-API-KEY","secrets.CUSTOM",        false))),
            RuleSetWith(
                Execution("github", ("ANOTHER-TOKEN", "secrets.ANOTHER",       true))),
        };

        var picked = RulesEnvCatalog.BuildEnvList(rules, "github")
            .Select(e => e.Name)
            .OrderBy(n => n)
            .ToArray();

        Assert.Equal(
            new[] { "ANOTHER-TOKEN", "CUSTOM-API-KEY", "GITHUB-TOKEN" }, picked);
    }

    [Fact]
    public void BuildEnvList_DedupesByEnvName_FirstWinsPreservesMandatoryFromFirstHit()
    {
        var rules = new[]
        {
            RuleSetWith(
                Execution("github", ("GITHUB-TOKEN", "secrets.GITHUB-TOKEN-A", true)),
                Execution("github", ("GITHUB-TOKEN", "secrets.GITHUB-TOKEN-B", false))),
        };

        var entry = Assert.Single(RulesEnvCatalog.BuildEnvList(rules, "github"));

        Assert.Equal("GITHUB-TOKEN", entry.Name);
        Assert.Equal("secrets.GITHUB-TOKEN-A", entry.Value);
        Assert.True(entry.Mandatory);
    }

    [Fact]
    public void BuildEnvList_PlatformLookupIsCaseInsensitive()
    {
        var rules = new[]
        {
            RuleSetWith(Execution("GitHub", ("GITHUB-TOKEN", "secrets.GITHUB-TOKEN", true))),
        };

        var picked = RulesEnvCatalog.BuildEnvList(rules, "github")
            .Select(e => e.Name)
            .ToArray();

        Assert.Equal(new[] { "GITHUB-TOKEN" }, picked);
    }

    [Fact]
    public void BuildEnvList_NoMatchingExecutions_ReturnsEmpty()
    {
        var rules = new[]
        {
            RuleSetWith(Execution("github", ("GITHUB-TOKEN", "secrets.GITHUB-TOKEN", true))),
        };

        Assert.Empty(RulesEnvCatalog.BuildEnvList(rules, "azuredevops"));
    }

    [Fact]
    public void BuildEnvList_SkipsEnvEntriesWithBlankNames()
    {
        var rules = new[]
        {
            RuleSetWith(Execution("github",
                ("",             "secrets.WHATEVER",     true),
                ("GITHUB-TOKEN", "secrets.GITHUB-TOKEN", true))),
        };

        var picked = RulesEnvCatalog.BuildEnvList(rules, "github")
            .Select(e => e.Name)
            .ToArray();

        Assert.Equal(new[] { "GITHUB-TOKEN" }, picked);
    }

    /// <summary>
    /// Rule-set-wide common envs apply to every execution in the rule set, so the
    /// chat-dispatch catalog must always ship them — even when no per-execution env
    /// would have matched the platform filter. Mirrors what the operator wrote in
    /// rules.json: "this credential is needed by every run under this webhook."
    /// </summary>
    [Fact]
    public void BuildEnvList_RuleSetCommonEnvs_AlwaysIncludedRegardlessOfPlatformFilter()
    {
        var rules = new[]
        {
            RuleSetWithCommon(
                [ Env("GITHUB-TOKEN", "secrets.GITHUB-TOKEN", mandatory: true) ],
                Execution("azuredevops",
                    ("AZURE-DEVOPS-TOKEN", "secrets.AZURE-DEVOPS-TOKEN", true))),
        };

        var picked = RulesEnvCatalog.BuildEnvList(rules, "github")
            .Select(e => e.Name)
            .OrderBy(n => n)
            .ToArray();

        Assert.Equal(new[] { "GITHUB-TOKEN" }, picked);
    }

    /// <summary>
    /// When a rule-set common env and an execution-level env share a name, the catalog
    /// must surface it exactly once. First-wins dedup means the rule-set common's
    /// mandatory flag is preserved (we want "the strictest required state" to surface to
    /// the chat tool, and the rule-set common is encountered first in the walk).
    /// </summary>
    [Fact]
    public void BuildEnvList_DedupsAcrossRuleSetCommonAndExecutionEnvs_FirstWins()
    {
        var rules = new[]
        {
            RuleSetWithCommon(
                [ Env("GITHUB-TOKEN", "secrets.GITHUB-TOKEN-COMMON", mandatory: true) ],
                Execution("github",
                    ("GITHUB-TOKEN", "secrets.GITHUB-TOKEN-EXEC", false))),
        };

        var entry = Assert.Single(RulesEnvCatalog.BuildEnvList(rules, "github"));

        Assert.Equal("GITHUB-TOKEN", entry.Name);
        Assert.Equal("secrets.GITHUB-TOKEN-COMMON", entry.Value);
        Assert.True(entry.Mandatory);
    }

    /// <summary>
    /// Rule-set common envs and platform-filtered execution envs combine into one list
    /// when both contribute distinct names — this is the typical "common GITHUB-TOKEN
    /// plus platform-specific extras" shape.
    /// </summary>
    [Fact]
    public void BuildEnvList_CombinesRuleSetCommonsWithPlatformFilteredExecutionEnvs()
    {
        var rules = new[]
        {
            RuleSetWithCommon(
                [ Env("GITHUB-TOKEN", "secrets.GITHUB-TOKEN", mandatory: true) ],
                Execution("github",      ("EXTRA-GH",  "secrets.EXTRA-GH", false)),
                Execution("azuredevops", ("EXTRA-ADO", "secrets.EXTRA-ADO", false))),
        };

        var picked = RulesEnvCatalog.BuildEnvList(rules, "github")
            .Select(e => e.Name)
            .OrderBy(n => n)
            .ToArray();

        Assert.Equal(new[] { "EXTRA-GH", "GITHUB-TOKEN" }, picked);
    }

    /// <summary>
    /// A rule-set common env with a blank name is dropped (mirrors execution-level
    /// behaviour) — defensive against typos in rules.json.
    /// </summary>
    [Fact]
    public void BuildEnvList_RuleSetCommonEnv_BlankNameDropped()
    {
        var rules = new[]
        {
            RuleSetWithCommon(
                [ Env("", "secrets.NOPE"), Env("GITHUB-TOKEN", "secrets.GITHUB-TOKEN", true) ],
                Execution("github")),
        };

        var picked = RulesEnvCatalog.BuildEnvList(rules, "github")
            .Select(e => e.Name)
            .ToArray();

        Assert.Equal(new[] { "GITHUB-TOKEN" }, picked);
    }

    /// <summary>
    /// Locks in the chat-path promise: rule-set commons ship to chat dispatches for
    /// *any* platform, even when the rule set has no executions at all. This is the
    /// scenario where an operator has only declared platform-agnostic common envs
    /// (e.g. a global feature flag) and not yet wired any specific webhook execution
    /// — the chat tool should still pick those envs up so `RunClaudeCodeOnRepository`
    /// behaves the same way as a webhook run would.
    /// </summary>
    [Fact]
    public void BuildEnvList_RuleSetWithOnlyCommonEnvs_NoExecutions_ShipsToEveryPlatform()
    {
        var rules = new[]
        {
            RuleSetWithCommon(
                [
                    Env("GITHUB-TOKEN", "secrets.GITHUB-TOKEN", mandatory: true),
                    Env("CUSTOM-FLAG", "1", mandatory: false),
                ]),
        };

        var github = RulesEnvCatalog.BuildEnvList(rules, "github")
            .Select(e => e.Name).OrderBy(n => n).ToArray();
        var ado = RulesEnvCatalog.BuildEnvList(rules, "azuredevops")
            .Select(e => e.Name).OrderBy(n => n).ToArray();

        Assert.Equal(new[] { "CUSTOM-FLAG", "GITHUB-TOKEN" }, github);
        Assert.Equal(new[] { "CUSTOM-FLAG", "GITHUB-TOKEN" }, ado);
    }
}
