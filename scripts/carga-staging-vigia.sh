#!/bin/bash
# Orquesta la carga progresiva contra STAGING (REC-196/P33, decisión D2 del
# propietario, 2026-09-19) con un vigía de PRODUCCIÓN que la aborta, y un
# muestreo de memoria del VPS en paralelo. Lo invoca a mano el workflow
# carga-staging.yml; no corre en ningún otro sitio.
#
# Por qué existe el vigía: staging y producción comparten máquina (4 GB, sin
# swap). Una carga contra staging puede quitarle memoria o CPU a producción,
# así que el criterio de aborto se mide SOBRE PRODUCCIÓN (/salud, una petición
# por segundo, anónima, un SELECT 1), no sobre lo que ve k6 en staging.
#
# Criterios de aborto (propuestos por la sesión T6; los aprueba quien lance la
# carga al revisar la PR), sobre las respuestas de /salud de PRODUCCIÓN:
#   1. cualquier 5xx;
#   2. cualquier respuesta que tarde más de 3 s o no llegue (timeout);
#   3. 3 respuestas seguidas distintas de 200;
#   4. degradación sostenida: p95 > 1 s en una ventana de 30 s (con ≥ 15
#      muestras y la ventana cubierta; con 1 muestra/s, p95 = tolera 1 valor
#      atípico y aborta con 2);
#   5. (con COMPROBAR_DESPLIEGUES=1) que empiece un despliegue mientras dura, o
#      no poder consultarlo 2 veces seguidas (falla cerrado);
#   6. que el muestreo de memoria del VPS se pierda: sin él la carga no sirve.
# Al abortar: se para k6 (SIGTERM), se sigue recogiendo el tramo de
# recuperación y el resultado del job es ROJO (código 3).
#
# Códigos de salida: 0 carga completa sin abortar · 2 entrada no válida ·
# 3 ABORTADA por producción · 4 verificación previa fallida (no se empezó) ·
# 5 el VPS no ofrece el modo de muestreo (hacen falta dos despliegues tras
# fusionar la PR que lo añadió) · 6 hay un despliegue en curso (no se empezó) ·
# 7 k6 falló o se cortó por sus umbrales de STAGING (sin abortar producción) ·
# 8 el muestreo del VPS no cubrió la ventana (la carga acabó, los datos no).
set -uo pipefail

CONFIRMACION_ESPERADA="CARGAR STAGING"
UMBRAL_P95_MS=1000
VENTANA_MS=30000
TIMEOUT_MS=3000
FALLOS_SEGUIDOS=3
MUESTRAS_MIN_VENTANA=15
DURACION_MAX_MUESTREO=420

log() { echo "[$(date -u +%H:%M:%S)] $*"; }

# ---------------------------------------------------------------------------
# Un sondeo de /salud: imprime "<codigo> <milisegundos>". Sin respuesta o con
# timeout de curl (3 s) imprime "000 <ms>": nunca falla.
# ---------------------------------------------------------------------------
sondear_salud() {
    local salida
    salida="$(curl -sS -o /dev/null --max-time 3 -w '%{http_code} %{time_total}' "$1/salud" 2>/dev/null)" || true
    [ -n "$salida" ] || salida="000 3.000"
    echo "$salida" | awk '{ printf "%s %d\n", $1, $2 * 1000 }'
}

# ---------------------------------------------------------------------------
# Decide sobre TODO el CSV de muestras (epoch_ms,codigo,ms). Imprime "OK" y
# devuelve 0, o "ABORTAR: <motivo>" y devuelve 1. Los criterios 1-3 miran todas
# las muestras desde el inicio del vigía; el 4, solo la última ventana.
# ---------------------------------------------------------------------------
evaluar_ventana() {
    local fichero="$1" motivo estado primero ultimo lim valores m rango p95
    [ -s "$fichero" ] || { echo "OK"; return 0; }
    motivo="$(awk -F, -v timeout_ms="$TIMEOUT_MS" -v max_seguidos="$FALLOS_SEGUIDOS" '
        { seguidos_ok = ($2 == "200") }
        $2 ~ /^5[0-9][0-9]$/ { print "ABORTAR: respuesta " $2 " de /salud de producción"; exit 1 }
        $2 == "000" || $3 > timeout_ms { print "ABORTAR: /salud de producción no respondió o tardó más de " timeout_ms " ms (" $3 " ms)"; exit 1 }
        { if (seguidos_ok) seguidos = 0; else seguidos++ }
        seguidos >= max_seguidos { print "ABORTAR: " seguidos " respuestas seguidas de /salud de producción distintas de 200"; exit 1 }
    ' "$fichero")"
    estado=$?
    if [ "$estado" -ne 0 ]; then echo "$motivo"; return 1; fi

    # La ventana se mide sobre las muestras de los ÚLTIMOS 30 s, no sobre todo el
    # fichero: tras una pausa del vigía puede haber muchas filas viejas y solo
    # una reciente, y una sola respuesta lenta no es degradación sostenida.
    ultimo="$(tail -n1 "$fichero" | cut -d, -f1)"
    lim=$(( ultimo - VENTANA_MS ))
    m="$(awk -F, -v lim="$lim" '$1 >= lim { n++ } END { print n + 0 }' "$fichero")"
    primero="$(awk -F, -v lim="$lim" '$1 >= lim { print $1; exit }' "$fichero")"
    if [ "$m" -ge "$MUESTRAS_MIN_VENTANA" ] && [ $(( ultimo - primero )) -ge $(( VENTANA_MS - 1000 )) ]; then
        valores="$(awk -F, -v lim="$lim" '$1 >= lim { print $3 }' "$fichero" | sort -n)"
        rango=$(( (95 * m + 99) / 100 ))
        p95="$(printf '%s\n' "$valores" | sed -n "${rango}p")"
        if [ "${p95:-0}" -gt "$UMBRAL_P95_MS" ]; then
            echo "ABORTAR: degradación sostenida, p95 de /salud de producción = ${p95} ms > ${UMBRAL_P95_MS} ms en los últimos $(( VENTANA_MS / 1000 )) s (${m} muestras)"
            return 1
        fi
    fi
    echo "OK"
    return 0
}

# ---------------------------------------------------------------------------
# Despliegues de GitHub: 0 ninguno en marcha, 1 hay uno (en curso o en cola),
# 2 no se pudo saber. Un despliegue "waiting" (a la espera de la aprobación
# humana de producción) no cuenta: no construye nada mientras espera.
# ---------------------------------------------------------------------------
estado_despliegues_github() {
    local estado total=0 n
    for estado in in_progress queued; do
        n="$(gh run list --workflow deploy.yml --status "$estado" --json databaseId --jq length 2>/dev/null)" || return 2
        case "$n" in ''|*[!0-9]*) return 2 ;; esac
        total=$(( total + n ))
    done
    [ "$total" -eq 0 ] && return 0
    return 1
}

ssh_muestreo() {
    ssh -i "${SSH_KEY:-$HOME/.ssh/id_ed25519}" -o BatchMode=yes -o ConnectTimeout=15 \
        -o ServerAliveInterval=10 -o ServerAliveCountMax=3 "root@${VPS_HOST}" "muestreo-memoria $1 $2"
}

registrar_sondeo() {
    local par
    par="$(sondear_salud "$URL_PRODUCCION")"
    echo "$(date +%s%3N),${par% *},${par#* }" >> "$DIR_SALIDA/salud-produccion.csv"
}

# Vigila producción durante $1 segundos (o, con 0, mientras viva PID_K6). Con
# $2 = 1 evalúa los criterios de aborto y devuelve 1 (MOTIVO_ABORTO) al
# cumplirse alguno; con 0 solo registra (tramo de recuperación).
vigilar() {
    local segundos="$1" evalua="$2" fin veredicto vueltas=0 desconocidos=0
    fin=$(( SECONDS + segundos ))
    while :; do
        if [ "$segundos" -gt 0 ] && [ "$SECONDS" -ge "$fin" ]; then return 0; fi
        if [ "$segundos" -eq 0 ] && ! kill -0 "$PID_K6" 2>/dev/null; then return 0; fi
        registrar_sondeo
        vueltas=$(( vueltas + 1 ))
        if [ "$evalua" = 1 ]; then
            veredicto="$(evaluar_ventana "$DIR_SALIDA/salud-produccion.csv")" || { MOTIVO_ABORTO="$veredicto"; return 1; }
            if [ "${COMPROBAR_DESPLIEGUES:-0}" = 1 ] && [ $(( vueltas % 10 )) -eq 0 ]; then
                estado_despliegues_github
                case $? in
                    0) desconocidos=0 ;;
                    1) MOTIVO_ABORTO="ABORTAR: ha empezado (o hay en cola) un despliegue mientras dura la carga"; return 1 ;;
                    *)
                        # Falla cerrado: sin poder saber si hay un despliegue, no se sigue
                        # empujando el VPS compartido. Dos consultas seguidas sin respuesta
                        # (unos 20 s) abortan; una sola se tolera como fallo transitorio.
                        desconocidos=$(( desconocidos + 1 ))
                        if [ "$desconocidos" -ge 2 ]; then MOTIVO_ABORTO="ABORTAR: no se puede consultar a GitHub si hay un despliegue en marcha (2 consultas seguidas sin respuesta)"; return 1; fi ;;
                esac
            fi
            # Sin muestreo del VPS la carga no sirve para lo que se lanzó y solo
            # arriesga producción: si el proceso SSH murió sin cerrar la ventana, se para.
            if [ -n "${PID_SSH:-}" ] && ! kill -0 "$PID_SSH" 2>/dev/null && ! grep -q "Fin del muestreo" "$DIR_SALIDA/muestreo.log" 2>/dev/null; then
                MOTIVO_ABORTO="ABORTAR: el muestreo de memoria del VPS se ha perdido (el proceso SSH terminó sin cerrar la ventana)"; return 1
            fi
        fi
        sleep "$SONDEO_S"
    done
}

# Resumen legible del muestreo del VPS: el máximo de memoria que vio docker
# stats por contenedor durante la ventana, y el `memory.peak` final (pico de
# TODA la vida del contenedor) tal cual lo dejó el muestreo.
resumir_muestreo() {
    local log="$1"
    [ -s "$log" ] || { echo "(sin salida del muestreo)"; return 0; }
    echo "Máximo de memoria vista por docker stats en la ventana (una fila por contenedor):"
    awk '
        / mem=/ {
            nombre = $1; sub(/^mem=/, "", $2); valor = $2; unidad = valor; gsub(/[0-9.]/, "", unidad); gsub(/[A-Za-z]/, "", valor)
            f = (unidad == "GiB") ? 1024 : (unidad == "KiB") ? 1/1024 : (unidad == "B") ? 1/1048576 : 1
            mib = valor * f
            if (!(nombre in max) || mib > max[nombre]) max[nombre] = mib
        }
        END { for (n in max) printf "  %-28s %8.1f MiB\n", n, max[n] }
    ' "$log" | sort
    echo "Lectura final de cgroup (memory.peak = pico de toda la vida del contenedor):"
    awk '/Estado final de los contenedores/ {dentro = 1} dentro && (/^--- / || /memory\.peak|memory\.events|memory\.max/) {print "  " $0}' "$log"
}

main() {
    local i par url veredicto salida_pre SEGUNDOS_MUESTREO ABORTADO=0
    DIR_SALIDA="${DIR_SALIDA:-carga-staging-out}"
    SONDEO_S="${SONDEO_S:-1}"
    K6_BIN="${K6_BIN:-k6}"
    BASE_S="${BASE_S:-30}"
    CARGA_S="${CARGA_S:-300}"
    RECUP_S="${RECUP_S:-60}"
    # Margen para lo que k6 añade a las etapas (gracefulRampDown 5 s + gracefulStop 5 s) y su arranque.
    MARGEN_S="${MARGEN_S:-15}"
    VUS_MAX="${VUS_MAX:-20}"
    MOTIVO_ABORTO=""
    PID_K6=""

    # --- entrada -----------------------------------------------------------
    if [ "${CONFIRMACION:-}" != "$CONFIRMACION_ESPERADA" ]; then
        echo "Falta la confirmación: escribe exactamente «$CONFIRMACION_ESPERADA». No se carga nada." >&2; return 2
    fi
    [ -n "${URL_PRODUCCION:-}" ] && [ -n "${URL_STAGING:-}" ] && [ -n "${VPS_HOST:-}" ] || { echo "Faltan URL_PRODUCCION, URL_STAGING o VPS_HOST." >&2; return 2; }
    case "$URL_STAGING" in https://staging.*) ;; *) echo "URL_STAGING debe ser https://staging.…, recibí: $URL_STAGING" >&2; return 2 ;; esac
    case "$URL_PRODUCCION" in *staging*) echo "URL_PRODUCCION no puede ser staging: $URL_PRODUCCION" >&2; return 2 ;; https://*) ;; *) echo "URL_PRODUCCION debe ser https://…" >&2; return 2 ;; esac
    case "$VUS_MAX" in ''|*[!0-9]*) echo "VUS_MAX debe ser un entero." >&2; return 2 ;; esac
    if [ "$VUS_MAX" -lt 5 ] || [ "$VUS_MAX" -gt 60 ]; then echo "VUS_MAX fuera de 5..60: $VUS_MAX" >&2; return 2; fi
    SEGUNDOS_MUESTREO=$(( BASE_S + CARGA_S + MARGEN_S + RECUP_S ))
    if [ "$SEGUNDOS_MUESTREO" -gt "$DURACION_MAX_MUESTREO" ]; then
        echo "La ventana de muestreo (${SEGUNDOS_MUESTREO} s) supera el techo de ${DURACION_MAX_MUESTREO} s del VPS." >&2; return 2
    fi
    mkdir -p "$DIR_SALIDA"; : > "$DIR_SALIDA/salud-produccion.csv"

    # --- verificación previa: nada de empezar con algo ya mal ---------------
    log "Verificación previa: /salud de producción y de staging, 5 sondeos cada uno."
    for i in 1 2 3 4 5; do
        for url in "$URL_PRODUCCION" "$URL_STAGING"; do
            par="$(sondear_salud "$url")"
            if [ "${par% *}" != "200" ] || [ "${par#* }" -ge "$UMBRAL_P95_MS" ]; then
                echo "Verificación previa fallida: $url/salud dio ${par% *} en ${par#* } ms. No se carga nada." >&2; return 4
            fi
        done
        sleep "$SONDEO_S"
    done
    if [ "${COMPROBAR_DESPLIEGUES:-0}" = 1 ]; then
        estado_despliegues_github; case $? in
            0) ;;
            1) echo "Hay un despliegue en curso o en cola. No se carga nada." >&2; return 6 ;;
            *) echo "No se pudo consultar los despliegues de GitHub. No se carga nada." >&2; return 4 ;;
        esac
    fi
    log "Verificación previa del muestreo del VPS (10 s)."
    salida_pre="$(ssh_muestreo 10 2 2>&1)"
    if printf '%s' "$salida_pre" | grep -q "Hay un despliegue en curso"; then
        echo "El VPS reporta un despliegue en curso. No se carga nada." >&2; return 6
    fi
    if ! printf '%s' "$salida_pre" | grep -q "Fin del muestreo"; then
        echo "El VPS no ofrece el modo muestreo-memoria (¿aún corre un ci-deploy.sh anterior? hacen falta dos despliegues tras fusionar la PR que lo añadió). Salida:" >&2
        printf '%s\n' "$salida_pre" | head -5 >&2; return 5
    fi

    # --- ejecución ----------------------------------------------------------
    log "Arrancando el muestreo del VPS (${SEGUNDOS_MUESTREO} s, cada 3 s)."
    ssh_muestreo "$SEGUNDOS_MUESTREO" 3 > "$DIR_SALIDA/muestreo.log" 2>&1 &
    PID_SSH=$!

    log "Línea base de ${BASE_S} s (solo vigilancia)."
    if ! vigilar "$BASE_S" 1; then
        ABORTADO=1
    else
        log "Arrancando k6 contra ${URL_STAGING} con VUS_MAX=${VUS_MAX} (${CARGA_S} s)."
        "$K6_BIN" run --summary-export "$DIR_SALIDA/k6-resumen.json" \
            -e "BASE_URL=$URL_STAGING" -e "VUS_MAX=$VUS_MAX" \
            "$(dirname "${BASH_SOURCE[0]}")/carga-staging.k6.js" > "$DIR_SALIDA/k6.log" 2>&1 &
        PID_K6=$!
        if ! vigilar 0 1; then
            ABORTADO=1
            log "$MOTIVO_ABORTO"
            log "Parando k6."
            kill -TERM "$PID_K6" 2>/dev/null
            for i in 1 2 3 4 5 6 7 8 9 10; do kill -0 "$PID_K6" 2>/dev/null || break; sleep 1; done
            kill -KILL "$PID_K6" 2>/dev/null
        fi
        wait "$PID_K6" 2>/dev/null
        K6_ESTADO=$?
    fi
    [ "$ABORTADO" = 1 ] && [ -n "$MOTIVO_ABORTO" ] && log "$MOTIVO_ABORTO"

    log "Tramo de recuperación de ${RECUP_S} s (solo registro)."
    vigilar "$RECUP_S" 0
    for i in $(seq 1 60); do kill -0 "$PID_SSH" 2>/dev/null || break; sleep 1; done
    SSH_ESTADO=""
    if kill -0 "$PID_SSH" 2>/dev/null; then kill "$PID_SSH" 2>/dev/null; SSH_ESTADO="no terminó en 60 s"; fi
    wait "$PID_SSH" 2>/dev/null; SSH_CODIGO=$?
    [ -n "$SSH_ESTADO" ] || SSH_ESTADO="código $SSH_CODIGO"
    # Completo = el proceso SSH terminó por sí solo con 0 Y cerró la ventana.
    MUESTREO_COMPLETO=1
    if [ "$SSH_CODIGO" -ne 0 ] || [ "$SSH_ESTADO" = "no terminó en 60 s" ] || ! grep -q "Fin del muestreo" "$DIR_SALIDA/muestreo.log" 2>/dev/null; then
        MUESTREO_COMPLETO=0
    fi

    # --- resumen ------------------------------------------------------------
    {
        echo "Resultado: $([ "$ABORTADO" = 1 ] && echo "ABORTADA — $MOTIVO_ABORTO" || echo "completa, sin abortar")"
        echo "k6 (código de salida): ${K6_ESTADO:-no arrancó}$([ "$ABORTADO" = 0 ] && [ "${K6_ESTADO:-0}" -ne 0 ] && echo " — FALLÓ: se cortó por sus umbrales de staging o no se ejecutó bien")"
        echo "Muestreo del VPS: $([ "$MUESTREO_COMPLETO" = 1 ] && echo "completo" || echo "INCOMPLETO (${SSH_ESTADO}): los datos de memoria no cubren toda la ventana")"
        echo "Muestras de /salud de producción: $(wc -l < "$DIR_SALIDA/salud-produccion.csv" | tr -d ' '); máximo $(cut -d, -f3 "$DIR_SALIDA/salud-produccion.csv" | sort -n | tail -n1) ms; distintas de 200: $(awk -F, '$2 != "200"' "$DIR_SALIDA/salud-produccion.csv" | wc -l | tr -d ' ')"
        resumir_muestreo "$DIR_SALIDA/muestreo.log"
    } | tee "$DIR_SALIDA/resumen.txt"
    [ "$ABORTADO" = 1 ] && return 3
    [ "${K6_ESTADO:-0}" -ne 0 ] && return 7
    [ "$MUESTREO_COMPLETO" = 1 ] || return 8
    return 0
}

if [ "${BASH_SOURCE[0]}" = "$0" ]; then
    main "$@"
    exit $?
fi
