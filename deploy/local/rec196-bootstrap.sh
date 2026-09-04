#!/usr/bin/env bash
# REC-196 — desechable, no se commitea el uso; script auxiliar de validación
# en WSL2. Espera a que db esté healthy y aplica el bootstrap de roles.
set -euo pipefail
cd "$(dirname "$0")"

for i in $(seq 1 15); do
  st=$(docker inspect --format '{{.State.Health.Status}}' caemanager-db 2>&1 || echo "sin-contenedor")
  echo "intento $i: $st"
  if [ "$st" = "healthy" ]; then break; fi
  sleep 2
done

docker compose -f docker-compose.produccion.yml exec -T db \
  psql -U postgres -d postgres -v ON_ERROR_STOP=1 < ../bootstrap/roles-de-cluster.sql
