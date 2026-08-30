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

# Todo el cuerpo va dentro de una función, llamada al final, para que bash lo
# parsee entero ANTES de ejecutar la primera línea. Sin esto, este script se
# auto-modifica en pleno vuelo: resolve-deploy-sha.sh (más abajo) hace un
# `git checkout --detach` sobre /opt/talveg, que es el propio checkout donde
# vive este fichero — y es LA COPIA EN DISCO del VPS la que se ejecuta (ver
# el comentario del "comando forzado" de arriba), no la del runner. bash lee
# el script por bloques con un offset de bytes, no lo bufferiza entero de
# entrada: si el fichero cambia de tamaño a mitad de ejecución, el offset
# sigue avanzando sobre el contenido NUEVO y puede ejecutar un híbrido sin
# sentido o cortar una sentencia por la mitad, sin que nada lo señale. Hoy
# solo se detectó un diagnóstico que tardó un despliegue entero en surtir
# efecto (el volcado de logs de más abajo, auditoría de colas 2026-08-30);
# con otro cambio podría ejecutar basura sobre el VPS. Definir una función
# fuerza a bash a leer hasta la llave de cierre antes de poder llamarla, así
# que en el momento en que `main` empieza a correr el fichero ya está
# parseado entero y da igual que cambie en disco a partir de ahí.
main() {

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

# Volcar logs y estado del contenedor app si el despliegue no llega a sano —
# auditoria de colas, 2026-08-30: un fallo de "is unhealthy" solo dejaba esa
# frase en el log de CI, sin la excepcion real que la causo. El job de
# despliegue no tiene forma de leerlos despues (el contenedor puede haberse
# reiniciado o el proceso ya no existir), asi que hay que capturarlos AQUI,
# en el momento del fallo, para que salgan por el mismo canal que ya llega al
# log de GitHub Actions — mismo criterio que el PR #372 aplico a los logs de
# E2E. compose ps primero: dice que contenedor exacto fallo (podria no ser
# "app") antes de intentar volcar el suyo.
volcar_diagnostico_si_falla() {
    local fichero_compose="$1" env_file="${2:-}"
    local args=(-f "$fichero_compose")
    [ -n "$env_file" ] && args+=(--env-file "$env_file")

    if ! docker compose "${args[@]}" up -d --build --wait --wait-timeout 180; then
        echo "=== Despliegue no llego a sano — estado de los contenedores ===" >&2
        docker compose "${args[@]}" ps >&2 || true
        for contenedor in $(docker compose "${args[@]}" ps --format '{{.Name}}' 2>/dev/null || true); do
            echo "=== docker logs --tail 300 ${contenedor} ===" >&2
            docker logs --tail 300 "$contenedor" >&2 || true
        done
        exit 1
    fi
}

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
    volcar_diagnostico_si_falla docker-compose.staging.yml .env.staging
    ;;
  produccion)
    volcar_diagnostico_si_falla docker-compose.produccion.yml
    ;;
esac

}

main "$@"
