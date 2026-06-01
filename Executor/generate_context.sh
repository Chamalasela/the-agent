#!/usr/bin/env bash
# Generates and caches a compact, persistent repository context so the agent does not
# re-explore the codebase from a cold start on every run (the single biggest token sink:
# each execution gets a fresh worktree with no memory of prior runs).
#
# Two artifacts are produced DETERMINISTICALLY from the checked-out worktree — no
# LLM/token cost to build them:
#
#   * CLAUDE.md   — project orientation (overview, detected stack/commands, top-level
#                   layout, pointer to the symbol map). Claude Code auto-loads CLAUDE.md
#                   from the working directory, so this trims the discovery turns Claude
#                   would otherwise spend grepping and listing the tree.
#   * .xianix/repomap.txt — a compact file→symbol map (functions/classes per file) that
#                   replaces a storm of Read/Grep calls with one cheap lookup.
#
# Optionally (host opt-in via XIANIX_CONTEXT_LLM) a HYBRID step appends an LLM-authored
# "Architecture & conventions" narrative to CLAUDE.md — the deterministic facts give Claude
# the *what/where* cheaply, while a single budget- and turn-capped Haiku pass adds the *why*
# (how the pieces fit, conventions, gotchas) that can't be extracted with grep. It runs only
# on a cache miss (i.e. once per HEAD change), so its cost is amortised across every later run
# that reuses the cache, and it never runs when the repo ships its own CLAUDE.md.
#
# All artifacts are cached on the tenant volume (inside the bare repo dir, which is the only
# persistent location) keyed by the worktree's commit sha, so they are regenerated only
# when HEAD moves — matching "regenerate when the default branch changes".
#
# Injection rules (never clobber tenant content):
#   * The generated CLAUDE.md is copied into the worktree ONLY when the repo doesn't already
#     ship its own CLAUDE.md — a tenant-authored CLAUDE.md always wins, and the (expensive)
#     LLM narrative pass is skipped entirely in that case.
#   * Injected files are added to the worktree's git exclude list so they never show up in a
#     plugin's `git status` / `git diff` (e.g. PR-review diffs stay clean).
#
# Usage: generate_context.sh <worktree_dir> <cache_dir>
# Best-effort: any failure (including the LLM pass) logs a warning and exits 0 — context is an
# optimisation, never a precondition for the run.
set -uo pipefail

log() { echo "[context] $*" >&2; }

WT="${1:-}"
CACHE_DIR="${2:-}"

if [ -z "${WT}" ] || [ ! -d "${WT}" ]; then
    log "WARNING: worktree '${WT}' missing — skipping context generation."
    exit 0
fi
if [ -z "${CACHE_DIR}" ]; then
    log "WARNING: no cache dir provided — skipping context generation."
    exit 0
fi

MAX_REPOMAP_LINES=2000          # cap the symbol map so it can never balloon the context
MAX_OVERVIEW_LINES=40           # README excerpt length
MAX_NARRATIVE_LINES=200         # hard cap on the LLM narrative so a runaway can't bloat CLAUDE.md
README_MD="${CACHE_DIR}/CLAUDE.md"
REPOMAP="${CACHE_DIR}/repomap.txt"
SHA_FILE="${CACHE_DIR}/sha"

# ── Optional hybrid LLM narrative (host opt-in) ──────────────────────────────
LLM_ENABLED="$(printf '%s' "${XIANIX_CONTEXT_LLM:-}" | tr '[:upper:]' '[:lower:]')"
case "${LLM_ENABLED}" in 1|true|yes|on) LLM_ENABLED=1 ;; *) LLM_ENABLED=0 ;; esac
LLM_MODEL="${XIANIX_CONTEXT_LLM_MODEL:-claude-haiku-4-5}"
LLM_MAX_TURNS="${XIANIX_CONTEXT_LLM_MAX_TURNS:-15}"
LLM_TIMEOUT="${XIANIX_CONTEXT_LLM_TIMEOUT:-180}"

mkdir -p "${CACHE_DIR}" 2>/dev/null || { log "WARNING: cannot create cache dir '${CACHE_DIR}'."; exit 0; }

current_sha="$(git -C "${WT}" rev-parse HEAD 2>/dev/null || echo "nogit")"

# Detect a repo-owned CLAUDE.md up front (covers regular files, dirs, and symlinks — even
# broken ones, which `-e` alone would miss). When the tenant ships their own, we never inject
# AND we skip the expensive LLM pass: there is no point spending tokens on content that will
# never reach the worktree.
repo_has_own_claude=0
if [ -e "${WT}/CLAUDE.md" ] || [ -L "${WT}/CLAUDE.md" ]; then
    repo_has_own_claude=1
fi

# ── Detect language / build & test commands from marker files ────────────────
detect_stack() {
    local found=0
    _line() { printf '* %s\n' "$1"; found=1; }
    [ -f "${WT}/package.json" ]      && _line "Node.js — \`npm install\`, \`npm test\`, \`npm run build\` (see package.json scripts)."
    [ -f "${WT}/pnpm-lock.yaml" ]    && _line "pnpm workspace — \`pnpm install\`, \`pnpm test\`."
    [ -f "${WT}/requirements.txt" ]  && _line "Python — \`pip install -r requirements.txt\`; tests via \`pytest\`."
    [ -f "${WT}/pyproject.toml" ]    && _line "Python (pyproject) — \`pip install -e .\` / \`uv sync\`; tests via \`pytest\`."
    ls "${WT}"/*.sln >/dev/null 2>&1 && _line ".NET solution — \`dotnet build\`, \`dotnet test\`."
    ls "${WT}"/**/*.csproj >/dev/null 2>&1 && _line ".NET project(s) present — \`dotnet build\`, \`dotnet test\`."
    [ -f "${WT}/go.mod" ]            && _line "Go — \`go build ./...\`, \`go test ./...\`."
    [ -f "${WT}/Cargo.toml" ]        && _line "Rust — \`cargo build\`, \`cargo test\`."
    [ -f "${WT}/pom.xml" ]           && _line "Java (Maven) — \`mvn -q verify\`."
    { [ -f "${WT}/build.gradle" ] || [ -f "${WT}/build.gradle.kts" ]; } && _line "Java/Kotlin (Gradle) — \`./gradlew build test\`."
    [ -f "${WT}/Gemfile" ]           && _line "Ruby — \`bundle install\`, \`bundle exec rspec\`."
    [ -f "${WT}/Makefile" ]          && _line "Makefile present — inspect targets with \`make help\` / \`grep -E '^[a-z].*:' Makefile\`."
    [ "${found}" -eq 0 ] && printf '* No standard build/test marker files detected — inspect the repo root.\n'
}

# ── Compact top-level layout (depth 2, noise dirs pruned) ────────────────────
detect_layout() {
    ( cd "${WT}" && find . -maxdepth 2 -type d \
        \( -name .git -o -name node_modules -o -name dist -o -name build \
           -o -name bin -o -name obj -o -name .venv -o -name venv \
           -o -name vendor -o -name target -o -name .next -o -name .idea \) -prune -o \
        -type d -print 2>/dev/null \
      | sed 's|^\./||' | grep -v '^\.$' | sort | head -n 60 )
}

# ── README excerpt (first non-empty lines) ───────────────────────────────────
detect_overview() {
    local readme
    for readme in README.md README.rst README README.txt readme.md; do
        if [ -f "${WT}/${readme}" ]; then
            grep -v '^[[:space:]]*$' "${WT}/${readme}" | head -n "${MAX_OVERVIEW_LINES}"
            return 0
        fi
    done
    printf '_No README found at the repository root._\n'
}

# ── Symbol map via ctags, with a source-file listing as a robust fallback ────
# universal-ctags (shipped in the image) supports `-x -R --exclude`; if a different ctags
# variant is on PATH and produces nothing, or ctags is absent, we degrade to a file inventory
# so the agent at least gets the source layout instead of an empty map.
generate_repomap() {
    local symbols=""
    if command -v ctags >/dev/null 2>&1; then
        # `-x` emits a stable cross-reference: "name kind line file pattern".
        # Reformat to "file: name (kind)" so the model gets a path-anchored symbol map.
        symbols="$( ( cd "${WT}" && ctags -x -R \
            --exclude=.git --exclude=node_modules --exclude=dist --exclude=build \
            --exclude=bin --exclude=obj --exclude=.venv --exclude=venv \
            --exclude=vendor --exclude=target --exclude=.next . 2>/dev/null ) \
          | awk 'NF>=4 {print $4": "$1" ("$2")"}' \
          | sort -u | head -n "${MAX_REPOMAP_LINES}" )"
    fi

    if [ -n "${symbols}" ]; then
        printf '%s\n' "${symbols}"
        return 0
    fi

    # Fallback: enumerate source files so the model at least has the file inventory.
    ( cd "${WT}" && find . -type f \
        \( -name '*.ts' -o -name '*.tsx' -o -name '*.js' -o -name '*.jsx' \
           -o -name '*.py' -o -name '*.cs' -o -name '*.go' -o -name '*.rs' \
           -o -name '*.java' -o -name '*.kt' -o -name '*.rb' -o -name '*.php' \) \
        2>/dev/null | sed 's|^\./||' | sort | head -n "${MAX_REPOMAP_LINES}" )
}

# ── Optional: LLM-authored architecture narrative (best-effort, capped) ──────
# Runs the Claude Code CLI headlessly with a cheap model and hard turn/time caps, then prints
# a concise markdown narrative on stdout. Every failure mode (missing CLI, no API key, timeout,
# non-zero exit, empty output) returns non-zero so the caller silently falls back to the
# deterministic-only CLAUDE.md.
generate_narrative() {
    if ! command -v claude >/dev/null 2>&1; then
        log "claude CLI not on PATH — skipping LLM narrative."; return 1
    fi
    if [ -z "${ANTHROPIC_API_KEY:-}" ]; then
        log "ANTHROPIC_API_KEY not set — skipping LLM narrative."; return 1
    fi

    local prompt out timeout_bin=""
    command -v timeout >/dev/null 2>&1 && timeout_bin="timeout ${LLM_TIMEOUT}"

    prompt="$(cat <<'PROMPT'
You are documenting THIS repository for another AI coding agent that will work in it later.
Explore only as much as needed (a handful of key files) and produce a single concise markdown
section, <= 350 words, titled exactly:

## Architecture & conventions

Cover: the high-level architecture and how the main components fit together; important
conventions (naming, layering, error handling, config); and any non-obvious gotchas a new
contributor would trip over. Do NOT restate build/test commands, file trees, or symbol lists —
those are already documented elsewhere. Output ONLY the markdown section, no preamble.
PROMPT
)"

    # shellcheck disable=SC2086
    out="$( ( cd "${WT}" && ${timeout_bin} claude -p "${prompt}" \
        --model "${LLM_MODEL}" \
        --max-turns "${LLM_MAX_TURNS}" \
        --permission-mode bypassPermissions \
        --output-format text ) 2>/dev/null )" || {
        log "LLM narrative generation failed or timed out — using deterministic context only."
        return 1
    }

    out="$(printf '%s\n' "${out}" | head -n "${MAX_NARRATIVE_LINES}")"
    [ -z "${out//[[:space:]]/}" ] && { log "LLM narrative came back empty — skipping."; return 1; }
    printf '%s\n' "${out}"
}

# ── Reuse cache when HEAD hasn't moved, else regenerate ──────────────────────
cached_sha="$(cat "${SHA_FILE}" 2>/dev/null || echo "")"
if [ "${cached_sha}" = "${current_sha}" ] && [ -s "${README_MD}" ]; then
    log "Reusing cached context for HEAD ${current_sha} (cache hit)."
else
    log "Generating context for HEAD ${current_sha} (cache miss)."

    {
        printf '# Repository Context\n\n'
        printf '_Auto-generated by the Xianix Executor — a deterministic, cached snapshot to help you\n'
        printf 'orient quickly. Prefer this over re-scanning the whole tree. Regenerated only when the\n'
        printf 'branch HEAD changes._\n\n'
        printf '## Overview\n\n'
        detect_overview
        printf '\n## Detected stack & commands\n\n'
        detect_stack
        printf '\n## Top-level layout\n\n```\n'
        detect_layout
        printf '```\n\n## Symbol map\n\n'
        printf 'A compact file-to-symbol map lives at `.xianix/repomap.txt`. Read it to locate code by\n'
        printf 'symbol before grepping — it lists the functions/classes defined in each source file.\n'
    } > "${README_MD}.tmp" 2>/dev/null && mv "${README_MD}.tmp" "${README_MD}"

    generate_repomap > "${REPOMAP}.tmp" 2>/dev/null || true
    if [ -s "${REPOMAP}.tmp" ]; then mv "${REPOMAP}.tmp" "${REPOMAP}"; else rm -f "${REPOMAP}.tmp"; fi

    # Hybrid pass: append an LLM narrative to the freshly-built CLAUDE.md. Gated so it only runs
    # when (a) the host opted in, and (b) the repo doesn't ship its own CLAUDE.md (otherwise the
    # output would be discarded at injection time — don't pay for it). Best-effort: on any
    # failure the deterministic CLAUDE.md stands as-is.
    if [ "${LLM_ENABLED}" = "1" ] && [ "${repo_has_own_claude}" = "0" ] && [ -s "${README_MD}" ]; then
        log "Generating LLM narrative (model=${LLM_MODEL}, max_turns=${LLM_MAX_TURNS}, timeout=${LLM_TIMEOUT}s)..."
        narrative="$(generate_narrative || true)"
        if [ -n "${narrative}" ]; then
            { printf '\n'; printf '%s\n' "${narrative}"; } >> "${README_MD}" 2>/dev/null \
                && log "Appended LLM narrative to cached CLAUDE.md." || true
        fi
    elif [ "${LLM_ENABLED}" = "1" ] && [ "${repo_has_own_claude}" = "1" ]; then
        log "Repo ships its own CLAUDE.md — skipping LLM narrative (would not be injected)."
    fi

    printf '%s' "${current_sha}" > "${SHA_FILE}" 2>/dev/null || true
fi

# ── Inject into the worktree (respect tenant content; keep git status clean) ─
# Resolve the git dir absolutely so the exclude write doesn't depend on the caller's cwd, and
# so it targets the worktree-specific exclude file when WT is a linked worktree.
git_dir="$(git -C "${WT}" rev-parse --absolute-git-dir 2>/dev/null || echo "")"
add_exclude() {
    [ -z "${git_dir}" ] && return 0
    mkdir -p "${git_dir}/info" 2>/dev/null || true
    local ex="${git_dir}/info/exclude"
    grep -qxF "$1" "${ex}" 2>/dev/null || printf '%s\n' "$1" >> "${ex}" 2>/dev/null || true
}

if [ -s "${REPOMAP}" ]; then
    mkdir -p "${WT}/.xianix" 2>/dev/null || true
    cp "${REPOMAP}" "${WT}/.xianix/repomap.txt" 2>/dev/null || true
    add_exclude ".xianix/"
fi

if [ -s "${README_MD}" ]; then
    if [ "${repo_has_own_claude}" = "1" ]; then
        log "Repo already ships a CLAUDE.md — leaving it untouched (tenant content wins)."
    elif cp "${README_MD}" "${WT}/CLAUDE.md" 2>/dev/null; then
        add_exclude "CLAUDE.md"
        log "Injected generated CLAUDE.md into the worktree."
    else
        log "WARNING: could not write CLAUDE.md into the worktree — continuing without it."
    fi
fi

log "Context preparation complete."
exit 0
