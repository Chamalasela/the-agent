using System.Text.Json;
using Microsoft.Extensions.Logging;
using Temporalio.Exceptions;
using Temporalio.Workflows;
using Xianix.Activities;
using Xianix.Containers;
using Xians.Lib.Agents.Core;

namespace Xianix.Workflows;

/// <summary>
/// Chat-initiated repo onboarding. Runs the executor container in
/// <c>XIANIX-MODE=prepare</c>, which causes <c>Executor/prepare_repo.sh</c> to bare-clone
/// the repository into the tenant volume and exit before any plugin/prompt phase.
///
/// Started by <c>SupervisorSubagentTools.OnboardRepository</c> via
/// <c>SubWorkflowService.StartAsync</c> (fire-and-forget — the chat tool returns
/// immediately, this workflow becomes the source of truth for user-facing output).
///
/// Mirrors <see cref="ClaudeCodeChatWorkflow"/> closely on purpose so the chat user sees
/// the same kind of progress + completion stream regardless of which tool they triggered.
/// </summary>
//[Workflow(Constants.AgentName + ":Onboard Repository Workflow")]
[Workflow]
public class OnboardRepositoryWorkflow
{
    [WorkflowRun]
    public async Task RunAsync(OnboardRepositoryRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);

        try
        {
            await ExecutePipelineAsync(req);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Workflow.Logger.LogError(ex,
                "OnboardRepositoryWorkflow failed for tenant={TenantId}, repo={Repo}.",
                req.TenantId, req.RepositoryName);
            await NotifyAsync(req, $"Repository onboarding failed: {ex.Message}");
            throw new ApplicationFailureException(
                $"OnboardRepositoryWorkflow failed: {ex.Message}", ex, nonRetryable: true);
        }
    }

    private static async Task ExecutePipelineAsync(OnboardRepositoryRequest req)
    {
        Workflow.Logger.LogInformation(
            "OnboardRepositoryWorkflow starting: tenant={TenantId}, repo={Repo}, platform={Platform}, participant={ParticipantId}.",
            req.TenantId, req.RepositoryName, req.Platform, req.ParticipantId);

        await NotifyAsync(req,
            $"Onboarding `{req.RepositoryName}` (platform: `{req.Platform}`) — cloning into your tenant workspace.");

        // EnsureWorkspaceVolumeAsync is the *only* place that stamps the
        // xianix.repository / xianix.tenant labels TenantVolumeReader.ListAsync keys off.
        // Calling it for an unknown URL is what makes the repo show up in
        // ListTenantRepositories on subsequent chat turns.
        var volumeName = await Workflow.ExecuteActivityAsync(
            (ContainerActivities a) => a.EnsureWorkspaceVolumeAsync(req.TenantId, req.RepositoryUrl),
            ContainerWorkflowOptions.Standard);

        var input = BuildContainerInput(req, volumeName, Workflow.NewGuid().ToString("N")[..8]);

        var containerId = await Workflow.ExecuteActivityAsync(
            (ContainerActivities a) => a.StartContainerAsync(input),
            ContainerWorkflowOptions.Standard);

        try
        {
            var result = await Workflow.ExecuteActivityAsync(
                (ContainerActivities a) => a.WaitAndCollectOutputAsync(
                    containerId,
                    req.TenantId,
                    $"onboard:{req.RepositoryName}",
                    (int)ContainerWorkflowOptions.ContainerExecutionTimeout.TotalSeconds),
                ContainerWorkflowOptions.Wait);

            string summary;
            if (result.Succeeded)
            {
                summary = $"Repository `{req.RepositoryName}` onboarded successfully. " +
                          $"You can now run prompts against it with `RunClaudeCodeOnRepository`.";
            }
            else
            {
                summary = BuildFailureSummary(
                    req.RepositoryName, req.Platform, result.ExitCode, result.StdErr);
            }

            await NotifyAsync(req, summary);

            Workflow.Logger.LogInformation(
                "OnboardRepositoryWorkflow finished: tenant={TenantId}, repo={Repo}, exitCode={ExitCode}.",
                req.TenantId, req.RepositoryName, result.ExitCode);
        }
        finally
        {
            await Workflow.DelayAsync(TimeSpan.FromSeconds(30));
            await Workflow.ExecuteActivityAsync(
                (ContainerActivities a) => a.CleanupContainerAsync(containerId),
                ContainerWorkflowOptions.Cleanup);
        }
    }

    private static Task NotifyAsync(OnboardRepositoryRequest req, string text) =>
        XiansContext.Messaging.SendChatAsSupervisorAsync(text, participantId: req.ParticipantId, scope: req.Scope);

    private static string Truncate(string text, int max) =>
        string.IsNullOrEmpty(text) || text.Length <= max
            ? text
            : text[..max] + $"…(+{text.Length - max} chars)";

    /// <summary>
    /// Turns a failed prepare-phase run into a user-facing message. In prepare mode there is
    /// no JSON envelope (execute_plugin.py never runs), so the only signal is the container's
    /// exit code plus stderr — typically a git clone error or the platform-credential
    /// fail-fast from <c>_common.sh</c>. We translate the most common git failures into the
    /// concrete decision the user has to make, then still append the raw output for anyone
    /// who needs it. <c>internal</c> + <c>static</c> so it can be unit-tested without a
    /// workflow host.
    /// </summary>
    internal static string BuildFailureSummary(
        string repositoryName, string platform, int exitCode, string? stdErr)
    {
        var rawDetail = string.IsNullOrWhiteSpace(stdErr)
            ? $"(no error output; container exit code {exitCode})"
            : stdErr;

        var guidance = DiagnoseFailure(stdErr, platform);

        var header = $"Onboarding failed for `{repositoryName}` (exit={exitCode}).";
        return guidance is null
            ? $"{header}\n\n{Truncate(rawDetail, 1500)}"
            : $"{header}\n\n{guidance}\n\n---\nRaw output:\n\n{Truncate(rawDetail!, 1500)}";
    }

    /// <summary>
    /// Best-effort classification of a git clone failure into an actionable message, or
    /// <c>null</c> when nothing matches (caller falls back to the raw stderr). Matching is
    /// case-insensitive and intentionally pattern-based: git/GitHub wording is stable enough
    /// for these well-known cases, and an unmatched failure still surfaces the raw output.
    /// </summary>
    private static string? DiagnoseFailure(string? stdErr, string platform)
    {
        if (string.IsNullOrWhiteSpace(stdErr))
            return null;

        var s = stdErr.ToLowerInvariant();
        bool Has(string needle) => s.Contains(needle, StringComparison.Ordinal);

        // Our own fail-fast from _common.sh when the tenant has no PAT in the vault.
        if (Has("github-token is required") || Has("github-token is empty"))
        {
            return "No valid `GITHUB-TOKEN` is configured for this tenant. " +
                   "Add it to the Xians Secret Vault, then retry onboarding.";
        }

        if (Has("azure_devops_token is required") || Has("azure-devops-token is required")
            || Has("azure_devops_token is empty"))
        {
            return "No valid `AZURE-DEVOPS-TOKEN` is configured for this tenant. " +
                   "Add it to the Xians Secret Vault, then retry onboarding.";
        }

        // GitHub deliberately masks 403 (private, no access) as 404 (not found), so this
        // single message has to cover both a wrong URL and a credential/access problem.
        if (Has("repository not found") || Has("not found"))
        {
            var credName = platform == RepositoryPlatform.AzureDevOps
                ? "AZURE-DEVOPS-TOKEN"
                : "GITHUB-TOKEN";
            return "The git host returned **\"repository not found\"**, which means one of two things:\n" +
                   "1. The repository owner/name is wrong — double-check the URL.\n" +
                   $"2. The repository is **private** and this tenant's `{credName}` is missing, expired, or lacks access to it.\n\n" +
                   "Hosts report both cases identically to avoid leaking private repos, so verify the URL first, then the token's access/scope.";
        }

        if (Has("authentication failed") || Has("invalid username or password")
            || Has("could not read username") || Has("permission denied")
            || Has("access denied") || Has(" 403"))
        {
            return "Authentication to the git host failed. This tenant's credential is likely missing, " +
                   "expired, or lacks access to the repository. Refresh the token in the Xians Secret Vault and retry.";
        }

        if (Has("could not resolve host") || Has("connection timed out")
            || Has("connection refused") || Has("network is unreachable")
            || Has("failed to connect"))
        {
            return "Could not reach the git host (a network/DNS problem). " +
                   "Check connectivity from the executor and retry.";
        }

        return null;
    }

    /// <summary>
    /// Constructs the <see cref="ContainerExecutionInput"/> for an onboarding run.
    /// Extracted (and made <c>internal</c>) so unit tests can assert that we always send
    /// <c>Mode="prepare"</c>, an empty plugin list, an empty prompt, and that the inputs
    /// dictionary contains exactly the structural keys the executor scripts expect — none
    /// of which can safely regress without breaking the chat onboarding flow.
    /// </summary>
    internal static ContainerExecutionInput BuildContainerInput(
        OnboardRepositoryRequest req, string volumeName, string executionId)
    {
        // Inputs the executor scripts read from XIANIX_INPUTS via jq. We deliberately do
        // NOT pass git-ref: a bare clone fetches all refs, and onboarding doesn't pick a
        // working ref — that decision happens later in RunClaudeCodeOnRepository.
        var inputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["repository-url"]  = req.RepositoryUrl,
            ["repository-name"] = req.RepositoryName,
            ["platform"]        = req.Platform,
        };

        return new ContainerExecutionInput
        {
            TenantId          = req.TenantId,
            ExecutionId       = executionId,
            InputsJson        = JsonSerializer.Serialize(inputs),
            ClaudeCodePlugins = "[]",
            WithEnvsJson      = ContainerEnvSerialization.Serialize(req.WithEnvs),
            Prompt            = string.Empty,
            VolumeName        = volumeName,
            Mode              = "prepare",
        };
    }
}
