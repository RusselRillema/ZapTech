#!/usr/bin/env bash
set -euo pipefail

if [[ "${1:-}" == "--help" || "${1:-}" == "-h" ]]; then
    printf '%s\n' \
        'Usage: bash Scripts/run-demos.sh [scenario ...] | --list' \
        'With no arguments, run all scenarios listed by the console application.' \
        'Requires Docker Compose and a configured .env copied from .env.docker.example.' \
        'DEMO_ENV_FILE selects another environment file; COMPOSE_PROJECT_NAME selects the stack.' \
        'Use a dedicated demo stack. Payments and database volumes are retained after the run.' \
        'Reversal/refund demonstrations await Task 1 Step 6.'
    exit 0
fi

repository_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
environment_file=${DEMO_ENV_FILE:-"$repository_root/.env"}
if [[ ! -f "$environment_file" ]]; then
    printf 'Environment file not found: %s\nCopy .env.docker.example to .env and configure its values first.\n' "$environment_file" >&2
    exit 2
fi
command -v docker >/dev/null || { printf 'Docker is required.\n' >&2; exit 2; }
docker compose version >/dev/null
compose=(docker compose --ansi never --project-directory "$repository_root" \
    --file "$repository_root/compose.yaml" --env-file "$environment_file")

"${compose[@]}" config --quiet
"${compose[@]}" build demo
listed=$("${compose[@]}" run --rm --no-deps -T demo --list)
available=()
while IFS= read -r scenario; do
    if [[ -n "$scenario" ]]; then
        available+=("$scenario")
    fi
done <<< "$listed"
if [[ ${#available[@]} -eq 0 ]]; then
    printf 'The console application returned no supported scenarios.\n' >&2
    exit 1
fi
if [[ $# -eq 1 && "$1" == "--list" ]]; then
    printf '%s\n' "${available[@]}"
    exit 0
fi

scenarios=("${available[@]}")
if [[ $# -gt 0 ]]; then
    scenarios=("$@")
    for requested in "${scenarios[@]}"; do
        found=false
        for supported in "${available[@]}"; do
            if [[ "$requested" == "$supported" ]]; then found=true; break; fi
        done
        if [[ "$found" == false ]]; then
            printf 'Unsupported scenario: %s. Use --list.\n' "$requested" >&2
            exit 2
        fi
    done
fi

"${compose[@]}" up --build --wait --wait-timeout 180 payments
for scenario in "${scenarios[@]}"; do
    printf '\nRunning %s\n' "$scenario"
    # A failure exits immediately with the runner's nonzero status.
    "${compose[@]}" run --rm --no-deps -T demo "$scenario"
done
printf '\nAll %s selected scenarios passed. Reversal/refund scenarios are deferred to step 6.\n' "${#scenarios[@]}"
printf 'The stack and audit data remain available. Stop containers with docker compose down when finished.\n'
