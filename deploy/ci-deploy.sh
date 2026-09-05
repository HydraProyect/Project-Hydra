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
#
# LIMITACIÓN CONOCIDA, no un defecto: un cambio a ESTE fichero tarda un
# despliegue en surtir efecto. SSH ejecuta la copia que ya hay en disco en
# /opt/talveg antes de que el checkout de más abajo la actualice — el
# guion que orquesta el checkout tiene que existir ya, coherente, antes de
# poder correr el checkout que lo trae al día. Así que el primer despliegue
# tras tocar ci-deploy.sh sigue ejecutando la versión ANTERIOR (completa y
# sin corromper, gracias al main() de más abajo — eso es lo que este
# fichero sí garantiza) y es el SEGUNDO despliegue el que ya corre con el
# cambio. Visto de verdad: el diagnóstico que #376 añadió aquí no apareció
# en el primer run tras mergearse, pese a estar ya en main. Cerrarlo del
# todo exigiría que el comando forzado de authorized_keys hiciera un
# bootstrap mínimo (fetch + checkout) y luego un exec de la copia ya
# actualizada — eso es configuración del VPS, no código versionado en este
# repositorio, así que queda pendiente de decisión.
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

# Cerrojo exclusivo de TODO el despliegue (REC-199, revisión adversarial del
# handoff HO-199-01) — no solo del build. staging y producción son DOS
# proyectos Compose sobre el MISMO checkout /opt/talveg (`context: ../..` en
# ambos docker-compose.*.yml) y el MISMO daemon Docker. El `needs: staging`
# de .github/workflows/deploy.yml solo serializa staging y producción DENTRO
# de un mismo run — no protege contra un run B (staging de un push más
# reciente) corriendo a la vez que el `produccion` de un run A cuya
# aprobación manual tardó horas en llegar (el propio script ya asume esa
# demora, ver el comentario de más abajo sobre el SHA). Sin cerrojo, dos
# `resolve-deploy-sha.sh` concurrentes se pisan el `git checkout --detach`
# del mismo /opt/talveg (mezcla de dos commits a medias, sin relación con la
# memoria) y dos `docker compose build -m 2560m` concurrentes suman hasta
# 5120m de techo en una máquina de 4 GB — exactamente el mismo problema que
# este incremento cierra para UN build, reabierto por la suma de dos. Un
# timeout de 10 min falla con un mensaje claro en vez de colgar el job de
# GitHub Actions indefinidamente si el despliegue que tiene el cerrojo
# nunca lo suelta.
exec 9>/opt/talveg/deploy/.ci-deploy.lock
if ! flock -w 600 9; then
  echo "No se pudo obtener el cerrojo de despliegue en 10 min — otro despliegue (staging o producción) sigue en marcha sobre /opt/talveg." >&2
  exit 1
fi

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

# Techo de memoria del PASO DE BUILD (REC-199, apagón de producción
# 2026-09-04) — no confundir con LIMITE_MEMORIA_APP (compose, cgroup del
# contenedor "app" ya corriendo): esto acota el `dotnet publish` que corre
# DENTRO de `docker build`, sin cgroup propio hasta este cambio. Ese build
# corre en el mismo VPS que sigue sirviendo tráfico, y el 2026-09-04 su
# proceso de compilación (VBCSCompiler) llegó a 2,1 GB de anon-rss sin techo
# — el kernel acabó eligiendo víctima por su cuenta (OOM: mató systemd y
# luego VBCSCompiler) en una máquina de 4 GB. Medido en un banco de pruebas
# capado a 2 vCPU/4 GB/sin swap (aproximación al CX23 real, REC-196): con
# 700m el build muere limpio dentro de su propio cgroup ("csc" exit 137,
# `docker compose build` sale con código de error) sin que el resto del
# stack pierda un solo health-check; con 2560m el mismo build (sin tocar el
# código) completa con normalidad. 2560m dado aquí: deja margen sobre el
# build real y sigue muy por debajo de los ~3 GB que quedan libres en la
# máquina con app+db+caddy+seq ya arriba.
LIMITE_MEMORIA_BUILD="2560m"

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

    # Build y arranque van en DOS pasos, no en el `up -d --build` de antes:
    # `--memory` de `docker compose build` no existe bajo BuildKit ("Not
    # supported by BuildKit", medido en su propio --help) — solo el builder
    # clásico lo aplica de verdad como cgroup del contenedor de build.
    # DOCKER_BUILDKIT=0 lo fuerza explícitamente en vez de confiar en cuál
    # sea el motor por defecto de esta instalación del VPS.
    if ! DOCKER_BUILDKIT=0 docker compose "${args[@]}" build -m "$LIMITE_MEMORIA_BUILD"; then
        echo "=== Build no completó dentro del techo de memoria (LIMITE_MEMORIA_BUILD=$LIMITE_MEMORIA_BUILD) — contenido a su propio cgroup, el resto del stack sigue sirviendo ===" >&2
        exit 1
    fi

    if ! docker compose "${args[@]}" up -d --wait --wait-timeout 180; then
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
