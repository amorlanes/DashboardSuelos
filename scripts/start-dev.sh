#!/usr/bin/env bash
set -euo pipefail
script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
source "$script_dir/dotnet-env.sh"
cd "$script_dir/.."
bash "$script_dir/start-postgres.sh"
export ConnectionStrings__Servidor="${ConnectionStrings__Servidor:-Host=127.0.0.1;Port=54329;Database=constructora;Username=constructora}"
export Database__Initialize="${Database__Initialize:-true}"
export ASPNETCORE_URLS="${ASPNETCORE_URLS:-http://127.0.0.1:5080}"
exec dotnet run --project src/Constructora.Server --no-build --no-launch-profile
