#!/bin/bash
# Pruebas de scripts/carga-staging-vigia.sh (REC-196/P33, D2). `curl`, `ssh`,
# `k6` y `gh` son ejecutables falsos en un PATH temporal: nada sale de la
# máquina. Lo que se fija, y lo que rompe cada mutación, está en el mensaje de
# cada caso.
set -uo pipefail

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
GUION="$DIR/carga-staging-vigia.sh"
source "$GUION"

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
fallo() { echo "FALLO: $*" >&2; exit 1; }

# ============================================================================
# A. evaluar_ventana con series sintéticas (epoch_ms,codigo,ms)
# ============================================================================
serie() { # $1 nº de muestras, $2 código, $3 ms → una por segundo desde t=1_000_000
    local i; for ((i = 0; i < $1; i++)); do echo "$((1000000 + i * 1000)),$2,$3"; done
}
evalua() { evaluar_ventana "$1" 2>&1 || true; }   # devuelve 1 al abortar: sin el `|| true`, pipefail rompería los `evalua | grep`

echo "=== A1: sin datos y con datos sanos -> OK ==="
: > "$TMP/s"; [ "$(evalua "$TMP/s")" = "OK" ] || fallo "un fichero vacío debe dar OK"
serie 40 200 50 > "$TMP/s"; [ "$(evalua "$TMP/s")" = "OK" ] || fallo "40 muestras sanas deben dar OK: $(evalua "$TMP/s")"
echo "OK"

echo "=== A2: cualquier 5xx aborta, también uno aislado ==="
{ serie 10 200 50; echo "1010000,503,80"; serie 5 200 50; } > "$TMP/s"
R="$(evalua "$TMP/s")"; case "$R" in "ABORTAR: respuesta 503"*) ;; *) fallo "un 503 debía abortar: $R" ;; esac
{ serie 3 200 50; echo "1003000,500,80"; } > "$TMP/s"
evalua "$TMP/s" | grep -q "ABORTAR: respuesta 500" || fallo "un 500 debía abortar"
{ serie 3 200 50; echo "1003000,599,80"; } > "$TMP/s"
evalua "$TMP/s" | grep -q "ABORTAR: respuesta 599" || fallo "un 599 debía abortar"
{ serie 3 200 50; echo "1003000,499,80"; } > "$TMP/s"
[ "$(evalua "$TMP/s")" = "OK" ] || fallo "un 499 aislado no es 5xx ni son 3 seguidos: debía dar OK"
echo "OK"

echo "=== A3: timeout (>3 s o sin respuesta) aborta ==="
{ serie 5 200 50; echo "1005000,000,3001"; } > "$TMP/s"
evalua "$TMP/s" | grep -q "no respondió o tardó" || fallo "un 000 debía abortar por timeout"
{ serie 5 200 50; echo "1005000,200,3500"; } > "$TMP/s"
evalua "$TMP/s" | grep -q "no respondió o tardó" || fallo "un 200 de 3500 ms debía abortar por timeout"
{ serie 5 200 50; echo "1005000,000,3000"; } > "$TMP/s"
evalua "$TMP/s" | grep -q "no respondió o tardó" || fallo "un 000 (curl sin respuesta, 3000 ms) debía abortar aunque no supere los 3000 ms"
{ serie 5 200 50; echo "1005000,200,3000"; serie 3 200 50; } > "$TMP/s"
[ "$(evalua "$TMP/s")" = "OK" ] || fallo "exactamente 3000 ms no supera el umbral: debía dar OK"
echo "OK"

echo "=== A4: 3 fallos seguidos abortan; 2 no ==="
{ serie 5 200 50; echo "1005000,404,50"; echo "1006000,404,50"; echo "1007000,404,50"; } > "$TMP/s"
evalua "$TMP/s" | grep -q "3 respuestas seguidas" || fallo "3 seguidas debían abortar: $(evalua "$TMP/s")"
{ serie 5 200 50; echo "1005000,404,50"; echo "1006000,404,50"; echo "1007000,200,50"; echo "1008000,404,50"; echo "1009000,404,50"; echo "1010000,200,50"; } > "$TMP/s"
[ "$(evalua "$TMP/s")" = "OK" ] || fallo "2 seguidas dos veces, separadas por un 200, no deben abortar: $(evalua "$TMP/s")"
echo "OK"

echo "=== A5: p95 sostenido > 1 s en 30 s: 2 valores lentos abortan, 1 no ==="
{ serie 38 200 50; echo "1038000,200,1500"; echo "1039000,200,1500"; } > "$TMP/s"   # 40 muestras, 2 lentas al final
evalua "$TMP/s" | grep -q "degradación sostenida" || fallo "2 lentas de 30 debían dar p95 > 1 s: $(evalua "$TMP/s")"
{ serie 39 200 50; echo "1039000,200,1500"; } > "$TMP/s"                            # 1 lenta
[ "$(evalua "$TMP/s")" = "OK" ] || fallo "1 valor atípico no es degradación sostenida: $(evalua "$TMP/s")"
{ serie 38 200 50; echo "1038000,200,1000"; echo "1039000,200,1000"; } > "$TMP/s"   # justo en el umbral
[ "$(evalua "$TMP/s")" = "OK" ] || fallo "p95 = 1000 ms exactos no supera el umbral: $(evalua "$TMP/s")"
echo "OK"

echo "=== A6: la ventana solo cuenta los últimos 30 s, y no se evalúa sin cubrirla ==="
{ serie 5 200 1500; serie 55 200 50 | awk -F, '{ $1 = $1 + 5000; print }' OFS=,; } > "$TMP/s"
[ "$(evalua "$TMP/s")" = "OK" ] || fallo "lentas fuera de los últimos 30 s no deben contar: $(evalua "$TMP/s")"
serie 20 200 1500 > "$TMP/s"   # 20 muestras, 19 s: ventana sin cubrir
[ "$(evalua "$TMP/s")" = "OK" ] || fallo "sin la ventana cubierta no se evalúa el p95: $(evalua "$TMP/s")"
serie 12 200 1500 > "$TMP/s"; { cat "$TMP/s"; echo "1040000,200,1500"; } > "$TMP/s2"
[ "$(evalua "$TMP/s2")" = "OK" ] || fallo "con menos de 15 muestras no se evalúa el p95"
echo "OK"

echo "=== A7: tras una pausa del vigía, una lenta reciente no cuenta como ventana ==="
{ serie 20 200 50; echo "1200000,200,1500"; } > "$TMP/s"   # 20 filas viejas y UNA reciente, lenta
[ "$(evalua "$TMP/s")" = "OK" ] || fallo "con una sola muestra en los últimos 30 s no hay ventana que evaluar: $(evalua "$TMP/s")"
# Muestras dispersas: 3 en 30 s tras una pausa larga, con 20 filas viejas. Cubren el
# tiempo pero no llegan al mínimo de 15 muestras DENTRO de la ventana.
{ serie 20 200 50; echo "1200000,200,50"; echo "1215000,200,1500"; echo "1230000,200,1500"; } > "$TMP/s"
[ "$(evalua "$TMP/s")" = "OK" ] || fallo "3 muestras en la ventana no bastan para evaluar el p95: $(evalua "$TMP/s")"
echo "OK"

# ============================================================================
# B. Orquestación con ejecutables falsos
# ============================================================================
BIN="$TMP/bin"; mkdir -p "$BIN"
cat > "$BIN/curl" <<'EOF'
#!/bin/bash
# Falso: staging siempre sano; producción según CURL_PROD_MODO (fichero) y
# CURL_PROD_5XX_DESDE (nº de llamada a producción a partir del cual da 503).
url="${@: -1}"
case "$url" in
  *staging*) echo "200 0.050"; exit 0 ;;
esac
n=$(cat "$FAKE_DIR/curl.n" 2>/dev/null || echo 0); n=$((n + 1)); echo "$n" > "$FAKE_DIR/curl.n"
if [ -n "${CURL_PROD_LENTO:-}" ]; then echo "200 1.500"; exit 0; fi
if [ -n "${CURL_PROD_5XX_DESDE:-}" ] && [ "$n" -ge "$CURL_PROD_5XX_DESDE" ]; then echo "503 0.100"; exit 0; fi
echo "200 0.050"
EOF
cat > "$BIN/ssh" <<'EOF'
#!/bin/bash
orden="${@: -1}"
echo "$orden" >> "$FAKE_DIR/ssh.calls"
# El muestreo LARGO (no la verificación previa de 10 s) puede fallar de dos formas.
if [ "$orden" != "muestreo-memoria 10 2" ]; then
    case "${FAKE_SSH_LARGO:-ok}" in
        muere) echo "=== Muestreo de memoria (REC-196/P33): $orden ==="; exit 255 ;;
        # Sale con 0 y SIN cerrar la ventana, pasados unos segundos (durante la recuperación).
        muere_callado) echo "=== Muestreo de memoria (REC-196/P33): $orden ==="; sleep 4; exit 0 ;;
        cierra_mal) SSH_SALIR=255 ;;
    esac
fi
case "${FAKE_SSH_MODO:-ok}" in
  viejo) echo "Entorno no permitido: 'muestreo-memoria'"; exit 1 ;;
  despliegue) echo "Hay un despliegue en curso (o el cerrojo no se puede leer): no se muestrea." >&2; exit 3 ;;
esac
echo "=== Muestreo de memoria (REC-196/P33): $orden ==="
echo "--- muestra 1 ---"
echo "caemanager-app mem=210MiB / 3GiB 6.8% cpu=1%"
echo "caemanager-seq mem=0.25GiB / 3.7GiB 6.6% cpu=0%"
echo "--- muestra 2 ---"
echo "caemanager-app mem=320MiB / 3GiB 10.4% cpu=9%"
echo "caemanager-seq mem=250MiB / 3.7GiB 6.6% cpu=0%"
echo "=== Estado final de los contenedores (memory.peak = pico de TODA la vida del contenedor) ==="
echo "--- caemanager-app ---"
echo "memory.peak: 555555 "
echo "=== Fin del muestreo: 2 muestras ==="
exit "${SSH_SALIR:-0}"
EOF
cat > "$BIN/k6" <<'EOF'
#!/bin/bash
echo "$@" > "$FAKE_DIR/k6.args"
trap 'touch "$FAKE_DIR/k6.term"; exit 143' TERM
limite="${FAKE_K6_SEGUNDOS:-30}"; inicio=$SECONDS
while [ $((SECONDS - inicio)) -lt "$limite" ]; do sleep 0.2; done
exit "${FAKE_K6_EXIT:-0}"
EOF
cat > "$BIN/gh" <<'EOF'
#!/bin/bash
n=$(cat "$FAKE_DIR/gh.n" 2>/dev/null || echo 0); n=$((n + 1)); echo "$n" > "$FAKE_DIR/gh.n"
if [ -n "${FAKE_GH_FALLA_DESDE:-}" ] && [ "$n" -ge "$FAKE_GH_FALLA_DESDE" ]; then echo "gh: HTTP 502" >&2; exit 1; fi
echo "${FAKE_GH_N:-0}"
EOF
chmod +x "$BIN"/*

lanzar() { # env... -> ejecuta main() del guion en un subshell limpio; deja el código en $CODIGO
    rm -rf "$TMP/f"; mkdir -p "$TMP/f"
    CODIGO=0
    (
        export FAKE_DIR="$TMP/f" PATH="$BIN:$PATH" DIR_SALIDA="$TMP/f/out"
        export VPS_HOST=203.0.113.9 URL_PRODUCCION=https://app.example.test URL_STAGING=https://staging.example.test
        export CONFIRMACION="CARGAR STAGING" VUS_MAX=20 SONDEO_S=0.2 BASE_S=2 CARGA_S=5 RECUP_S=1 FAKE_K6_SEGUNDOS=3
        unset COMPROBAR_DESPLIEGUES
        for kv in "$@"; do export "$kv"; done
        bash "$GUION" 2>"$TMP/f/stderr" >"$TMP/f/stdout"
    ) || CODIGO=$?
}
ejecutado() { [ -e "$TMP/f/$1" ]; }

echo "=== B1: sin la confirmación exacta no se hace nada ==="
for conf in "" "cargar staging" "CARGAR STAGING " "SI"; do
    lanzar "CONFIRMACION=$conf"
    [ "$CODIGO" -eq 2 ] || fallo "confirmación [$conf]: debía salir con 2, salió con $CODIGO"
    ! ejecutado curl.n && ! ejecutado ssh.calls && ! ejecutado k6.args || fallo "confirmación [$conf]: hizo algo pese a no estar confirmado"
done
echo "OK"

echo "=== B2: URLs y parámetros no válidos ==="
lanzar "URL_STAGING=https://app.example.test";           [ "$CODIGO" -eq 2 ] || fallo "URL_STAGING sin 'staging.' debía dar 2 (dio $CODIGO)"
lanzar "URL_PRODUCCION=https://staging.example.test";    [ "$CODIGO" -eq 2 ] || fallo "URL_PRODUCCION = staging debía dar 2 (dio $CODIGO)"
lanzar "URL_PRODUCCION=http://app.example.test";         [ "$CODIGO" -eq 2 ] || fallo "URL_PRODUCCION http debía dar 2 (dio $CODIGO)"
lanzar "VUS_MAX=61";                                     [ "$CODIGO" -eq 2 ] || fallo "VUS_MAX=61 debía dar 2 (dio $CODIGO)"
lanzar "VUS_MAX=4";                                      [ "$CODIGO" -eq 2 ] || fallo "VUS_MAX=4 debía dar 2 (dio $CODIGO)"
lanzar "VUS_MAX=20; id";                                 [ "$CODIGO" -eq 2 ] || fallo "VUS_MAX con inyección debía dar 2 (dio $CODIGO)"
lanzar "BASE_S=30" "CARGA_S=300" "RECUP_S=76";           [ "$CODIGO" -eq 2 ] || fallo "ventana de muestreo de 421 s debía dar 2 (dio $CODIGO)"
echo "OK"

echo "=== B3: ejecución completa sin abortar ==="
lanzar
[ "$CODIGO" -eq 0 ] || fallo "debía terminar con 0, terminó con $CODIGO: $(cat "$TMP/f/stderr") $(tail -5 "$TMP/f/stdout")"
grep -q "BASE_URL=https://staging.example.test" "$TMP/f/k6.args" || fallo "k6 no apunta a staging: $(cat "$TMP/f/k6.args")"
grep -q "VUS_MAX=20" "$TMP/f/k6.args" || fallo "k6 no recibió VUS_MAX"
! grep -q "app.example.test" "$TMP/f/k6.args" || fallo "k6 recibió la URL de producción"
[ "$(sed -n 1p "$TMP/f/ssh.calls")" = "muestreo-memoria 10 2" ] || fallo "la verificación previa del muestreo debía ser 'muestreo-memoria 10 2': $(sed -n 1p "$TMP/f/ssh.calls")"
[ "$(sed -n 2p "$TMP/f/ssh.calls")" = "muestreo-memoria 23 3" ] || fallo "el muestreo largo debía ser BASE+CARGA+MARGEN+RECUP = 2+5+15+1 = 23 s cada 3: $(sed -n 2p "$TMP/f/ssh.calls")"
grep -q "completa, sin abortar" "$TMP/f/out/resumen.txt" || fallo "el resumen no dice que fue completa"
grep -Eq "caemanager-app +320.0 MiB" "$TMP/f/out/resumen.txt" || fallo "el resumen no da el máximo de app (320 MiB): $(cat "$TMP/f/out/resumen.txt")"
grep -Eq "caemanager-seq +256.0 MiB" "$TMP/f/out/resumen.txt" || fallo "el resumen no convierte GiB a MiB (0.25GiB = 256): $(cat "$TMP/f/out/resumen.txt")"
grep -q "memory.peak: 555555" "$TMP/f/out/resumen.txt" || fallo "el resumen no trae el memory.peak final"
! ejecutado k6.term || fallo "se mandó SIGTERM a k6 en una ejecución sin abortar"
echo "OK"

echo "=== B4: un 5xx en producción durante la carga aborta y PARA k6 ==="
# 5 sondeos previos de producción + ~10 de línea base (2 s a 0,2 s) -> el 503 llega con k6 ya en marcha.
lanzar "CURL_PROD_5XX_DESDE=22" "FAKE_K6_SEGUNDOS=30"
[ "$CODIGO" -eq 3 ] || fallo "debía terminar con 3 (abortada), terminó con $CODIGO: $(tail -5 "$TMP/f/stdout")"
ejecutado k6.args || fallo "el 5xx debía llegar con k6 ya arrancado (ajusta CURL_PROD_5XX_DESDE)"
ejecutado k6.term || fallo "k6 no recibió SIGTERM tras el aborto: la carga seguiría empujando"
grep -q "ABORTADA — ABORTAR: respuesta 503" "$TMP/f/out/resumen.txt" || fallo "el resumen no recoge el motivo: $(cat "$TMP/f/out/resumen.txt")"
grep -q "Parando k6" "$TMP/f/stdout" || fallo "no registró que paraba k6"
echo "OK"

echo "=== B5: un 5xx en la línea base aborta sin llegar a arrancar k6 ==="
# Las 5 primeras llamadas a producción son la verificación previa; la 6.ª es el primer sondeo de la línea base.
lanzar "CURL_PROD_5XX_DESDE=6"
[ "$CODIGO" -eq 3 ] || fallo "debía terminar con 3, terminó con $CODIGO"
! ejecutado k6.args || fallo "k6 arrancó pese a que producción ya estaba mal en la línea base"
echo "OK"

echo "=== B6: verificación previa fallida -> no se empieza ==="
lanzar "CURL_PROD_5XX_DESDE=1"
[ "$CODIGO" -eq 4 ] || fallo "debía terminar con 4, terminó con $CODIGO"
! ejecutado k6.args && ! ejecutado ssh.calls || fallo "arrancó algo pese a la verificación previa fallida"
lanzar "CURL_PROD_LENTO=1"
[ "$CODIGO" -eq 4 ] || fallo "con producción lenta (1,5 s) en la verificación previa debía terminar con 4, terminó con $CODIGO"
! ejecutado k6.args || fallo "k6 arrancó con producción ya lenta"
echo "OK"

echo "=== B7: el VPS sin el modo, o con un despliegue en curso -> no se empieza ==="
lanzar "FAKE_SSH_MODO=viejo"
[ "$CODIGO" -eq 5 ] || fallo "VPS sin el modo: debía terminar con 5, terminó con $CODIGO"
! ejecutado k6.args || fallo "k6 arrancó sin poder medir memoria"
grep -q "dos despliegues" "$TMP/f/stderr" || fallo "no explica lo de los dos despliegues"
lanzar "FAKE_SSH_MODO=despliegue"
[ "$CODIGO" -eq 6 ] || fallo "despliegue en curso: debía terminar con 6, terminó con $CODIGO"
! ejecutado k6.args || fallo "k6 arrancó con un despliegue en curso"
echo "OK"

echo "=== B8: COMPROBAR_DESPLIEGUES consulta a GitHub antes de empezar ==="
lanzar "COMPROBAR_DESPLIEGUES=1" "FAKE_GH_N=1"
[ "$CODIGO" -eq 6 ] || fallo "con despliegues en marcha debía terminar con 6, terminó con $CODIGO"
! ejecutado k6.args || fallo "k6 arrancó con un despliegue en marcha"
lanzar "COMPROBAR_DESPLIEGUES=1" "FAKE_GH_N=0"
[ "$CODIGO" -eq 0 ] || fallo "sin despliegues debía completar (dio $CODIGO)"
echo "OK"

echo "=== B9: si GitHub deja de responder durante la carga, aborta (falla cerrado) ==="
# Las 2 primeras llamadas a gh son la verificación previa (in_progress + queued); a partir de la 3.ª falla.
lanzar "COMPROBAR_DESPLIEGUES=1" "FAKE_GH_FALLA_DESDE=3" "FAKE_K6_SEGUNDOS=40"
[ "$CODIGO" -eq 3 ] || fallo "sin respuesta de GitHub debía terminar con 3, terminó con $CODIGO: $(tail -4 "$TMP/f/stdout")"
grep -q "no se puede consultar a GitHub" "$TMP/f/out/resumen.txt" || fallo "el resumen no recoge el motivo: $(head -3 "$TMP/f/out/resumen.txt")"
ejecutado k6.args && { ejecutado k6.term || fallo "k6 seguía vivo tras el aborto"; }
echo "OK"

echo "=== B10: un k6 que falla sin abortar producción deja el resultado en rojo ==="
lanzar "FAKE_K6_EXIT=99"
[ "$CODIGO" -eq 7 ] || fallo "k6 con código 99 debía dar 7, dio $CODIGO"
grep -q "FALLÓ" "$TMP/f/out/resumen.txt" || fallo "el resumen no marca el fallo de k6: $(cat "$TMP/f/out/resumen.txt")"
lanzar "FAKE_K6_EXIT=0"
[ "$CODIGO" -eq 0 ] || fallo "k6 con código 0 debía dar 0, dio $CODIGO"
echo "OK"

echo "=== B11: si el muestreo del VPS se pierde, no se carga (o se para) ==="
lanzar "FAKE_SSH_LARGO=muere" "FAKE_K6_SEGUNDOS=40"
[ "$CODIGO" -eq 3 ] || fallo "muestreo perdido debía terminar con 3, terminó con $CODIGO: $(tail -4 "$TMP/f/stdout")"
grep -q "muestreo de memoria del VPS se ha perdido" "$TMP/f/out/resumen.txt" || fallo "el resumen no recoge el motivo"
ejecutado k6.args && { ejecutado k6.term || fallo "k6 siguió vivo con el muestreo perdido"; }
echo "OK"

echo "=== B12: muestreo que cierra la ventana pero sale con error -> resultado en rojo (8) ==="
lanzar "FAKE_SSH_LARGO=cierra_mal"
[ "$CODIGO" -eq 8 ] || fallo "muestreo con salida 255 debía dar 8, dio $CODIGO: $(tail -4 "$TMP/f/stdout")"
grep -q "INCOMPLETO" "$TMP/f/out/resumen.txt" || fallo "el resumen no marca el muestreo como incompleto"
echo "OK"

echo "=== B13: un muestreo que sale con 0 pero sin cerrar la ventana también es incompleto ==="
# Muere durante el tramo de recuperación (donde el vigía ya no evalúa): solo lo ve la comprobación final.
lanzar "FAKE_SSH_LARGO=muere_callado" "BASE_S=1" "FAKE_K6_SEGUNDOS=1" "RECUP_S=6"
[ "$CODIGO" -eq 8 ] || fallo "muestreo sin cierre de ventana debía dar 8, dio $CODIGO: $(tail -4 "$TMP/f/stdout")"
grep -q "INCOMPLETO" "$TMP/f/out/resumen.txt" || fallo "el resumen no marca el muestreo como incompleto"
echo "OK"

echo "TODAS LAS PRUEBAS PASARON"
