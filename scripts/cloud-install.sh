#!/usr/bin/env bash
set -euo pipefail
script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
source "$script_dir/dotnet-env.sh"
cd "$script_dir/.."
sdk_version=8.0.426
sdk_sha512=8141d01bdc53f604aed3bb661b604707a64209f8a947cd81bf95b4f518951593d77864069191f54ea5e84362a409da6b0f69074bf893b283d7e0cf93e2bf20a8
if [[ ! -x "$DOTNET_ROOT/dotnet" ]] || ! "$DOTNET_ROOT/dotnet" --list-sdks | grep -q "^$sdk_version "; then
    [[ "$(uname -m)" == x86_64 ]] || { echo 'Este instalador cloud requiere Linux x64.' >&2; exit 1; }
    sdk_archive="$(mktemp /tmp/constructora-sdk.XXXXXX.tar.gz)"
    trap 'rm -f "$sdk_archive"' EXIT
    curl --fail --show-error --location --retry 3 \
        "https://builds.dotnet.microsoft.com/dotnet/Sdk/$sdk_version/dotnet-sdk-$sdk_version-linux-x64.tar.gz" \
        --output "$sdk_archive"
    printf '%s  %s\n' "$sdk_sha512" "$sdk_archive" | sha512sum --check --status
    mkdir -p "$DOTNET_ROOT"
    tar -xzf "$sdk_archive" -C "$DOTNET_ROOT"
fi
dotnet restore Constructora.sln --locked-mode
dotnet build Constructora.sln --no-restore --configuration Debug --maxcpucount:3
docker pull postgres@sha256:0ea6700a3b4f0ae6ce746519073558aed4d88a79d8d07622a9a644946c7319c4
