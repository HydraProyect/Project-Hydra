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

echo "TODAS LAS PRUEBAS PASARON"
