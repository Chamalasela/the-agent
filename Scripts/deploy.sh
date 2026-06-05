#!/usr/bin/env bash
# deploy.sh – Pull the latest xianix-agent and executor images on a remote Azure VM
# and (re)start the agent container.
#
# Usage:
#   ./deploy.sh [--resource-group <rg>] [--vm-name <vm>] [--subscription <id>]
#
# All three flags are optional if the defaults below already match your environment.
# The script requires the Azure CLI to be installed and an active `az login` session.
set -euo pipefail

# ── Defaults (override via flags or environment variables) ──────────────────
RESOURCE_GROUP="${DEPLOY_RESOURCE_GROUP:-xianix-agent-rg}"
VM_NAME="${DEPLOY_VM_NAME:-xianix-agent-vm}"
SUBSCRIPTION="${DEPLOY_SUBSCRIPTION:-}"          # leave empty to use the az default
AGENT_IMAGE="99xio/xianix-agent:latest"
EXECUTOR_IMAGE="99xio/xianix-executor:latest"
REMOTE_START_SCRIPT="/etc/xianix/start-agent.sh"

# ── Argument parsing ─────────────────────────────────────────────────────────
while [[ $# -gt 0 ]]; do
  case "$1" in
    --resource-group) RESOURCE_GROUP="$2"; shift 2 ;;
    --vm-name)        VM_NAME="$2";         shift 2 ;;
    --subscription)   SUBSCRIPTION="$2";    shift 2 ;;
    *) echo "Unknown option: $1" >&2; exit 1 ;;
  esac
done

# ── Validate prerequisites ────────────────────────────────────────────────────
if ! command -v az &>/dev/null; then
  echo "ERROR: Azure CLI (az) is not installed or not on PATH." >&2
  exit 1
fi

if ! az account show &>/dev/null; then
  echo "ERROR: No active Azure CLI session. Run 'az login' first." >&2
  exit 1
fi

SUBSCRIPTION_ARGS=()
if [[ -n "$SUBSCRIPTION" ]]; then
  SUBSCRIPTION_ARGS=(--subscription "$SUBSCRIPTION")
fi

echo "==> Target VM:  ${RESOURCE_GROUP}/${VM_NAME}"
[[ -n "$SUBSCRIPTION" ]] && echo "    Subscription: ${SUBSCRIPTION}"
echo ""

# ── Helper: run a command on the remote VM ────────────────────────────────────
run_remote() {
  local description="$1"
  local script="$2"

  echo "==> ${description}"
  az vm run-command invoke \
    --resource-group  "$RESOURCE_GROUP" \
    --name            "$VM_NAME" \
    --command-id      RunShellScript \
    --scripts         "$script" \
    ${SUBSCRIPTION_ARGS[@]+"${SUBSCRIPTION_ARGS[@]}"} \
    --output          json \
  | jq -r '
      .value[]
      | select(.code != null)
      | "\(.code)\n\(.message)"
    ' 2>/dev/null || true
  echo ""
}

# ── Step 1: Pull images ───────────────────────────────────────────────────────
run_remote \
  "Pulling ${AGENT_IMAGE}" \
  "docker pull ${AGENT_IMAGE}"

run_remote \
  "Pulling ${EXECUTOR_IMAGE}" \
  "docker pull ${EXECUTOR_IMAGE}"

# ── Step 2: (Re)start the agent ───────────────────────────────────────────────
# If the canonical start-agent.sh exists on the VM, delegate to it so all
# Key Vault secret resolution and container flags stay in one place.
# Otherwise fall back to a minimal inline restart that reuses whatever env
# vars are already set on the running container.
run_remote \
  "Restarting agent container" \
  "
if [ -f '${REMOTE_START_SCRIPT}' ]; then
  bash '${REMOTE_START_SCRIPT}'
else
  PREV_ENV=\$(docker inspect xianix-agent 2>/dev/null \
    | jq -r 'if length > 0 then .[0].Config.Env[] | \"-e \" + . else empty end' \
    | tr '\n' ' ' || true)
  docker rm -f xianix-agent 2>/dev/null || true
  # shellcheck disable=SC2086
  docker run -d \
    --name xianix-agent \
    --restart unless-stopped \
    -v /var/run/docker.sock:/var/run/docker.sock \
    \$PREV_ENV \
    ${AGENT_IMAGE}
fi
"

echo "==> Deploy complete."
