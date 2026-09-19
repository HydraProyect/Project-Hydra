#!/bin/bash
# Prueba de volcar_diagnostico_memoria, definida y ejecutada TAL CUAL en
# ci-deploy.sh (mismo criterio que ci-deploy-secretos.tests.sh: `source`,
# sin copiar la lógica). `docker`/`free` se mockean como funciones de shell
# — este entorno no tiene el daemon real.
#
# volcar_diagnostico_si_falla (quien llama a volcar_diagnostico_memoria) NO
# es testeable de la misma forma: sigue anidada dentro de main(), que no se
# ejecuta al hacer `source` (guarda de BASH_SOURCE al final del fichero) —
# invocar main() completo ejercitaría el `case "$ENTORNO"` entero contra
# SSH_ORIGINAL_COMMAND real, fuera del alcance de este test. El caso 2 de
# abajo comprueba en su lugar, por lectura del propio fichero fuente, que
# la llamada a volcar_diagnostico_memoria está DESPUÉS del bloque que hace
# `exit 1` si el despliegue no llega a sano — no dentro de él.
#
# No sustituye probar contra un VPS real: REC-198/P33 se mide en el
# próximo despliegue real, leyendo el log del paso "Desplegar SHA exacto a
# staging/producción" en GitHub Actions.
set -euo pipefail

DIR_GUION="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$DIR_GUION/ci-deploy.sh"

echo "=== Caso 1: volcar_diagnostico_memoria imprime free -h y docker stats ==="
docker() {
    case "$1" in
        stats)
            echo "NAME  MEM USAGE  MEM %"
            echo "app   100MiB / 3GiB   3.3%"
            ;;
        *) return 0 ;;
    esac
}
free() { echo "mock free -h: 4.0Gi total, 1.2Gi disponible"; }

SALIDA1="$(volcar_diagnostico_memoria 2>&1)"
echo "$SALIDA1" | grep -q "Diagnóstico de memoria (REC-198/P33)" \
    || { echo "FALLO: falta la cabecera del diagnóstico" >&2; echo "$SALIDA1" >&2; exit 1; }
echo "$SALIDA1" | grep -q "mock free -h" \
    || { echo "FALLO: la salida de 'free -h' no llegó a stdout" >&2; exit 1; }
echo "$SALIDA1" | grep -q "MEM USAGE" \
    || { echo "FALLO: la salida de 'docker stats' no llegó a stdout" >&2; exit 1; }
echo "OK: volcar_diagnostico_memoria imprime free -h y docker stats a stdout"

echo "=== Caso 2: la llamada vive DESPUÉS del exit 1 del bloque de 'up' no sano ==="
# La función tiene DOS bloques "exit 1" con la misma indentación: el del
# fallo de `build` y el del `up -d --wait` no sano. Un `grep | head -1`
# ingenuo coge el de `build` (el primero) — hallazgo de Codex sobre la
# primera versión de este caso: eso deja pasar una regresión que mueva la
# llamada a un punto intermedio (después de `build`, pero dentro o antes
# del bloque de `up`), que SÍ se ejecutaría con un despliegue no sano y
# este caso no lo habría detectado. Se ancla explícitamente a la línea de
# `up -d --wait` y se busca el `exit 1` que le sigue A ELLA, no el primero
# del fichero.
FICHERO_FUENTE="$DIR_GUION/ci-deploy.sh"
LINEA_UP="$(grep -n 'docker compose "\${args\[@\]}" up -d --wait' "$FICHERO_FUENTE" | head -1 | cut -d: -f1)"
[ -n "$LINEA_UP" ] || { echo "FALLO: no se encontró la línea de 'docker compose ... up -d --wait' — el fichero cambió de forma inesperada" >&2; exit 1; }
LINEA_EXIT="$(tail -n "+$LINEA_UP" "$FICHERO_FUENTE" | grep -n '^        exit 1$' | head -1 | cut -d: -f1)"
[ -n "$LINEA_EXIT" ] || { echo "FALLO: no se encontró un 'exit 1' después de la línea de 'up -d --wait' (línea $LINEA_UP)" >&2; exit 1; }
LINEA_EXIT=$((LINEA_UP + LINEA_EXIT - 1))
LINEA_LLAMADA="$(grep -n '^    volcar_diagnostico_memoria$' "$FICHERO_FUENTE" | head -1 | cut -d: -f1)"
[ -n "$LINEA_LLAMADA" ] || { echo "FALLO: no se encontró la llamada a volcar_diagnostico_memoria" >&2; exit 1; }
[ "$LINEA_LLAMADA" -gt "$LINEA_EXIT" ] \
    || { echo "FALLO: volcar_diagnostico_memoria se llama en la línea $LINEA_LLAMADA, ANTES o EN el exit 1 del 'up' no sano (línea $LINEA_EXIT) — se ejecutaría con un despliegue roto" >&2; exit 1; }
echo "OK: la llamada (línea $LINEA_LLAMADA) vive después del exit 1 del bloque de 'up' no sano (línea $LINEA_EXIT)"

# ---------------------------------------------------------------------------
# volcar_pico_memoria_previo (REC-196/P33): corre ANTES del build y del `up`.
# El guion interno de `docker exec` se ejecuta DE VERDAD contra un cgroup
# falso en un directorio temporal (el mock de `docker exec` sustituye
# /sys/fs/cgroup por ese directorio), no se compara con una copia de su texto.
# `timeout` se sombrea con una función: el real es un binario externo y no
# vería las funciones `docker`/`free` de este test.
# ---------------------------------------------------------------------------
timeout() { shift; "$@"; }
RAIZ_CGROUP="$(mktemp -d)"
trap 'rm -rf "$RAIZ_CGROUP"' EXIT
free() { [ "${MOCK_EXEC_FALLA:-0}" = "1" ] && return 1; echo "mock free -m"; }

docker() {
    case "$1" in
        stats) [ "${MOCK_EXEC_FALLA:-0}" = "1" ] && return 1; echo "NAME  MEM USAGE / LIMIT  MEM %"; echo "caemanager-app  200MiB / 3GiB  6%" ;;
        ps) printf '%s\n' caemanager-app caemanager-db otro-contenedor ;;
        inspect) [ "${MOCK_EXEC_FALLA:-0}" = "1" ] && return 1; echo "iniciado=2026-09-19T00:00:00Z reinicios=0 oom_docker=false" ;;
        exec)
            [ "${MOCK_EXEC_FALLA:-0}" = "1" ] && return 1
            local guion="${*: -1}"
            sh -c "${guion//\/sys\/fs\/cgroup/$RAIZ_CGROUP}" ;;
        *) return 0 ;;
    esac
}

echo "=== Caso 3: el pico de cgroup v2 (memory.peak, memory.events, memory.stat) llega al log, solo de caemanager-* ==="
printf '%s\n' 987654321 > "$RAIZ_CGROUP/memory.peak"
printf '%s\n' 555555555 > "$RAIZ_CGROUP/memory.current"
printf '%s\n' 3221225472 > "$RAIZ_CGROUP/memory.max"
printf '%s\n' "low 0" "high 0" "max 0" "oom 0" "oom_kill 0" > "$RAIZ_CGROUP/memory.events"
printf '%s\n' "anon 111111" "file 222222" "kernel 5" "shmem 333" "file_mapped 44" > "$RAIZ_CGROUP/memory.stat"
SALIDA3="$(volcar_pico_memoria_previo 2>&1)"
echo "$SALIDA3" | grep -q "memory.peak: 987654321" \
    || { echo "FALLO: memory.peak no llegó al log" >&2; echo "$SALIDA3" >&2; exit 1; }
echo "$SALIDA3" | grep -q "oom_kill 0" \
    || { echo "FALLO: memory.events (oom_kill) no llegó al log" >&2; echo "$SALIDA3" >&2; exit 1; }
echo "$SALIDA3" | grep -q "anon 111111 file 222222 shmem 333 file_mapped 44" \
    || { echo "FALLO: memory.stat filtrado no llegó al log" >&2; echo "$SALIDA3" >&2; exit 1; }
echo "$SALIDA3" | grep -q -- "--- caemanager-db ---" \
    || { echo "FALLO: falta el contenedor caemanager-db" >&2; exit 1; }
if echo "$SALIDA3" | grep -q "otro-contenedor"; then
    echo "FALLO: se volcó un contenedor que no es caemanager-*" >&2; exit 1
fi
echo "$SALIDA3" | grep -q "iniciado=2026-09-19T00:00:00Z" \
    || { echo "FALLO: falta el instante de arranque (sin él el pico no se puede interpretar)" >&2; exit 1; }
echo "OK: memory.peak, memory.events, memory.stat e inicio llegan al log; solo caemanager-*"

echo "=== Caso 4: cgroup v1 (memory.max_usage_in_bytes) ==="
rm -f "$RAIZ_CGROUP"/memory.*
mkdir -p "$RAIZ_CGROUP/memory"
printf '%s\n' 424242424 > "$RAIZ_CGROUP/memory/memory.max_usage_in_bytes"
SALIDA4="$(volcar_pico_memoria_previo 2>&1)"
echo "$SALIDA4" | grep -q "memory/memory.max_usage_in_bytes: 424242424" \
    || { echo "FALLO: el pico de cgroup v1 no llegó al log" >&2; echo "$SALIDA4" >&2; exit 1; }
echo "OK: cgroup v1 cubierto"

echo "=== Caso 5: una lectura que falla NO tumba el despliegue (set -e activo) ==="
# Sin `||` a propósito: en la lista de un `||` bash IGNORA `set -e` dentro de
# la función, y un `|| true` borrado de en medio pasaría desapercibido.
# inherit_errexit hace que la sustitución de comandos vea el `set -e` del
# test, igual que la función lo ve dentro de main() en el despliegue real; si
# algo devuelve error, el test muere aquí con salida distinta de cero.
shopt -s inherit_errexit
MOCK_EXEC_FALLA=1
echo "(si el test muere en esta línea, alguna lectura de volcar_pico_memoria_previo NO tolera el fallo)"
SALIDA5="$(volcar_pico_memoria_previo 2>&1)"
echo "$SALIDA5" | grep -q "sin lectura de cgroup en caemanager-app" \
    || { echo "FALLO: no avisa de que faltó la lectura" >&2; echo "$SALIDA5" >&2; exit 1; }
MOCK_EXEC_FALLA=0
echo "OK: docker exec roto se tolera y se avisa"

echo "=== Caso 6: volcar_pico_memoria_previo se llama dentro de volcar_diagnostico_si_falla, ANTES del build ==="
LINEA_FUNC="$(grep -n '^volcar_diagnostico_si_falla() {' "$FICHERO_FUENTE" | head -1 | cut -d: -f1)"
LINEA_BUILD="$(grep -n 'docker compose "\${args\[@\]}" build' "$FICHERO_FUENTE" | head -1 | cut -d: -f1)"
LINEA_PREVIO="$(grep -n '^    volcar_pico_memoria_previo$' "$FICHERO_FUENTE" | head -1 | cut -d: -f1)"
[ -n "$LINEA_FUNC" ] && [ -n "$LINEA_BUILD" ] && [ -n "$LINEA_PREVIO" ] \
    || { echo "FALLO: no se localizó función/build/llamada (func=$LINEA_FUNC build=$LINEA_BUILD previo=$LINEA_PREVIO)" >&2; exit 1; }
[ "$LINEA_PREVIO" -gt "$LINEA_FUNC" ] && [ "$LINEA_PREVIO" -lt "$LINEA_BUILD" ] \
    || { echo "FALLO: la llamada (línea $LINEA_PREVIO) no está entre el inicio de volcar_diagnostico_si_falla ($LINEA_FUNC) y el build ($LINEA_BUILD) — mediría contenedores ya reemplazados" >&2; exit 1; }
echo "OK: la llamada (línea $LINEA_PREVIO) va antes del build (línea $LINEA_BUILD)"

echo "=== Caso 7: contadores del host (PSI, oom_kill) de un /proc falso ==="
PROC_FALSO="$(mktemp -d)"
trap 'rm -rf "$RAIZ_CGROUP" "$PROC_FALSO"' EXIT
mkdir -p "$PROC_FALSO/pressure"
printf '%s\n' "some avg10=0.00 avg60=0.10 avg300=0.20 total=777" "full avg10=0.00 avg60=0.00 avg300=0.00 total=55" > "$PROC_FALSO/pressure/memory"
printf '%s\n' "nr_free_pages 1" "oom_kill 3" "pgmajfault 4242" "allocstall_normal 9" "otra_cosa 1" > "$PROC_FALSO/vmstat"
printf '%s\n' "12345.67 9999.00" > "$PROC_FALSO/uptime"
SALIDA7="$(RAIZ_PROC="$PROC_FALSO" volcar_contadores_memoria_host "prueba" 2>&1)"
echo "$SALIDA7" | grep -q "Contadores de memoria del host (prueba)" \
    || { echo "FALLO: falta la cabecera con la etapa" >&2; echo "$SALIDA7" >&2; exit 1; }
echo "$SALIDA7" | grep -q "some avg10=0.00 avg60=0.10 avg300=0.20 total=777" \
    || { echo "FALLO: PSI no llegó al log" >&2; echo "$SALIDA7" >&2; exit 1; }
echo "$SALIDA7" | grep -q "oom_kill 3" \
    || { echo "FALLO: oom_kill de /proc/vmstat no llegó al log" >&2; echo "$SALIDA7" >&2; exit 1; }
echo "$SALIDA7" | grep -q "uptime_s: 12345.67" \
    || { echo "FALLO: uptime no llegó al log" >&2; echo "$SALIDA7" >&2; exit 1; }
if echo "$SALIDA7" | grep -q "otra_cosa"; then
    echo "FALLO: se volcó un contador que no se pidió" >&2; exit 1
fi
echo "OK: PSI, oom_kill y uptime llegan al log"

echo "=== Caso 8: sin /proc/pressure ni vmstat (kernel sin PSI) no tumba el despliegue ==="
echo "(si el test muere en esta línea, volcar_contadores_memoria_host NO tolera la ausencia de ficheros)"
SALIDA8="$(RAIZ_PROC="$PROC_FALSO/no-existe" volcar_contadores_memoria_host "sin psi" 2>&1)"
echo "$SALIDA8" | grep -q "sin PSI" \
    || { echo "FALLO: no avisa de que falta PSI" >&2; echo "$SALIDA8" >&2; exit 1; }
rm -f "$PROC_FALSO/vmstat"
echo "(idem con /proc/vmstat sin coincidencias)"
printf '%s\n' "solo_otra_cosa 1" > "$PROC_FALSO/vmstat"
SALIDA8B="$(RAIZ_PROC="$PROC_FALSO" volcar_contadores_memoria_host "sin coincidencias" 2>&1)"
echo "OK: ausencia de PSI y grep sin coincidencias se toleran"

echo "=== Caso 9: los contadores se vuelcan antes del despliegue, tras el build (antes del up) y tras el up ==="
LINEA_TRAS_BUILD="$(grep -n 'volcar_contadores_memoria_host "tras el build"' "$FICHERO_FUENTE" | head -1 | cut -d: -f1)"
[ -n "$LINEA_TRAS_BUILD" ] || { echo "FALLO: falta el volcado 'tras el build'" >&2; exit 1; }
[ "$LINEA_TRAS_BUILD" -gt "$LINEA_BUILD" ] && [ "$LINEA_TRAS_BUILD" -lt "$LINEA_UP" ] \
    || { echo "FALLO: 'tras el build' (línea $LINEA_TRAS_BUILD) no está entre el build ($LINEA_BUILD) y el up ($LINEA_UP)" >&2; exit 1; }
grep -q 'volcar_contadores_memoria_host "antes del despliegue"' "$FICHERO_FUENTE" \
    || { echo "FALLO: falta el volcado 'antes del despliegue'" >&2; exit 1; }
grep -q 'volcar_contadores_memoria_host "tras el despliegue"' "$FICHERO_FUENTE" \
    || { echo "FALLO: falta el volcado 'tras el despliegue'" >&2; exit 1; }
echo "OK: tres puntos de lectura, el del build entre build y up"

echo "TODAS LAS PRUEBAS PASARON"
