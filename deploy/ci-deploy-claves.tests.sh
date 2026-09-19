#!/bin/bash
# Prueba de la capacidad por clave SSH de ci-deploy.sh (D6, 2026-09-19):
# `--clave staging` solo admite staging y muestreo-memoria; `--clave
# produccion`, solo produccion y secretos; sin argumentos, todo (modo de
# compatibilidad); cualquier otra forma de argumentos, nada.
#
# Dos instrumentos sobre el MISMO fichero, sin copiar su lógica:
#   - unitario: `source` de ci-deploy.sh (la guarda de BASH_SOURCE del final
#     impide que main() se dispare) y llamada a exigir_modo_permitido_para_clave
#     con cada combinación de modo y argumentos;
#   - de caja negra: ejecuta el guion entero con SSH_ORIGINAL_COMMAND, para
#     comprobar que main() llama de verdad a la guarda ANTES de cualquier efecto
#     (el cerrojo /opt/talveg/deploy/.ci-deploy.lock es el primero: si la salida
#     lo menciona, la guarda dejó pasar la orden; si no, la rechazó antes).
#     Solo corre si /opt/talveg no existe (no despliega nada en un VPS real).
#
# Al final, la prueba de sensibilidad: una copia mutada del guion por cada
# guarda (ampliar los modos de una clave, quitar la comprobación de `--clave`,
# aflojar el rechazo de argumentos raros, quitar la llamada de main...) tiene
# que ponerse en rojo, y por el caso esperado — un rojo por otro motivo o un
# verde con la mutación puesta hacen fallar esta prueba.
set -uo pipefail

DIR_GUION="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
GUION_REAL="$DIR_GUION/ci-deploy.sh"
DIR="$(mktemp -d)"
trap 'rm -rf "$DIR"' EXIT

GUION_ACTUAL="$GUION_REAL"
FALLOS=()
COMPROBACIONES=0

fallo() { FALLOS+=("FALLO[$1]: $2"); }

# rc de exigir_modo_permitido_para_clave con SSH_ORIGINAL_COMMAND="$2" y los
# argumentos restantes (los de `ci-deploy.sh`, no los del cliente SSH).
rc_unitario() {
    local modo="$1"; shift
    ( source "$GUION_ACTUAL"; SSH_ORIGINAL_COMMAND="$modo"; exigir_modo_permitido_para_clave "$@" ) >/dev/null 2>&1
}
salida_unitaria() {
    local modo="$1"; shift
    ( source "$GUION_ACTUAL"; SSH_ORIGINAL_COMMAND="$modo"; exigir_modo_permitido_para_clave "$@" ) 2>&1 >/dev/null
}

permite() {
    local id="$1"; shift
    COMPROBACIONES=$((COMPROBACIONES + 1))
    rc_unitario "$@" || fallo "$id" "debía permitir: modo='$1' args=(${*:2})"
}
rechaza() {
    local id="$1"; shift
    COMPROBACIONES=$((COMPROBACIONES + 1))
    if rc_unitario "$@"; then fallo "$id" "debía rechazar: modo='$1' args=(${*:2})"; fi
}

casos_unitarios() {
    # --- clave de staging: staging y muestreo, nunca producción ni secretos ---
    permite  staging-permite-staging      "staging abc123"   --clave staging
    permite  staging-permite-muestreo     "muestreo-memoria 60 5" --clave staging
    rechaza  staging-rechaza-produccion   "produccion abc123" --clave staging
    rechaza  staging-rechaza-secretos     "secretos"         --clave staging
    rechaza  staging-rechaza-shell        "bash"             --clave staging
    rechaza  staging-rechaza-vacio        ""                 --clave staging
    rechaza  staging-rechaza-encadenado   "staging;produccion abc" --clave staging
    # Los espacios y tabuladores delante no cambian el modo (main lee igual).
    rechaza  staging-rechaza-espacio-previo " produccion abc" --clave staging
    rechaza  staging-rechaza-tab-previo   $'\tproduccion abc' --clave staging

    # --- clave de producción: producción y secretos, nunca staging ni muestreo ---
    permite  produccion-permite-produccion "produccion abc123" --clave produccion
    permite  produccion-permite-secretos   "secretos"          --clave produccion
    rechaza  produccion-rechaza-staging    "staging abc123"    --clave produccion
    rechaza  produccion-rechaza-muestreo   "muestreo-memoria 60 5" --clave produccion
    rechaza  produccion-rechaza-shell      "bash"              --clave produccion
    rechaza  produccion-rechaza-vacio      ""                  --clave produccion

    # --- sin argumentos: la clave única de hoy sigue funcionando (transición) ---
    permite  compat-permite-staging        "staging abc123"
    permite  compat-permite-produccion     "produccion abc123"
    permite  compat-permite-secretos       "secretos"
    permite  compat-permite-muestreo       "muestreo-memoria 60 5"
    rechaza  compat-rechaza-shell          "bash"
    rechaza  compat-rechaza-vacio          ""

    # --- cualquier otra forma de argumentos falla cerrado, incluso con un modo
    # --- que esa clave, bien escrita, sí admitiría ---
    rechaza  args-clave-desconocida        "staging abc123"    --clave todas
    rechaza  args-clave-mayusculas         "staging abc123"    --clave STAGING
    rechaza  args-clave-vacia              "staging abc123"    --clave ""
    rechaza  args-sin-valor                "staging abc123"    --clave
    rechaza  args-igual-pegado             "staging abc123"    --clave=staging
    rechaza  args-solo-valor               "staging abc123"    staging
    rechaza  args-opcion-distinta          "staging abc123"    --key staging
    rechaza  args-tres-tokens              "staging abc123"    --clave staging extra
    rechaza  args-dos-claves               "produccion abc123" --clave produccion --clave staging
    rechaza  args-produccion-con-basura    "produccion abc123" --clave produccion staging
}

# Mensajes: distinguen «modo conocido pero no de esta clave» de «modo
# desconocido» (este último conserva el texto de siempre, que el workflow
# deploy.yml busca para tolerar una copia antigua del guion en el VPS).
casos_de_mensaje() {
    local m
    COMPROBACIONES=$((COMPROBACIONES + 1))
    m="$(salida_unitaria "produccion abc" --clave staging)"
    case "$m" in
        *"Modo 'produccion' no permitido para esta clave"*) ;;
        *) fallo mensaje-modo-prohibido "mensaje inesperado: $m" ;;
    esac
    COMPROBACIONES=$((COMPROBACIONES + 1))
    m="$(salida_unitaria "bash" --clave staging)"
    case "$m" in
        *"Entorno no permitido: 'bash'"*) ;;
        *) fallo mensaje-modo-desconocido "mensaje inesperado: $m" ;;
    esac
    COMPROBACIONES=$((COMPROBACIONES + 1))
    m="$(salida_unitaria "staging abc" --clave todas)"
    case "$m" in
        *"Argumentos del comando forzado no válidos"*) ;;
        *) fallo mensaje-argumentos "mensaje inesperado: $m" ;;
    esac
}

# Caja negra: el guion entero, con SSH_ORIGINAL_COMMAND puesto por «el
# servidor». Sin /opt/talveg, lo que pasa la guarda muere en el primer efecto
# (abrir el cerrojo) y lo que no la pasa ni llega ahí.
SALIDA=""
ejecutar_guion() {
    local modo="$1"; shift
    SALIDA="$(SSH_ORIGINAL_COMMAND="$modo" bash "$GUION_ACTUAL" "$@" 2>&1 </dev/null)"
}
caja_rechaza() {
    local id="$1" modo="$2"; shift 2
    COMPROBACIONES=$((COMPROBACIONES + 1))
    ejecutar_guion "$modo" "$@"
    case "$SALIDA" in
        *".ci-deploy.lock"*) fallo "$id" "la orden llegó al cerrojo, la guarda no la paró: $SALIDA" ;;
        *"no permitido"*|*"no válidos"*) ;;
        *) fallo "$id" "no hay mensaje de rechazo: $SALIDA" ;;
    esac
}
caja_pasa() {
    local id="$1" modo="$2"; shift 2
    COMPROBACIONES=$((COMPROBACIONES + 1))
    ejecutar_guion "$modo" "$@"
    case "$SALIDA" in
        *".ci-deploy.lock"*) ;;
        *) fallo "$id" "la guarda paró una orden legítima antes del cerrojo: $SALIDA" ;;
    esac
}

casos_caja_negra() {
    if [ -e /opt/talveg ]; then
        echo "AVISO: /opt/talveg existe (¿esto es el VPS?): se omite la caja negra para no desplegar nada." >&2
        fallo caja-negra-omitida "no se pudo ejercitar main()"
        return
    fi
    caja_rechaza caja-staging-produccion   "produccion abc123" --clave staging
    caja_rechaza caja-staging-secretos     "secretos"          --clave staging
    caja_rechaza caja-produccion-staging   "staging abc123"    --clave produccion
    caja_rechaza caja-args-invalidos       "staging abc123"    --clave todas
    caja_rechaza caja-compat-shell         "bash"
    caja_pasa    caja-staging-staging      "staging abc123"    --clave staging
    caja_pasa    caja-produccion-secretos  "secretos"          --clave produccion
    caja_pasa    caja-produccion-produccion "produccion abc123" --clave produccion
    caja_pasa    caja-compat-produccion    "produccion abc123"
}

# Ejecuta todos los casos sobre GUION_ACTUAL y deja los fallos en FALLOS.
correr_todo() {
    FALLOS=()
    COMPROBACIONES=0
    casos_unitarios
    casos_de_mensaje
    casos_caja_negra
}

echo "=== Casos sobre ci-deploy.sh real ==="
GUION_ACTUAL="$GUION_REAL"
correr_todo
if [ "${#FALLOS[@]}" -ne 0 ]; then
    printf '%s\n' "${FALLOS[@]}" >&2
    echo "FALLO: ${#FALLOS[@]} de $COMPROBACIONES comprobaciones sobre el guion real" >&2
    exit 1
fi
echo "OK: $COMPROBACIONES comprobaciones sobre el guion real"

# --- Sensibilidad: una mutación por guarda ----------------------------------
# Cada fila: id # expresión sed # caso que tiene que ponerse en rojo.
# El sed se aplica a una copia; se exige que cambie el fichero, que siga
# parseando (`bash -n`: un fallo de sintaxis no demuestra sensibilidad) y que
# el caso indicado esté entre los que fallan.
MUTACIONES=(
  'staging-admite-produccion#s/^MODOS_CLAVE_STAGING="staging muestreo-memoria"/MODOS_CLAVE_STAGING="staging muestreo-memoria produccion"/#staging-rechaza-produccion'
  'staging-admite-secretos#s/^MODOS_CLAVE_STAGING="staging muestreo-memoria"/MODOS_CLAVE_STAGING="staging muestreo-memoria secretos"/#staging-rechaza-secretos'
  'staging-pierde-muestreo#s/^MODOS_CLAVE_STAGING="staging muestreo-memoria"/MODOS_CLAVE_STAGING="staging"/#staging-permite-muestreo'
  'produccion-admite-staging#s/^MODOS_CLAVE_PRODUCCION="produccion secretos"/MODOS_CLAVE_PRODUCCION="produccion secretos staging"/#produccion-rechaza-staging'
  'produccion-admite-muestreo#s/^MODOS_CLAVE_PRODUCCION="produccion secretos"/MODOS_CLAVE_PRODUCCION="produccion secretos muestreo-memoria"/#produccion-rechaza-muestreo'
  'produccion-pierde-secretos#s/^MODOS_CLAVE_PRODUCCION="produccion secretos"/MODOS_CLAVE_PRODUCCION="produccion"/#produccion-permite-secretos'
  'clave-desconocida-admitida#s/^                \*\)          return 1 ;;$/                *)          echo "$MODOS_SIN_ARGUMENTO" ;;/#args-clave-desconocida'
  'sin-comprobar-opcion#s/^            \[ "\$1" = "--clave" \] \|\| return 1$/            true/#args-opcion-distinta'
  'argumentos-raros-admitidos#s/^        \*\) return 1 ;;$/        *) echo "$MODOS_SIN_ARGUMENTO" ;;/#args-tres-tokens'
  'compat-retirada#s/^        0\) echo "\$MODOS_SIN_ARGUMENTO" ;;$/        0) return 1 ;;/#compat-permite-produccion'
  'compat-sin-modos-desconocidos#s/^MODOS_SIN_ARGUMENTO="staging produccion secretos muestreo-memoria"/MODOS_SIN_ARGUMENTO="staging produccion secretos muestreo-memoria bash"/#compat-rechaza-shell'
  'comparacion-siempre-cierta#s/^        if \[ "\$candidato" = "\$modo" \]; then$/        if true; then/#staging-rechaza-produccion'
  'main-sin-guarda#s/^exigir_modo_permitido_para_clave "\$@" \|\| exit 1$/true/#caja-staging-produccion'
  'mensaje-modo-prohibido-perdido#s/Modo .\$modo. no permitido para esta clave/Entorno no permitido: \x27$modo\x27/#mensaje-modo-prohibido'
)

echo "=== Sensibilidad: cada guarda, mutada, tiene que ponerse en rojo ==="
ERRORES_MUTACION=0
for fila in "${MUTACIONES[@]}"; do
    id="${fila%%#*}"; esperado="${fila##*#}"; expresion="${fila#*#}"; expresion="${expresion%#*}"
    copia="$DIR/mutada-$id.sh"
    if ! sed -E "$expresion" "$GUION_REAL" > "$copia"; then
        echo "FALLO[$id]: la expresión sed no se ejecutó (una copia vacía se pondría en rojo por cualquier motivo)" >&2
        ERRORES_MUTACION=$((ERRORES_MUTACION + 1))
        continue
    fi
    if cmp -s "$GUION_REAL" "$copia"; then
        echo "FALLO[$id]: la expresión sed no cambió el guion (mutación que no toca lo que dice)" >&2
        ERRORES_MUTACION=$((ERRORES_MUTACION + 1))
        continue
    fi
    if ! bash -n "$copia" 2>/dev/null; then
        echo "FALLO[$id]: la copia mutada no parsea — un fallo de sintaxis no demuestra sensibilidad" >&2
        ERRORES_MUTACION=$((ERRORES_MUTACION + 1))
        continue
    fi
    GUION_ACTUAL="$copia"
    correr_todo
    if [ "${#FALLOS[@]}" -eq 0 ]; then
        echo "FALLO[$id]: con la mutación puesta TODO sigue en verde — la guarda no está probada" >&2
        ERRORES_MUTACION=$((ERRORES_MUTACION + 1))
    elif ! printf '%s\n' "${FALLOS[@]}" | grep -q "^FALLO\[$esperado\]"; then
        echo "FALLO[$id]: se puso en rojo, pero no por el caso esperado ($esperado). Falló: $(printf '%s ' "${FALLOS[@]%%:*}")" >&2
        ERRORES_MUTACION=$((ERRORES_MUTACION + 1))
    else
        echo "OK: mutación '$id' → rojo por '$esperado' (${#FALLOS[@]} casos en rojo)"
    fi
done
GUION_ACTUAL="$GUION_REAL"

if [ "$ERRORES_MUTACION" -ne 0 ]; then
    echo "FALLO: $ERRORES_MUTACION mutaciones sin la respuesta esperada" >&2
    exit 1
fi
echo "OK: ${#MUTACIONES[@]} mutaciones, todas en rojo por el caso esperado"
