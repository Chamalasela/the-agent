using Xianix.Rules;

namespace TheAgent.Tests.Rules;

/// <summary>
/// Unit tests for <see cref="CatalogPlugin.RequiredEnvs"/> — the model-facing list of
/// every env declared on at least one execution that uses a given plugin.
///
/// The chat tool no longer uses any per-plugin env breakdown to forward credentials —
/// envs are sourced rule-wide via <see cref="RulesEnvCatalog"/> instead — but
/// <c>RequiredEnvs</c> is still surfaced to the LLM by <c>ListAvailablePlugins</c> so the
/// model can ask the user about missing vault entries before triggering a run.
/// </summary>
public class AvailablePluginsCatalogTests
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
        string pluginName,
        params (string name, string value, bool mandatory)[] envs) =>
        new()
        {
            Name      = $"{platform}-{pluginName}",
            Platform  = platform,
            Plugins   = [new PluginEntry { PluginName = pluginName, Marketplace = "mp" }],
            WithEnvs  = envs.Select(e => new EnvEntry
            {
                Name      = e.name,
                Value     = e.value,
                Mandatory = e.mandatory,
            }).ToList(),
        };

    [Fact]
    public void BuildCatalog_RequiredEnvs_UnionEveryEnvAcrossExecutionsThatUseThePlugin()
    {
        var rules = new[]
        {
            RuleSetWith(
                Execution("github",      "shared", ("GITHUB-TOKEN",      "secrets.GITHUB-TOKEN",      true)),
                Execution("azuredevops", "shared", ("AZURE-DEVOPS-TOKEN","secrets.AZURE-DEVOPS-TOKEN",true))),
        };

        var plugin = Assert.Single(AvailablePluginsCatalog.BuildCatalog(rules));

        Assert.Equal("shared", plugin.PluginName);
        Assert.Equal(
            new[] { "AZURE-DEVOPS-TOKEN", "GITHUB-TOKEN" },
            plugin.RequiredEnvs.Select(e => e.Name).OrderBy(n => n).ToArray());
    }

    [Fact]
    public void BuildCatalog_RequiredEnvs_DedupesByEnvNameFirstWins()
    {
        var rules = new[]
        {
            RuleSetWith(
                Execution("github", "p", ("GITHUB-TOKEN", "secrets.GITHUB-TOKEN-A", true)),
                Execution("github", "p", ("GITHUB-TOKEN", "secrets.GITHUB-TOKEN-B", false))),
        };

        var plugin = Assert.Single(AvailablePluginsCatalog.BuildCatalog(rules));

        var entry = Assert.Single(plugin.RequiredEnvs);
        Assert.Equal("GITHUB-TOKEN", entry.Name);
        Assert.True(entry.Mandatory);
    }

    /// <summary>
    /// A rule-set-wide common env applies to every execution in that rule set, so every
    /// plugin invoked from any of those executions inherits it in its <c>RequiredEnvs</c>.
    /// This is what powers the "declare GITHUB-TOKEN once at the top of the rule set"
    /// pattern without losing visibility in the chat catalog.
    /// </summary>
    [Fact]
    public void BuildCatalog_RequiredEnvs_IncludesRuleSetCommonEnvsForEveryPluginInRuleSet()
    {
        var rules = new[]
        {
            RuleSetWithCommon(
                [ Env("GITHUB-TOKEN", "secrets.GITHUB-TOKEN", mandatory: true) ],
                Execution("github", "pr-reviewer"),
                Execution("github", "req-analyst")),
        };

        var catalog = AvailablePluginsCatalog.BuildCatalog(rules);

        Assert.Equal(2, catalog.Count);
        foreach (var plugin in catalog)
        {
            var entry = Assert.Single(plugin.RequiredEnvs);
            Assert.Equal("GITHUB-TOKEN", entry.Name);
            Assert.True(entry.Mandatory);
        }
    }

    /// <summary>
    /// When the rule-set common and the execution both declare an env with the same name,
    /// the per-plugin <c>RequiredEnvs</c> dedup is first-wins — the common entry is added
    /// before the execution loop reaches its own with-envs, so the common version stays
    /// (matches the dedup order in <c>CatalogPluginBuilder.AddUsage</c>).
    /// </summary>
    [Fact]
    public void BuildCatalog_RequiredEnvs_RuleSetCommonWinsOverDuplicateExecutionEnv()
    {
        var rules = new[]
        {
            RuleSetWithCommon(
                [ Env("GITHUB-TOKEN", "secrets.GITHUB-TOKEN-COMMON", mandatory: true) ],
                Execution("github", "p", ("GITHUB-TOKEN", "secrets.GITHUB-TOKEN-EXEC", false))),
        };

        var plugin = Assert.Single(AvailablePluginsCatalog.BuildCatalog(rules));

        var entry = Assert.Single(plugin.RequiredEnvs);
        Assert.Equal("GITHUB-TOKEN", entry.Name);
        Assert.True(entry.Mandatory);
    }

    /// <summary>
    /// Empty-name common entries are dropped from <c>RequiredEnvs</c> just like
    /// execution-level entries — defensive against typo'd rules.json.
    /// </summary>
    [Fact]
    public void BuildCatalog_RequiredEnvs_DropsBlankNameRuleSetCommonEntries()
    {
        var rules = new[]
        {
            RuleSetWithCommon(
                [ Env("", "secrets.NOPE"), Env("GITHUB-TOKEN", "secrets.GITHUB-TOKEN", true) ],
                Execution("github", "p")),
        };

        var plugin = Assert.Single(AvailablePluginsCatalog.BuildCatalog(rules));

        var entry = Assert.Single(plugin.RequiredEnvs);
        Assert.Equal("GITHUB-TOKEN", entry.Name);
    }
}
