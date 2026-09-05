#!/usr/bin/env bash
# REC-196 — desechable, validación en WSL2. Espera a que app esté healthy y
# confirma /salud desde dentro del propio contenedor.
set -uo pipefail
cd "$(dirname "$0")"

for i in $(seq 1 20); do
  st=$(docker inspect --format '{{.State.Health.Status}}' caemanager-app 2>&1)
  echo "intento $i: $st"
  if [ "$st" = "healthy" ]; then break; fi
  sleep 3
done

docker exec caemanager-app curl -fsS http://localhost:8080/salud
echo "  <- salud exit $?"
