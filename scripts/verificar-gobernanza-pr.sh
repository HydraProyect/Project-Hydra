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
# <fichero-ficheros>: un path por línea, los ficheros que toca la PR
#   (`gh api .../pulls/N/files --paginate --jq '.[].filename'`).
#
# Ver scripts/verificar-gobernanza-pr.tests.sh para la prueba sin red.
set -euo pipefail

FICHERO_ROLES_CLUSTER="deploy/bootstrap/roles-de-cluster.sql"

CUERPO="${1:-}"
FICHEROS="${2:-}"

if [[ -z "$CUERPO" || ! -f "$CUERPO" || -z "$FICHEROS" || ! -f "$FICHEROS" ]]; then
  echo "Uso: $0 <fichero-cuerpo> <fichero-ficheros-cambiados>" >&2
  exit 2
fi

# Extrae el contenido de la sección "## <titulo>" hasta el siguiente "## " o
# el final del cuerpo. Compara por igualdad de texto (no regex) para no tener
# que escapar tildes ni caracteres especiales del título.
extraer_seccion() {
  local fichero="$1" titulo="$2"
  awk -v titulo="$titulo" '
    { linea = $0; sub(/[[:space:]]+$/, "", linea) }
    !encontrada && linea == titulo { encontrada = 1; next }
    encontrada && index($0, "## ") == 1 { exit }
    encontrada { print }
  ' "$fichero"
}

seccion_no_vacia() {
  printf '%s' "$1" | grep -qE '[^[:space:]]'
}

PROBLEMAS=0

if grep -qxF "$FICHERO_ROLES_CLUSTER" "$FICHEROS"; then
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
