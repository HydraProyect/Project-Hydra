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
#   4. degradación sostenida: p95 > 1 s en una ventana de 30 s (con ≥ 10
#      muestras y la ventana cubierta; con 1 muestra/s, p95 = tolera 1 valor
#      atípico y aborta con 2);
#   5. (con COMPROBAR_DESPLIEGUES=1) que empiece un despliegue mientras dura, o
#      no poder consultarlo 2 veces seguidas (falla cerrado);
#   6. que el muestreo de memoria del VPS se pierda: sin él la carga no sirve.
# Al abortar: se para k6 (SIGTERM), se sigue recogiendo el tramo de
# recuperación y el resultado del job es ROJO (código 3).
#
# Códigos de salida: 0 carga completa sin abortar · 2 entrada no válida ·
# 3 ABORTADA por producción · 4 verificación previa fallida, incluida la prueba de 10 s del muestreo sin lecturas (docker lento; no se empezó) ·
# 5 el VPS no ofrece el modo de muestreo (hacen falta dos despliegues tras
# fusionar la PR que lo añadió) · 6 hay un despliegue en curso (no se empezó) ·
# 7 k6 falló o se cortó por sus umbrales de STAGING (sin abortar producción) ·
# 8 el muestreo del VPS no pasó evaluar_cierre: sus hechos (lecturas, primera muestra, volcado final) no sostienen una medición completa (la carga acabó, los datos no).
set -uo pipefail

CONFIRMACION_ESPERADA="CARGAR STAGING"
UMBRAL_P95_MS=1000
VENTANA_MS=30000
TIMEOUT_MS=3000
FALLOS_SEGUIDOS=3
MUESTRAS_MIN_VENTANA=10
LENTAS_MIN_VENTANA=2
DURACION_MAX_MUESTREO=420
# Contrato con el VPS (deploy/ci-deploy.sh, muestreo_memoria): el muestreo cierra con
#   === Fin del muestreo: muestras=N stats_ok=M t_primera=Ts volcado_inicial=k/n volcado_final=k/n duracion=Ds ===
# `muestras` cuenta vueltas; lo medido es `stats_ok`. Un muestreo se da por válido
# solo si los HECHOS lo sostienen (evaluar_cierre); la ausencia de la línea, o que
# el servidor la sustituya por «INTERRUMPIDO»/«SIN lecturas», lo invalida.
CIERRE_MUESTREO="Fin del muestreo:"
# stats_ok mínimo, en % de duración/intervalo. HYPOTHESIS: la mitad tolera un
# `docker stats` más lento que el intervalo justo cuando el servidor va cargado
# (que es cuando se mide) sin dar por bueno un muestreo casi vacío. Los hechos
# van siempre al resumen, pase o no el umbral.
FRACCION_MIN_PCT=50
INTERVALO_MUESTREO_S=3

# Epoch en milisegundos. `date +%s%3N` no es portable (uutils coreutils lo imprime mal).
ahora_ms() { echo $(( $(date +%s%N) / 1000000 )); }

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
    local fichero="$1" motivo estado primero ultimo lim valores m rango p95 lentas
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
        # Con pocas muestras (las respuestas lentas espacian los sondeos) el p95 es el
        # máximo: se exigen al menos 2 lentas para que un valor atípico no aborte.
        lentas="$(awk -F, -v lim="$lim" -v u="$UMBRAL_P95_MS" '$1 >= lim && $3 > u { n++ } END { print n + 0 }' "$fichero")"
        if [ "${p95:-0}" -gt "$UMBRAL_P95_MS" ] && [ "$lentas" -ge "$LENTAS_MIN_VENTANA" ]; then
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
    echo "$(ahora_ms),${par% *},${par#* }" >> "$DIR_SALIDA/salud-produccion.csv"
}

# Campo `clave=valor` de la línea de cierre (vacío si no está).
campo_cierre() { printf '%s\n' "$1" | sed -n "s/.* $2=\([^ ]*\).*/\1/p" | head -1; }

# Juzga la línea de cierre del muestreo contra los hechos que declara.
#   $1 salida del muestreo · $2 duración pedida (s) · $3 intervalo (s)
#   $4 t_primera máximo (s: la línea base) · $5 "pleno" o "previo"
# Con "previo" (la prueba de 10 s) basta con que haya lecturas: comprueba que el
# modo existe y funciona, no que cubra una ventana. Imprime los motivos y
# devuelve 1 si no vale; devuelve 0 si vale (con "previo"/"pleno", sin imprimir).
evaluar_cierre() {
    local salida="$1" duracion="$2" intervalo="$3" base="$4" modo="$5"
    local linea muestras ok t_primera fin esperado minimo motivos=""
    linea="$(printf '%s\n' "$salida" | grep -a "$CIERRE_MUESTREO" | tail -1)"
    [ -n "$linea" ] || { echo "el muestreo no llegó a cerrar («Fin del muestreo» ausente)"; return 1; }
    muestras="$(campo_cierre "$linea" muestras)"; ok="$(campo_cierre "$linea" stats_ok)"
    t_primera="$(campo_cierre "$linea" t_primera)"; t_primera="${t_primera%s}"
    fin="$(campo_cierre "$linea" volcado_final)"
    case "$muestras" in ''|*[!0-9]*) echo "cierre sin datos legibles (muestras): $linea"; return 1 ;; esac
    case "$ok" in ''|*[!0-9]*) echo "cierre sin datos legibles (stats_ok): $linea"; return 1 ;; esac
    [ "$ok" -ge 1 ] || { echo "0 lecturas útiles de docker stats en ${muestras} vueltas"; return 1; }
    [ "$modo" = pleno ] || return 0
    esperado=$(( duracion / intervalo )); minimo=$(( esperado * FRACCION_MIN_PCT / 100 ))
    [ "$ok" -ge "$minimo" ] || motivos="${motivos}solo ${ok} lecturas de docker stats (mínimo ${minimo}: el ${FRACCION_MIN_PCT} % de ${esperado}); "
    case "$t_primera" in
        ''|*[!0-9]*) motivos="${motivos}t_primera ilegible; " ;;
        *) [ "$t_primera" -le "$base" ] || motivos="${motivos}la primera muestra cayó en t=${t_primera}s, después de la línea base (${base}s); " ;;
    esac
    if [[ "$fin" =~ ^([0-9]+)/([0-9]+)$ ]] && [ "${BASH_REMATCH[2]}" -ge 1 ] && [ "${BASH_REMATCH[1]}" -eq "${BASH_REMATCH[2]}" ]; then :
    else motivos="${motivos}volcado final incompleto (${fin:-sin dato}): falta memory.peak de algún contenedor; "; fi
    [ -z "$motivos" ] || { echo "${motivos%; }"; return 1; }
    return 0
}

# Vigila producción durante $1 segundos (o, con 0, mientras viva PID_K6). Con
# $2 = 1 evalúa los criterios de aborto y devuelve 1 (MOTIVO_ABORTO) al
# cumplirse alguno; con 0 solo registra (tramo de recuperación).
vigilar() {
    local segundos="$1" evalua="$2" fin veredicto vueltas=0 desconocidos=0 inicio_vuelta espera
    fin=$(( SECONDS + segundos ))
    while :; do
        if [ "$segundos" -gt 0 ] && [ "$SECONDS" -ge "$fin" ]; then return 0; fi
        if [ "$segundos" -eq 0 ] && ! kill -0 "$PID_K6" 2>/dev/null; then return 0; fi
        inicio_vuelta="$(ahora_ms)"
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
            if [ -n "${PID_SSH:-}" ] && ! kill -0 "$PID_SSH" 2>/dev/null && ! grep -q "$CIERRE_MUESTREO" "$DIR_SALIDA/muestreo.log" 2>/dev/null; then
                MOTIVO_ABORTO="ABORTAR: el muestreo de memoria del VPS se ha perdido (el proceso SSH terminó sin cerrar la ventana)"; return 1
            fi
        fi
        # Cadencia por reloj: el sondeo lento (hasta 3 s) cuenta dentro del intervalo,
        # no se le suma. Si no, con respuestas de 1,5 s salen ~12 muestras cada 30 s.
        espera="$(awk -v s="$SONDEO_S" -v ini="$inicio_vuelta" -v fin="$(ahora_ms)" 'BEGIN { r = s - (fin - ini) / 1000; if (r < 0) r = 0; if (r > s) r = s; printf "%.3f", r }')"
        sleep "$espera"
    done
}

# Resumen legible del muestreo del VPS: el máximo de memoria que vio docker
# stats por contenedor durante la ventana, y el `memory.peak` final (pico de
# TODA la vida del contenedor) tal cual lo dejó el muestreo.
resumir_muestreo() {
    local log="$1"
    [ -s "$log" ] || { echo "(sin salida del muestreo)"; return 0; }
    if ! grep -aq ' mem=' "$log"; then
        echo "(sin ninguna lectura de docker stats en la ventana: no hay tabla de máximos)"
    else
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
    fi
    echo "Lectura final de cgroup (memory.peak = pico de toda la vida del contenedor):"
    awk '/Estado final de los contenedores/ {dentro = 1} dentro && (/^--- / || /memory\.peak|memory\.events|memory\.max/) {print "  " $0}' "$log"
}

main() {
    local i par url veredicto salida_pre motivo_pre SEGUNDOS_MUESTREO ABORTADO=0
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
    if ! motivo_pre="$(evaluar_cierre "$salida_pre" 10 2 0 previo)"; then
        if printf '%s' "$salida_pre" | grep -q "=== Muestreo de memoria (REC-196/P33)"; then
            # El modo existe y arrancó: lo que falla es la máquina (docker lento), no el despliegue.
            echo "El VPS ofrece el modo muestreo-memoria pero la prueba de 10 s no dio lecturas válidas: ${motivo_pre}. No se carga nada (¿docker lento? no hace falta desplegar de nuevo). Final de la salida:" >&2
            printf '%s\n' "$salida_pre" | tail -6 >&2; return 4
        fi
        echo "El VPS no ofrece el modo muestreo-memoria (¿aún corre un ci-deploy.sh anterior? hacen falta dos despliegues tras fusionar la PR que lo añadió). Salida:" >&2
        printf '%s\n' "$salida_pre" | head -5 >&2; return 5
    fi

    # --- ejecución ----------------------------------------------------------
    log "Arrancando el muestreo del VPS (${SEGUNDOS_MUESTREO} s, cada ${INTERVALO_MUESTREO_S} s)."
    ssh_muestreo "$SEGUNDOS_MUESTREO" "$INTERVALO_MUESTREO_S" > "$DIR_SALIDA/muestreo.log" 2>&1 &
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
    MUESTREO_COMPLETO=1; MOTIVO_MUESTREO=""
    if [ "$SSH_CODIGO" -ne 0 ] || [ "$SSH_ESTADO" = "no terminó en 60 s" ]; then
        MUESTREO_COMPLETO=0; MOTIVO_MUESTREO="el proceso SSH acabó mal (${SSH_ESTADO})"
    elif ! MOTIVO_MUESTREO="$(evaluar_cierre "$(cat "$DIR_SALIDA/muestreo.log" 2>/dev/null)" "$SEGUNDOS_MUESTREO" "$INTERVALO_MUESTREO_S" "$BASE_S" pleno)"; then
        MUESTREO_COMPLETO=0
    fi

    # --- resumen ------------------------------------------------------------
    {
        echo "Resultado: $([ "$ABORTADO" = 1 ] && echo "ABORTADA — $MOTIVO_ABORTO" || echo "completa, sin abortar")"
        echo "k6 (código de salida): ${K6_ESTADO:-no arrancó}$([ "$ABORTADO" = 0 ] && [ "${K6_ESTADO:-0}" -ne 0 ] && echo " — FALLÓ: se cortó por sus umbrales de staging o no se ejecutó bien")"
        echo "Muestreo del VPS: $([ "$MUESTREO_COMPLETO" = 1 ] && echo "completo" || echo "INCOMPLETO (${SSH_ESTADO}): ${MOTIVO_MUESTREO}")"
        echo "Cierre del muestreo (hechos): $(grep -a "$CIERRE_MUESTREO\|Muestreo INTERRUMPIDO\|Muestreo SIN" "$DIR_SALIDA/muestreo.log" 2>/dev/null | tail -1 | sed 's/^=== //; s/ ===$//')"
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
