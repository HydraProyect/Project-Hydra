#!/bin/bash
# Verifica en el CUERPO de una PR la sección de paso operativo que el check
# "Gobernanza — metadatos de PR" no cubría hasta ahora (registro del turno
# nocturno 2026-09-17, PROTOCOLO-TURNO-NOCTURNO.md § 8):
#
#   #674 añadió el rol `cae_app_aprovisionamiento` en
#   deploy/bootstrap/roles-de-cluster.sql; ningún adaptador de despliegue
#   ejecuta ese fichero, así que staging quedó roto (42704) hasta que alguien
#   aplicó el GRANT a mano. Regla: toda PR que TOQUE
#   deploy/bootstrap/roles-de-cluster.sql lleva en el cuerpo una sección
#   titulada exactamente "## Paso operativo en servidores" que nombre
#   "staging" y "producción".
#
# Qué NO hace: no interpreta si el paso operativo descrito es correcto —
# eso lo decide una persona. Solo comprueba que la sección exista, no esté
# vacía y nombre los dos entornos.
#
# Uso:
#   scripts/verificar-gobernanza-pr.sh <fichero-cuerpo> <fichero-ficheros>
#
# <fichero-cuerpo>: el cuerpo de la PR tal cual (UTF-8, un fichero).
# <fichero-ficheros>: un path por línea, los ficheros que toca la PR. Debe
#   incluir tanto `.filename` como `.previous_filename` de cada entrada
#   (`gh api .../pulls/N/files --paginate --jq '.[] | .filename,
#   (.previous_filename // empty)'`) — si no, un rename que saque el fichero
#   vigilado de su ruta escapa de la regla sin que este guion pueda verlo:
#   solo mira lo que le llega, y la ruta anterior no está en `.filename`.
#
# Ver scripts/verificar-gobernanza-pr.tests.sh para la prueba sin red.
set -euo pipefail

FICHERO_ROLES_CLUSTER="deploy/bootstrap/roles-de-cluster.sql"

# La API de PRs de GitHub trunca la respuesta de /files en 3000 entradas,
# pagine lo que pagine (documentado, no un límite nuestro): con una PR de ese
# tamaño el guion podría no ver el fichero vigilado aunque esté en el diff
# real, y "no toca el fichero" sería un falso negativo, no una ausencia
# comprobada (§3 del protocolo de verificación: un instrumento que no puede
# observar la propiedad no cuenta como evidencia). Falla cerrado en vez de
# dar un OK que no puede respaldar.
LIMITE_PAGINACION_GH=3000

CUERPO="${1:-}"
FICHEROS="${2:-}"

if [[ -z "$CUERPO" || ! -f "$CUERPO" || -z "$FICHEROS" || ! -f "$FICHEROS" ]]; then
  echo "Uso: $0 <fichero-cuerpo> <fichero-ficheros-cambiados>" >&2
  exit 2
fi

# Extrae el contenido de la sección "## <titulo>" hasta el siguiente
# encabezado de nivel 2 o el final del cuerpo. Un encabezado siguiente se
# reconoce como "##" solo, o "##" seguido de espacio o tabulador (CommonMark
# admite ambos) — "## " a secas no cortaba "##\tOtra sección", y esa sección
# vacía podía heredar en silencio el contenido de la que viene después. El
# título objetivo se compara por igualdad de texto (no regex) para no tener
# que escapar tildes ni caracteres especiales.
extraer_seccion() {
  local fichero="$1" titulo="$2"
  awk -v titulo="$titulo" '
    { linea = $0; sub(/[[:space:]]+$/, "", linea) }
    !encontrada && linea == titulo { encontrada = 1; next }
    encontrada && $0 ~ /^##([ \t]|$)/ { exit }
    encontrada { print }
  ' "$fichero"
}

seccion_no_vacia() {
  printf '%s' "$1" | grep -qE '[^[:space:]]'
}

PROBLEMAS=0

NUM_FICHEROS=$(grep -c . "$FICHEROS" || true)
if (( NUM_FICHEROS >= LIMITE_PAGINACION_GH )); then
  echo "PROBLEMA  Esta PR toca $NUM_FICHEROS ficheros o más — la API de GitHub trunca" \
       "/pulls/N/files en $LIMITE_PAGINACION_GH y no se puede confirmar si toca o no" \
       "$FICHERO_ROLES_CLUSTER. Revisar el diff a mano."
  PROBLEMAS=$((PROBLEMAS + 1))
elif grep -qxF "$FICHERO_ROLES_CLUSTER" "$FICHEROS"; then
  SECCION="$(extraer_seccion "$CUERPO" "## Paso operativo en servidores")"
  if ! seccion_no_vacia "$SECCION"; then
    echo "PROBLEMA  Esta PR toca $FICHERO_ROLES_CLUSTER pero el cuerpo no tiene la sección" \
         "'## Paso operativo en servidores' (o está vacía). Registro: el rol" \
         "cae_app_aprovisionamiento de #674 rompió staging porque ningún adaptador de" \
         "despliegue ejecuta roles-de-cluster.sql — el paso manual hay que declararlo."
    PROBLEMAS=$((PROBLEMAS + 1))
  elif ! printf '%s' "$SECCION" | grep -qi 'staging'; then
    echo "PROBLEMA  La sección 'Paso operativo en servidores' no menciona 'staging'."
    PROBLEMAS=$((PROBLEMAS + 1))
  elif ! printf '%s' "$SECCION" | grep -qi 'producci'; then
    echo "PROBLEMA  La sección 'Paso operativo en servidores' no menciona 'producción'."
    PROBLEMAS=$((PROBLEMAS + 1))
  else
    echo "OK        Paso operativo de roles de clúster: sección presente, nombra staging y producción."
  fi
else
  echo "OK        No toca $FICHERO_ROLES_CLUSTER — la regla no aplica."
fi

echo
echo "Problemas: $PROBLEMAS"

if (( PROBLEMAS > 0 )); then
  exit 1
fi
