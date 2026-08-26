#!/bin/bash
# Comando forzado para la clave de deploy de GitHub Actions (ver
# /root/.ssh/authorized_keys en el VPS, entrada con "command=") — el cliente
# SSH nunca elige el comando real que corre aquí, solo los dos tokens que
# llegan en $SSH_ORIGINAL_COMMAND: el entorno ("staging"|"produccion") y el
# SHA exacto a desplegar. Si esta clave privada se filtrara, el máximo que
# permite es forzar el redeploy de un commit que YA es ancestro real de main
# en GitHub (resolve-deploy-sha.sh lo exige) — nunca una shell arbitraria ni
# un commit fuera de esa historia.
#
# El SHA es obligatorio: nunca se despliega "lo que haya en main ahora
# mismo". Es el commit exacto que el workflow de GitHub Actions resolvió al
# dispararse, aunque la aprobación manual de producción tarde horas y main
# haya avanzado mientras tanto (incidente 2026-08-26 con
# `git merge --ff-only origin/main`, ver deploy/resolve-deploy-sha.sh).
set -euo pipefail

read -r ENTORNO SHA <<< "${SSH_ORIGINAL_COMMAND:-}"

case "$ENTORNO" in
  staging|produccion) ;;
  *)
    echo "Entorno no permitido: '${ENTORNO:-<vacio>}'" >&2
    exit 1
    ;;
esac

/opt/talveg/deploy/resolve-deploy-sha.sh /opt/talveg "${SHA:-}"

cd /opt/talveg/deploy/local
case "$ENTORNO" in
  staging)
    docker compose -f docker-compose.staging.yml --env-file .env.staging up -d --build
    ;;
  produccion)
    docker compose -f docker-compose.produccion.yml up -d --build
    ;;
esac
