#!/usr/bin/env bash
set -euo pipefail
script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
source "$script_dir/dotnet-env.sh"
cd "$script_dir/.."
bash "$script_dir/start-postgres.sh"
dotnet build Constructora.sln --no-restore --maxcpucount:3
dotnet test Constructora.sln --no-build --no-restore --logger 'console;verbosity=normal'
