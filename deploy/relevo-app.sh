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
#   relevo-app.sh recargar <staging|produccion>
#       Recarga Caddy con los ficheros de ranuras tal como están y el
#       Caddyfile aprobado (volver-atras.sh, antes de dar por buena una ranura
#       que ya figura como activa).
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
#
# Antes de recrearlo se valida el Caddyfile del checkout con un Caddy
# desechable (misma imagen, mismos ficheros de ranuras, mismo DOMINIO): si no
# valida, no se recrea, porque un Caddy que no arranca deja sin proxy a los dos
# entornos. El Caddy nuevo arranca con el Caddyfile aprobado si existe, y si
# no, con el del checkout (compose). Es el que se acaba de validar.
validar_caddyfile() {
    local imagen dominio acme
    imagen="$(docker inspect -f '{{.Config.Image}}' "$CONTENEDOR_CADDY" 2>/dev/null)" || return 1
    dominio="$(docker exec "$CONTENEDOR_CADDY" printenv DOMINIO 2>/dev/null || true)"
    acme="$(docker exec "$CONTENEDOR_CADDY" printenv ACME_EMAIL 2>/dev/null || true)"
    docker run --rm -i --network none -e DOMINIO="$dominio" -e ACME_EMAIL="$acme" \
        -v "$DIR_RANURAS:/etc/caddy/ranuras:ro" --entrypoint caddy "$imagen" \
        validate --config /dev/stdin --adapter caddyfile < "$1"
}

asegurar_montaje_caddy() {
    docker inspect "$CONTENEDOR_CADDY" > /dev/null 2>&1 || return 0
    caddy_tiene_montaje && return 0
    echo "Caddy sin el montaje de ranuras (anterior a P1-F2): se valida el Caddyfile y se recrea desde docker-compose.produccion.yml."
    local fuente="$RAIZ_DESPLIEGUE/deploy/local/Caddyfile"
    [ -f "$(caddyfile_aprobado)" ] && fuente="$(caddyfile_aprobado)"
    if ! validar_caddyfile "$fuente"; then
        echo "El Caddyfile con el que arrancaría Caddy ($fuente) no valida: no se recrea Caddy (sigue el anterior)." >&2
        return 1
    fi
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
# producción un Caddyfile que producción no ha aprobado.
#
# ARRANQUE: la primera vez no hay aprobado. Se usa el del checkout y se guarda
# como aprobado, con aviso. Eso pasa una sola vez, en el primer relevo tras
# P1-F2 —el de staging, que el procedimiento de transición hace ir primero y
# que la coordinación revisa antes de aprobar producción— y su checkout es el
# Caddyfile de P1-F2. Si alguien borra el aprobado, vuelve a pasar: por eso el
# aviso.
#
# La copia que se va a guardar se prepara ANTES de recargar: si no se puede
# preparar, no se recarga; y si se recarga, guardarla es un `mv` en el mismo
# directorio. Así nunca queda aplicada una configuración que el aprobado no
# refleje y que una recarga posterior de staging desharía.
caddyfile_aprobado() { echo "$DIR_RANURAS/Caddyfile.aprobado"; }
recargar_caddy() {
    local entorno_checkout="${1:-}" fuente aprobado tmp=""
    aprobado="$(caddyfile_aprobado)"
    if [ "$entorno_checkout" = produccion ] || [ ! -f "$aprobado" ]; then
        fuente="$RAIZ_DESPLIEGUE/deploy/local/Caddyfile"
        [ -f "$aprobado" ] || echo "::warning::no hay Caddyfile aprobado: se usa el del checkout y queda como aprobado (arranque de P1-F2)." >&2
        tmp="$(mktemp "$DIR_RANURAS/.Caddyfile.XXXXXX")" && cp "$fuente" "$tmp" && chmod 644 "$tmp" \
            || { rm -f "$tmp"; echo "No se pudo preparar la copia del Caddyfile aprobado: no se recarga." >&2; return 1; }
    else
        fuente="$aprobado"
    fi
    if ! docker exec -i "$CONTENEDOR_CADDY" caddy reload --config /dev/stdin --adapter caddyfile < "$fuente"; then
        [ -n "$tmp" ] && rm -f "$tmp"
        return 1
    fi
    # Un `mv` fallido tiene que ser un fallo: si no, el aprobado se queda en
    # la versión anterior y la siguiente recarga, o el siguiente arranque de
    # Caddy, desharía esta (revisión de Codex). Quien llama restaura.
    if [ -n "$tmp" ] && ! mv -f "$tmp" "$aprobado"; then
        rm -f "$tmp"
        echo "Caddy aceptó la recarga, pero no se pudo guardar el Caddyfile aprobado." >&2
        return 1
    fi
    return 0
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
        # `|| return 1` explícito: se llama a la izquierda de `||`, donde
        # errexit no actúa, y sin él un fallo acababa en «lanzado».
        systemd-run --unit "$(unidad_drenaje "$entorno")" --collect --quiet \
            --setenv=RAIZ_DESPLIEGUE="$RAIZ_DESPLIEGUE" --setenv=DIR_RANURAS="$DIR_RANURAS" \
            --setenv=CONTENEDOR_CADDY="$CONTENEDOR_CADDY" --setenv=DRENAJE_INTERVALO="$DRENAJE_INTERVALO" \
            bash "$GUION_RELEVO" drenar "$entorno" "$contenedor" "$id" "$max" || return 1
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

    # La que sirve de verdad: la primera del fichero que esté SANA (en marcha
    # y healthy); si ninguna lo está, la primera en marcha. Si la activa murió
    # o no responde y queda una saliente sana (Caddy ya la usa por el
    # reintento), esa es la que hay que conservar, y la nueva va en la ranura
    # de la otra, no en la de la sana.
    local candidata primera_en_marcha=""
    for candidata in $(ranuras_en_fichero "$entorno"); do
        if sana "$candidata"; then activa_="$candidata"; break; fi
        [ -z "$primera_en_marcha" ] && en_marcha "$candidata" && primera_en_marcha="$candidata"
    done
    [ -n "$activa_" ] || activa_="$primera_en_marcha"
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

    # Salientes de un relevo anterior que siguen en el fichero (la ranura libre
    # si aún drenaba, o una cuyo drenaje murió, p. ej. tras reiniciar el VPS).
    # ANTES del up solo se saca de Caddy la ranura libre, porque el up la
    # recrea, y solo con una activa sana. Las demás se paran DESPUÉS de
    # conmutar: si la nueva no llega a sana, siguen ahí (pueden ser lo único
    # que sirve). El drenaje anterior no se para aquí: solo actúa bajo el
    # cerrojo, que tiene este despliegue, y si su contenedor se recrea, lo
    # detecta por el Id y termina.
    previas="$(ranuras_en_fichero "$entorno" | grep -vx -- "${activa_:-<ninguna>}" || true)"
    # Sin aprobado no se recarga aquí: esa recarga haría el arranque del
    # aprobado desde el checkout ANTES de saber si el up va bien (revisión de
    # Codex, pasada 4). La ranura libre se recrea igual; mientras, la cookie
    # vieja reintenta en la activa, y el arranque del aprobado queda para la
    # conmutación, tras un up sano.
    if [ -n "$activa_" ] && sana "$activa_" && [ -f "$(caddyfile_aprobado)" ] \
            && [[ $'\n'"$previas"$'\n' == *$'\n'"$cont_nueva"$'\n'* ]]; then
        escribir_ranuras "$entorno" "$activa_" \
            $(printf '%s\n' "$previas" | grep -vx -- "$cont_nueva" | awk 'NR == 1')
        # Con el aprobado, no con el del checkout: esta recarga solo cambia
        # las ranuras. Si aplicara aquí el Caddyfile nuevo y luego fallara el
        # up, Caddy quedaría con un Caddyfile que el aprobado no refleja
        # (revisión de Codex, pasada 3). El nuevo entra solo en la
        # conmutación, que sí restaura si algo falla.
        recargar_caddy || echo "::warning::no se pudo recargar Caddy al sacar $cont_nueva, que se va a recrear; la cookie vieja reintentará en la activa."
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

    local -a antes
    mapfile -t antes < <(ranuras_en_fichero "$entorno")
    escribir_ranuras "$entorno" "$cont_nueva" "$activa_"
    if ! recargar_caddy "$entorno"; then
        echo "Caddy no aceptó la conmutación a $cont_nueva: se restaura el fichero de ranuras anterior." >&2
        if [ "${#antes[@]}" -gt 0 ]; then
            escribir_ranuras "$entorno" "${antes[0]}" "${antes[1]:-}"
            recargar_caddy || echo "::warning::tampoco se pudo recargar Caddy con las ranuras anteriores: revisa 'docker logs $CONTENEDOR_CADDY'." >&2
        fi
        docker stop "$cont_nueva" > /dev/null 2>&1 || true
        return 1
    fi
    echo "Caddy conmutado: $entorno sirve $cont_nueva (caemanager:$sha)."

    # Salientes de relevos anteriores que no son la activa ni la nueva: ya
    # fuera de Caddy, se paran ahora que la nueva sirve.
    for previa in $previas; do
        [ "$previa" = "$cont_nueva" ] && continue
        echo "Saliente de un relevo anterior: se retira $previa."
        docker stop -t 30 "$previa" > /dev/null 2>&1 \
            || echo "::warning::no se pudo parar $previa: sigue en marcha fuera de Caddy; párala a mano."
        case "$previa" in
            *-azul|*-verde) ;;
            *) docker rm "$previa" > /dev/null 2>&1 || true ;;
        esac
    done

    if [ -n "$activa_" ]; then
        detener_drenaje_previo "$entorno"
        id_activa="$(id_de "$activa_")"
        lanzar_drenaje "$entorno" "$activa_" "$id_activa" \
            || echo "::warning::no se pudo lanzar el drenaje de $activa_: sigue en marcha y con sus circuitos; páralo a mano cuando se vacíe (runbook)."
    fi
}

# Recarga Caddy con el fichero de ranuras tal como está y el Caddyfile
# aprobado. Para cuando el fichero y la configuración cargada pueden no
# coincidir (un relevo interrumpido entre escribir y recargar): la usa
# volver-atras.sh antes de dar por buena una ranura que ya figura como activa.
recargar() {
    recargar_caddy
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
    # `if !` explícito: esta función se llama a la izquierda de `&&`, donde
    # errexit no actúa, y un stop fallido acababa en «retirado».
    if ! docker stop -t 30 "$contenedor" > /dev/null; then
        echo "::warning::no se pudo parar $contenedor: se reintenta en la próxima vuelta." >&2
        exec 9>&-
        return 1
    fi
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
        recargar)
            es_entorno "${2:-}" && [ $# -eq 2 ] || { echo "uso: relevo-app.sh recargar <staging|produccion>" >&2; exit 2; }
            recargar ;;
        drenar)
            es_entorno "${2:-}" && [ -n "${3:-}" ] && [ -n "${4:-}" ] && [[ "${5:-}" =~ ^[0-9]+$ ]] && [ $# -eq 5 ] \
                || { echo "uso: relevo-app.sh drenar <entorno> <contenedor> <id> <max_s>" >&2; exit 2; }
            drenar "$2" "$3" "$4" "$5" ;;
        *)
            echo "uso: relevo-app.sh <desplegar|activa|recargar|drenar> ..." >&2; exit 2 ;;
    esac
}

if [ "${BASH_SOURCE[0]}" = "$0" ]; then
    main_relevo "$@"
fi
