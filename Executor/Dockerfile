FROM python:3.12-slim-bookworm

# Pinned to bookworm (Debian 12) on purpose:
#   * Azure CLI ships a bookworm repo (no trixie dist yet).
#   * Avoids Debian trixie's `t64` (64-bit time_t) package-rename transition,
#     which breaks `playwright install --with-deps` on restricted apt mirrors.
# System deps + Node 20 (npx/MCP) + git + GitHub CLI (gh) + Azure CLI + azure-devops extension (az devops).
RUN apt-get update && apt-get install -y --no-install-recommends \
    git \
    curl \
    jq \
    universal-ctags \
    ca-certificates \
    gnupg \
    && mkdir -p /etc/apt/keyrings \
    && curl -fsSL https://deb.nodesource.com/gpgkey/nodesource-repo.gpg.key \
        | gpg --dearmor -o /etc/apt/keyrings/nodesource.gpg \
    && echo "deb [signed-by=/etc/apt/keyrings/nodesource.gpg] https://deb.nodesource.com/node_20.x nodistro main" \
        > /etc/apt/sources.list.d/nodesource.list \
    && curl -fsSL https://cli.github.com/packages/githubcli-archive-keyring.gpg \
        | tee /usr/share/keyrings/githubcli-archive-keyring.gpg >/dev/null \
    && chmod go+r /usr/share/keyrings/githubcli-archive-keyring.gpg \
    && echo "deb [arch=$(dpkg --print-architecture) signed-by=/usr/share/keyrings/githubcli-archive-keyring.gpg] https://cli.github.com/packages stable main" \
        > /etc/apt/sources.list.d/github-cli.list \
    && curl -sLS https://packages.microsoft.com/keys/microsoft.asc \
        | gpg --dearmor -o /etc/apt/keyrings/microsoft.gpg \
    && chmod go+r /etc/apt/keyrings/microsoft.gpg \
    && echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/microsoft.gpg] https://packages.microsoft.com/repos/azure-cli/ bookworm main" \
        > /etc/apt/sources.list.d/azure-cli.list \
    && apt-get update && apt-get install -y --no-install-recommends nodejs gh azure-cli \
    && az extension add --name azure-devops --yes \
    && rm -rf /var/lib/apt/lists/*

# Install global Node CLIs: Claude Code + Playwright (lib + CLI driver).
#   PLAYWRIGHT_BROWSERS_PATH      — shared, non-home browser cache so the
#                                   non-root xianix user can find browsers
#                                   at runtime (default is ~/.cache).
#   PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=1 — at *runtime* (after USER xianix),
#                                   prevents tools from trying to re-download
#                                   browsers into the read-only /ms-playwright.
#   NODE_PATH=/usr/lib/node_modules — lets `require('playwright')` resolve from
#                                   any CWD; the web-app-tester skill probes
#                                   the cache and preflights the launch via
#                                   `node -e "require('playwright')..."` from
#                                   /workspace, which otherwise hits
#                                   MODULE_NOT_FOUND for global packages.
#   @playwright/cli is the playwright-cli driver the skill shells out to and
#   is intentionally distinct from the `playwright` Node library — both are
#   installed globally so the non-root user never tries `npm install -g` at
#   runtime (which would EACCES on /usr/lib/node_modules).
#   playwright is pinned to match the version the skill uses as a fallback.
# `playwright install --with-deps` runs apt-get internally to pull Chromium's
# system libs, so we clean apt lists again afterwards to keep the image lean.
ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright \
    PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=1 \
    NODE_PATH=/usr/lib/node_modules
RUN npm install -g \
        @anthropic-ai/claude-code \
        @playwright/cli \
        playwright@1.49.0 \
    && PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=0 playwright install --with-deps chromium \
    && chmod -R a+rX /ms-playwright \
    && rm -rf /var/lib/apt/lists/* /root/.npm

WORKDIR /workspace

COPY requirements.txt /workspace/requirements.txt
RUN pip install --no-cache-dir -r /workspace/requirements.txt

COPY execute_plugin.py /workspace/execute_plugin.py
COPY _common.sh /workspace/_common.sh
COPY prepare_repo.sh /workspace/prepare_repo.sh
COPY run_prompt.sh /workspace/run_prompt.sh
COPY generate_context.sh /workspace/generate_context.sh
COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh /workspace/prepare_repo.sh /workspace/run_prompt.sh /workspace/generate_context.sh

RUN groupadd -r xianix && useradd -r -g xianix -d /workspace xianix \
    && mkdir -p /workspace/repo \
    && chown -R xianix:xianix /workspace
USER xianix

ENTRYPOINT ["/entrypoint.sh"]
