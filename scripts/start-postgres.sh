#!/usr/bin/env bash
set -euo pipefail
script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
source "$script_dir/dotnet-env.sh"
pg_data="$CONSTRUCTORA_TOOLS_ROOT/.constructora/postgres"
mkdir -p "$pg_data"
if docker container inspect constructora-postgres >/dev/null 2>&1; then
    docker start constructora-postgres >/dev/null
else
    docker run --detach --name constructora-postgres --restart no \
        --publish 127.0.0.1:54329:5432 \
        --env POSTGRES_USER=constructora --env POSTGRES_DB=constructora \
        --env POSTGRES_HOST_AUTH_METHOD=trust \
        --mount "type=bind,src=$pg_data,dst=/var/lib/postgresql/data" \
        postgres@sha256:0ea6700a3b4f0ae6ce746519073558aed4d88a79d8d07622a9a644946c7319c4 >/dev/null
fi
for ((attempt=0; attempt<30; attempt++)); do
    if docker exec constructora-postgres pg_isready -U constructora -d constructora >/dev/null 2>&1; then
        echo 'PostgreSQL disponible en 127.0.0.1:54329.'
        exit 0
    fi
    sleep 1
done
docker logs --tail 50 constructora-postgres >&2
echo 'PostgreSQL no está preparado.' >&2
exit 1
