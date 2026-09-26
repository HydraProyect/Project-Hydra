#!/bin/bash
# Relevo sin corte de la aplicación (P1-F2): arranca la versión nueva junto a
# la vieja, conmuta Caddy cuando la nueva está sana y deja drenar la vieja.
#
# Por qué: recrear el contenedor de la app en cada despliegue mataba el proceso
# que guarda en memoria los circuitos Blazor Server de todos los usuarios; su
# reconexión llegaba a un proceso que no los conocía y la interfaz mostraba
# «sesión perdida» (ReconnectModal), con los cambios sin guardar perdidos, y
# durante el arranque de la nueva Caddy respondía 502.
#
# Modelo:
#   - Dos ranuras por entorno en el compose, app-azul y app-verde (perfil
#     "ranura"), con container_name <prefijo>-azul / <prefijo>-verde.
#   - Un fichero por entorno en $DIR_RANURAS, importado por el Caddyfile dentro
#     de su reverse_proxy, con UNA línea: `to <activa>:8080 [<saliente>:8080]`.
#     Es el único estado del relevo: la primera dirección es la ranura activa.
#   - El Caddyfile fija afinidad por cookie (quien tenía la página abierta sigue
#     en la saliente) y stream_close_delay (una recarga no corta los WebSocket
#     abiertos). Ver su cabecera, con lo medido.
#
# Órdenes:
#   relevo-app.sh desplegar <staging|produccion> <sha>
#       Con la imagen caemanager:<sha> ya cargada y el cerrojo de despliegue
#       YA TOMADO por quien llama (ci-deploy.sh, volver-atras.sh):
#         1. Saca de Caddy y para las salientes de un relevo anterior que sigan
#            en el fichero (la libre si aún drenaba, o una cuyo drenaje murió).
#         2. `docker compose up -d --wait --no-build` de los servicios sin
#            perfil (db, migrador, seq y, en producción, caddy) y de la ranura
#            libre, con IMAGEN_TAG=<sha>. El migrador corre aquí, con la
#            ranura activa sirviendo: expand/contract (runbook de despliegue).
#         3. Sana la nueva, reescribe el fichero de ranuras con la nueva
#            primero y la activa detrás, y recarga Caddy. Si la recarga falla,
#            restaura el fichero, para la nueva y sale con 1: sigue sirviendo
#            la de antes.
#         4. Lanza el drenaje de la anterior (systemd-run si existe).
#       Sale con 0 solo si la nueva ranura quedó sirviendo.
#   relevo-app.sh activa <staging|produccion>
#       Imprime el container_name de la ranura activa (o del contenedor
#       anterior a P1-F2, si aún no ha habido relevo). Sale con 1 si no hay.
#   relevo-app.sh drenar <staging|produccion> <contenedor> <id> <max_s>
#       Lo lanza `desplegar`; no se llama a mano. Cada $DRENAJE_INTERVALO s
#       cuenta las conexiones TCP establecidas al 8080 del contenedor (las de
#       Caddy: circuitos abiertos y keep-alive), y cuando lleva dos lecturas
#       seguidas en 0 —o vence <max_s>— toma el cerrojo de despliegue, lo quita
#       del fichero de ranuras, recarga Caddy y lo para. Nunca toca un
#       contenedor que ya no sea el mismo (<id>) ni la ranura activa.
#
# Transición desde el despliegue de un solo contenedor (anterior a P1-F2): el
# contenedor <prefijo> (caemanager-app, caemanager-staging-app) se trata como
# activa mientras no haya fichero de ranuras, drena como cualquier saliente y
# al final se borra (ya no pertenece a ningún servicio del compose). Si el
# contenedor de Caddy no tiene aún el montaje de $DIR_RANURAS, se recrea desde
# docker-compose.produccion.yml. Ese primer relevo aún corta a quien tuviera un
# circuito abierto: sus navegadores no llevan la cookie de afinidad.
#
# Variables (con sus valores reales por defecto; los tests las sustituyen):
# RAIZ_DESPLIEGUE, DIR_RANURAS, CONTENEDOR_CADDY, DRENAJE_MAX_PRODUCCION,
# DRENAJE_MAX_STAGING, DRENAJE_INTERVALO, RELEVO_ESPERA_SALUD.

set -euo pipefail

RAIZ_DESPLIEGUE="${RAIZ_DESPLIEGUE:-/opt/talveg}"
DIR_RANURAS="${DIR_RANURAS:-/var/lib/talveg/caddy-ranuras}"
CONTENEDOR_CADDY="${CONTENEDOR_CADDY:-caemanager-caddy}"
# Máximo de drenaje. Producción: lo bastante para terminar lo que se tenía
# entre manos; staging: poco, comparte la máquina (4 GB) con producción y
# durante el drenaje hay dos contenedores de la app. El Caddyfile fija
# stream_close_delay por encima del máximo de producción: si se sube este, hay
# que subir aquel.
DRENAJE_MAX_PRODUCCION="${DRENAJE_MAX_PRODUCCION:-1800}"
DRENAJE_MAX_STAGING="${DRENAJE_MAX_STAGING:-300}"
DRENAJE_INTERVALO="${DRENAJE_INTERVALO:-30}"
RELEVO_ESPERA_SALUD="${RELEVO_ESPERA_SALUD:-180}"
GUION_RELEVO="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/$(basename "${BASH_SOURCE[0]}")"

es_entorno() { [ "${1:-}" = staging ] || [ "${1:-}" = produccion ]; }
prefijo() { [ "$1" = staging ] && echo caemanager-staging-app || echo caemanager-app; }
fichero_ranuras() { echo "$DIR_RANURAS/$1.caddy"; }
max_drenaje() { [ "$1" = staging ] && echo "$DRENAJE_MAX_STAGING" || echo "$DRENAJE_MAX_PRODUCCION"; }
unidad_drenaje() { echo "talveg-drenaje-$1"; }

en_marcha() { [ "$(docker inspect -f '{{.State.Running}}' "$1" 2>/dev/null || true)" = "true" ]; }
# En marcha y, si tiene healthcheck, healthy. Es lo que se exige a la ranura
# que se queda sirviendo antes de retirar otra.
sana() {
    local salud
    en_marcha "$1" || return 1
    salud="$(docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{end}}' "$1" 2>/dev/null || true)"
    [ -z "$salud" ] || [ "$salud" = healthy ]
}
id_de() { docker inspect -f '{{.Id}}' "$1" 2>/dev/null || true; }

# Direcciones del fichero de ranuras, sin el puerto, una por línea (la activa
# primero). Vacío si no hay fichero.
ranuras_en_fichero() {
    local f
    f="$(fichero_ranuras "$1")"
    [ -f "$f" ] || return 0
    awk '$1 == "to" { for (i = 2; i <= NF; i++) { sub(/:8080$/, "", $i); print $i }; exit }' "$f"
}

activa() {
    local primera
    primera="$(ranuras_en_fichero "$1" | awk 'NR == 1')"
    if [ -n "$primera" ]; then
        echo "$primera"
        return 0
    fi
    # Sin fichero: despliegue anterior a P1-F2, un solo contenedor.
    if en_marcha "$(prefijo "$1")"; then
        prefijo "$1"
        return 0
    fi
    return 1
}

# escribir_ranuras ENTORNO ACTIVA [SALIENTE] — escritura atómica (mismo
# directorio + mv), porque Caddy puede leerlo en cualquier momento.
escribir_ranuras() {
    local entorno="$1" activa_="$2" saliente="${3:-}" f tmp linea
    f="$(fichero_ranuras "$entorno")"
    mkdir -p "$DIR_RANURAS"
    linea="to ${activa_}:8080"
    [ -n "$saliente" ] && linea="$linea ${saliente}:8080"
    tmp="$(mktemp "$DIR_RANURAS/.$entorno.XXXXXX")"
    printf '# Generado por deploy/relevo-app.sh (P1-F2): la primera dirección es la ranura activa.\n%s\n' "$linea" > "$tmp"
    chmod 644 "$tmp"
    mv -f "$tmp" "$f"
}

# El Caddyfile importa los ficheros de los DOS entornos: si falta uno, Caddy no
# carga la configuración. Se crea el que falte con lo que sirva ese entorno
# ahora (o el nombre anterior a P1-F2, que da 502 como hoy si no corre).
asegurar_ficheros_ranuras() {
    local e a
    for e in produccion staging; do
        [ -f "$(fichero_ranuras "$e")" ] && continue
        a="$(activa "$e" || prefijo "$e")"
        escribir_ranuras "$e" "$a"
        echo "Fichero de ranuras de $e creado: $a"
    done
}

# Sin `| grep -q`: con pipefail, grep -q sale en la primera coincidencia y el
# SIGPIPE del productor puede convertir un «sí» en fallo (141).
caddy_tiene_montaje() {
    local destinos
    destinos=" $(docker inspect -f '{{range .Mounts}}{{.Destination}} {{end}}' "$CONTENEDOR_CADDY" 2>/dev/null || true) "
    [[ "$destinos" == *" /etc/caddy/ranuras "* ]]
}

# Transición: el Caddy anterior a P1-F2 no monta $DIR_RANURAS. Recrearlo corta
# un instante los WebSocket (1001); los circuitos siguen vivos en su contenedor
# y la reconexión de Blazor los recupera.
asegurar_montaje_caddy() {
    docker inspect "$CONTENEDOR_CADDY" > /dev/null 2>&1 || return 0
    caddy_tiene_montaje && return 0
    echo "Caddy sin el montaje de ranuras (anterior a P1-F2): se recrea desde docker-compose.produccion.yml."
    ( cd "$RAIZ_DESPLIEGUE/deploy/local" && docker compose -f docker-compose.produccion.yml up -d --no-deps --no-build caddy )
}

# Recarga por stdin: el Caddyfile montado es un bind de fichero suelto, y tras
# un `git checkout` el contenedor sigue viendo el inodo viejo. `docker exec`
# hereda el entorno del contenedor (DOMINIO, ACME_EMAIL).
#
# QUÉ Caddyfile: Caddy es uno solo para los dos entornos, y cualquier recarga
# aplica el fichero entero, bloque de producción incluido. Solo `desplegar
# produccion` —el único paso que ha pasado la aprobación de producción— usa el
# del checkout, y si Caddy lo acepta lo guarda como aprobado. Todo lo demás
# (desplegar staging, cualquier drenaje) recarga con el aprobado: si no, un
# despliegue de staging, o el checkout que dejó staging, metería en
# producción un Caddyfile que producción no ha aprobado. Antes del primer
# relevo de producción no hay aprobado y se usa el del checkout.
caddyfile_aprobado() { echo "$DIR_RANURAS/Caddyfile.aprobado"; }
recargar_caddy() {
    local entorno_checkout="${1:-}" fuente aprobado tmp
    aprobado="$(caddyfile_aprobado)"
    if [ "$entorno_checkout" = produccion ] || [ ! -f "$aprobado" ]; then
        fuente="$RAIZ_DESPLIEGUE/deploy/local/Caddyfile"
    else
        fuente="$aprobado"
    fi
    docker exec -i "$CONTENEDOR_CADDY" caddy reload --config /dev/stdin --adapter caddyfile < "$fuente" || return 1
    if [ "$entorno_checkout" = produccion ]; then
        tmp="$(mktemp "$DIR_RANURAS/.Caddyfile.XXXXXX")"
        cp "$fuente" "$tmp" && chmod 644 "$tmp" && mv -f "$tmp" "$aprobado" \
            || echo "::warning::no se pudo guardar el Caddyfile aprobado; las recargas de staging usarán el anterior." >&2
    fi
}

detener_drenaje_previo() {
    command -v systemctl > /dev/null 2>&1 || return 0
    systemctl stop "$(unidad_drenaje "$1")" > /dev/null 2>&1 || true
}

lanzar_drenaje() {
    local entorno="$1" contenedor="$2" id="$3" max
    max="$(max_drenaje "$entorno")"
    if command -v systemd-run > /dev/null 2>&1; then
        # Unidad transitoria: sobrevive al fin de la sesión SSH del despliegue,
        # deja su salida en journald y su nombre fijo impide dos drenajes del
        # mismo entorno a la vez.
        systemd-run --unit "$(unidad_drenaje "$entorno")" --collect --quiet \
            --setenv=RAIZ_DESPLIEGUE="$RAIZ_DESPLIEGUE" --setenv=DIR_RANURAS="$DIR_RANURAS" \
            --setenv=CONTENEDOR_CADDY="$CONTENEDOR_CADDY" --setenv=DRENAJE_INTERVALO="$DRENAJE_INTERVALO" \
            bash "$GUION_RELEVO" drenar "$entorno" "$contenedor" "$id" "$max"
        echo "Drenaje de $contenedor lanzado (máx. ${max} s): journalctl -u $(unidad_drenaje "$entorno")"
    else
        # 9>&-: el hijo no debe heredar el cerrojo de despliegue que tiene quien
        # llama, o lo retendría durante todo el drenaje.
        setsid nohup bash "$GUION_RELEVO" drenar "$entorno" "$contenedor" "$id" "$max" \
            >> "/var/log/$(unidad_drenaje "$entorno").log" 2>&1 < /dev/null 9>&- &
        echo "Drenaje de $contenedor lanzado (máx. ${max} s): /var/log/$(unidad_drenaje "$entorno").log"
    fi
}

volcar_ranura_fallida() {
    local contenedor="$1"
    echo "=== docker logs --tail 300 ${contenedor} ===" >&2
    docker logs --tail 300 "$contenedor" >&2 || true
    docker stop "$contenedor" > /dev/null 2>&1 || true
}

desplegar() {
    local entorno="$1" sha="$2" activa_="" nueva cont_nueva id_activa servicios previas previa
    local -a args
    args=(-f "docker-compose.$entorno.yml")
    [ "$entorno" = staging ] && args+=(--env-file .env.staging)

    # La que sirve de verdad: la primera del fichero que esté en marcha. Si la
    # activa murió y queda una saliente viva (Caddy ya la usa por el
    # reintento), esa es la que hay que conservar, y la nueva va en la ranura
    # de la muerta, no en la de la viva.
    local candidata
    for candidata in $(ranuras_en_fichero "$entorno"); do
        if en_marcha "$candidata"; then activa_="$candidata"; break; fi
    done
    if [ -z "$activa_" ] && [ ! -f "$(fichero_ranuras "$entorno")" ] && en_marcha "$(prefijo "$entorno")"; then
        activa_="$(prefijo "$entorno")"   # sin fichero: el contenedor único anterior a P1-F2
    fi
    case "$activa_" in
        *-azul) nueva=verde ;;
        *) nueva=azul ;;
    esac
    cont_nueva="$(prefijo "$entorno")-$nueva"
    echo "Relevo de $entorno: activa ${activa_:-<ninguna>}, nueva $cont_nueva (caemanager:$sha)."

    asegurar_ficheros_ranuras
    asegurar_montaje_caddy

    # Salientes de un relevo anterior que siguen en el fichero: la ranura libre
    # si aún drenaba, o cualquiera cuyo drenaje ya no corre (p. ej. tras
    # reiniciar el VPS: Docker la levanta y nadie la retira). Se sacan de Caddy
    # y se paran ahora; la libre la recrea el `up`. Sin una activa sana no se
    # toca ninguna: pueden ser lo único que sirve.
    detener_drenaje_previo "$entorno"
    previas="$(ranuras_en_fichero "$entorno" | grep -vx -- "${activa_:-<ninguna>}" || true)"
    if [ -n "$previas" ] && [ -n "$activa_" ]; then
        escribir_ranuras "$entorno" "$activa_"
        recargar_caddy "$entorno" || echo "::warning::no se pudo recargar Caddy al retirar las salientes anteriores; la cookie vieja reintentará en la activa."
        for previa in $previas; do
            [ "$previa" = "$activa_" ] && continue
            [ "$previa" = "$cont_nueva" ] && continue
            echo "Saliente anterior sin drenaje en marcha: se retira $previa."
            docker stop -t 30 "$previa" > /dev/null 2>&1 || true
            case "$previa" in
                *-azul|*-verde) ;;
                *) docker rm "$previa" > /dev/null 2>&1 || true ;;
            esac
        done
    fi

    cd "$RAIZ_DESPLIEGUE/deploy/local"
    export IMAGEN_TAG="$sha"
    # Servicios sin perfil (db, migrador, seq y caddy en producción) más la
    # ranura nueva. La activa no se nombra: Compose no la toca.
    mapfile -t servicios < <(docker compose "${args[@]}" config --services)
    if ! docker compose "${args[@]}" up -d --wait --wait-timeout "$RELEVO_ESPERA_SALUD" --no-build \
            "${servicios[@]}" "app-$nueva"; then
        echo "La ranura nueva $cont_nueva no llegó a sana: Caddy sigue en ${activa_:-<ninguna>}." >&2
        volcar_ranura_fallida "$cont_nueva"
        return 1
    fi
    if [ "$(docker inspect -f '{{.Config.Image}}' "$cont_nueva" 2>/dev/null || true)" != "caemanager:$sha" ]; then
        echo "$cont_nueva no corre caemanager:$sha tras el up: no se conmuta." >&2
        return 1
    fi

    escribir_ranuras "$entorno" "$cont_nueva" "$activa_"
    if ! recargar_caddy "$entorno"; then
        echo "Caddy no aceptó la conmutación a $cont_nueva: se restaura ${activa_:-<ninguna>}." >&2
        if [ -n "$activa_" ]; then
            escribir_ranuras "$entorno" "$activa_"
            recargar_caddy || echo "::warning::tampoco se pudo recargar Caddy con la ranura anterior: revisa 'docker logs $CONTENEDOR_CADDY'." >&2
        fi
        docker stop "$cont_nueva" > /dev/null 2>&1 || true
        return 1
    fi
    echo "Caddy conmutado: $entorno sirve $cont_nueva (caemanager:$sha)."

    if [ -n "$activa_" ]; then
        id_activa="$(id_de "$activa_")"
        lanzar_drenaje "$entorno" "$activa_" "$id_activa" \
            || echo "::warning::no se pudo lanzar el drenaje de $activa_: sigue en marcha y con sus circuitos; páralo a mano cuando se vacíe (runbook)."
    fi
}

# Conexiones TCP establecidas al 8080 del contenedor, sin las de loopback
# (el healthcheck). Imprime el número, o nada si no se pudo leer.
conexiones_establecidas() {
    local tablas
    tablas="$(docker exec "$1" cat /proc/net/tcp /proc/net/tcp6 2>/dev/null)" || return 0
    printf '%s\n' "$tablas" | awk '
        $2 ~ /:1F90$/ && $4 == "01" && $3 !~ /^0100007F:/ && $3 !~ /^0000000000000000FFFF00000100007F:/ && $3 !~ /^00000000000000000000000001000000:/ { n++ }
        END { print n + 0 }'
}

retirar_saliente() {
    local entorno="$1" contenedor="$2" id="$3" a
    exec 9>"$RAIZ_DESPLIEGUE/deploy/.ci-deploy.lock"
    if ! flock -w 600 9; then
        echo "Sin cerrojo de despliegue en 10 min: $contenedor sigue en marcha; se reintenta en la próxima vuelta." >&2
        exec 9>&-
        return 1
    fi
    if [ "$(id_de "$contenedor")" != "$id" ]; then
        echo "$contenedor ya no es el contenedor que drenaba (se recreó): nada que hacer."
        return 0
    fi
    a="$(activa "$entorno" || true)"
    if [ "$a" = "$contenedor" ]; then
        echo "$contenedor es la ranura activa de $entorno: no se retira." >&2
        return 0
    fi
    # Nunca se retira la saliente si la que se queda no está sana: si la nueva
    # murió tras pasar /salud, Caddy sirve por la saliente (reintento) y
    # pararla dejaría el entorno sin servir. Se aplaza y se reintenta en la
    # siguiente vuelta; si la activa no se recupera, lo resuelve el siguiente
    # despliegue (que conserva la viva).
    if [ -z "$a" ] || ! sana "$a"; then
        echo "::warning::la ranura activa de $entorno (${a:-<ninguna>}) no está sana: $contenedor no se retira todavía." >&2
        exec 9>&-
        return 1
    fi
    escribir_ranuras "$entorno" "$a"
    recargar_caddy || echo "::warning::no se pudo recargar Caddy al retirar $contenedor; la cookie vieja reintentará en la activa." >&2
    docker stop -t 30 "$contenedor" > /dev/null
    case "$contenedor" in
        *-azul|*-verde) ;;
        *) docker rm "$contenedor" > /dev/null ;;   # anterior a P1-F2: sin servicio en el compose
    esac
    echo "$contenedor retirado de $entorno."
}

drenar() {
    local entorno="$1" contenedor="$2" id="$3" max="$4" inicio="$SECONDS" vacias=0 n
    echo "Drenando $contenedor ($entorno), máx. ${max} s."
    while true; do
        if [ "$(id_de "$contenedor")" != "$id" ] || ! en_marcha "$contenedor"; then
            echo "$contenedor ya no está en marcha o se recreó: fin del drenaje."
            return 0
        fi
        n="$(conexiones_establecidas "$contenedor")"
        if [ "$n" = "0" ]; then vacias=$((vacias + 1)); else vacias=0; fi
        echo "$(( SECONDS - inicio )) s: ${n:-?} conexiones."
        if [ "$vacias" -ge 2 ] || [ $(( SECONDS - inicio )) -ge "$max" ]; then
            retirar_saliente "$entorno" "$contenedor" "$id" && return 0
        fi
        sleep "$DRENAJE_INTERVALO"
    done
}

main_relevo() {
    local orden="${1:-}"
    case "$orden" in
        desplegar)
            es_entorno "${2:-}" && [[ "${3:-}" =~ ^[0-9a-f]{40}$ ]] && [ $# -eq 3 ] \
                || { echo "uso: relevo-app.sh desplegar <staging|produccion> <sha de 40>" >&2; exit 2; }
            desplegar "$2" "$3" ;;
        activa)
            es_entorno "${2:-}" && [ $# -eq 2 ] || { echo "uso: relevo-app.sh activa <staging|produccion>" >&2; exit 2; }
            activa "$2" ;;
        drenar)
            es_entorno "${2:-}" && [ -n "${3:-}" ] && [ -n "${4:-}" ] && [[ "${5:-}" =~ ^[0-9]+$ ]] && [ $# -eq 5 ] \
                || { echo "uso: relevo-app.sh drenar <entorno> <contenedor> <id> <max_s>" >&2; exit 2; }
            drenar "$2" "$3" "$4" "$5" ;;
        *)
            echo "uso: relevo-app.sh <desplegar|activa|drenar> ..." >&2; exit 2 ;;
    esac
}

if [ "${BASH_SOURCE[0]}" = "$0" ]; then
    main_relevo "$@"
fi
