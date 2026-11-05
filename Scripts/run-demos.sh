#!/usr/bin/env bash
set -euo pipefail

include_adjustments=false
if [[ "${1:-}" == "--include-adjustments" ]]; then
    include_adjustments=true
    shift
fi

if [[ "${1:-}" == "--help" || "${1:-}" == "-h" ]]; then
    printf '%s\n' \
        'Usage: bash Scripts/run-demos.sh [--include-adjustments] [scenario ...] | --list' \
        'With no arguments, run the original six scenarios listed by the console application.' \
        'Requires Docker Compose and a .env copied from the ready-to-run .env.docker.example.' \
        'DEMO_ENV_FILE selects another environment file; COMPOSE_PROJECT_NAME selects the stack.' \
        'Use a dedicated demo stack. Payments and database volumes are retained after the run.' \
        '--include-adjustments adds six full reversal/refund checks, requiring published step 6 code and SQL.' \
        'Partial/multiple-refund and partial-reversal checks remain unsupported by the stub controls.'
    exit 0
fi

repository_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
environment_file=${DEMO_ENV_FILE:-"$repository_root/.env"}
if [[ ! -f "$environment_file" ]]; then
    printf 'Environment file not found: %s\nCopy .env.docker.example to .env first; no editing is required for local Docker use.\n' "$environment_file" >&2
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
default_scenarios=("${available[@]}")
adjustments=$("${compose[@]}" run --rm --no-deps -T demo --list-adjustments)
while IFS= read -r scenario; do
    if [[ -n "$scenario" ]]; then
        available+=("$scenario")
        if [[ "$include_adjustments" == true ]]; then default_scenarios+=("$scenario"); fi
    fi
done <<< "$adjustments"
if [[ $# -eq 1 && "$1" == "--list" ]]; then
    printf '%s\n' "${default_scenarios[@]}"
    exit 0
fi

scenarios=("${default_scenarios[@]}")
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
printf '\nAll %s selected scenarios passed.\n' "${#scenarios[@]}"
printf 'Adjustment scenarios require step 6; partial-adjustment and cutoff scenarios remain unsupported.\n'
printf 'The stack and audit data remain available. Stop containers with docker compose down when finished.\n'
