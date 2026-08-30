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

bash /opt/talveg/deploy/resolve-deploy-sha.sh /opt/talveg "${SHA:-}"

# Antes de construir: mantener el disco por debajo del umbral. Un build en un
# disco lleno no falla de forma legible —da errores de NuGet que no mencionan
# el disco— y, peor, deja a PostgreSQL sin poder escribir, lo que tumba
# produccion aunque nadie haya desplegado nada. Paso el 2026-08-26 y otra vez
# el 2026-08-29, con 23 GB de cache de build sin usar acumulada porque este
# guion no la retiraba nunca.
#
# Si tras liberar el disco sigue critico, liberar-disco.sh corta aqui: mejor
# un despliegue que no arranca con un mensaje claro que uno que se rompe a
# medias y se lleva la base de datos por delante.
bash /opt/talveg/deploy/liberar-disco.sh

cd /opt/talveg/deploy/local
case "$ENTORNO" in
  staging)
# --wait: `up -d` a secas devuelve en cuanto los contenedores ARRANCAN, no
# cuando estan sanos. El 2026-08-29 el despliegue de 669e3108 reporto
# "success" en staging y produccion mientras la aplicacion de staging estaba
# en `Restarting (139)` en bucle: el CD dio por bueno un despliegue roto y el
# fallo se descubrio horas despues, por otra via. Con --wait, compose espera a
# que los servicios con healthcheck (app y db) esten healthy y el resto
# running, y falla si no llegan.
#
# 180 s con holgura: el arranque de la aplicacion incluye aplicar migraciones
# pendientes, que tras varias semanas pueden ser muchas.
    docker compose -f docker-compose.staging.yml --env-file .env.staging up -d --build --wait --wait-timeout 180
    ;;
  produccion)
    docker compose -f docker-compose.produccion.yml up -d --build --wait --wait-timeout 180
    ;;
esac
