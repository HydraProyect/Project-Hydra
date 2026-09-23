#!/bin/bash
# Verifica en el CUERPO de una PR las dos secciones de gobernanza que el
# check "Gobernanza — metadatos de PR" no cubría hasta ahora (registro del
# turno nocturno 2026-09-17, PROTOCOLO-TURNO-NOCTURNO.md § 8):
#
#   1. Paso operativo de roles de clúster (PR #683). #674 añadió el rol
#      `cae_app_aprovisionamiento` en deploy/bootstrap/roles-de-cluster.sql;
#      ningún adaptador de despliegue ejecuta ese fichero, así que staging
#      quedó roto (42704) hasta que alguien aplicó el GRANT a mano. Regla:
#      toda PR que TOQUE ese fichero lleva en el cuerpo una sección titulada
#      exactamente "## Paso operativo en servidores" que nombre "staging" y
#      "producción".
#   2. Revisión Codex en el cuerpo. #678 se abrió sin pasar por Codex y #673
#      dejó un marcador provisional ("2ª pasada — (resultado abajo)") sin
#      rellenar. Regla: toda PR (salvo dependabot[bot]) lleva una sección
#      titulada "## Revisión Codex" (o una de sus variantes aceptadas, ver
#      TITULOS_REVISION_CODEX más abajo — #688 tituló la suya "## Revisión de
#      Codex" y el check dio rojo con la revisión ya hecha) con contenido
#      real: al menos una línea de hallazgos aceptados o rechazados, o "sin
#      hallazgos" — y sin marcadores provisionales ("resultado abajo",
#      "pendiente", "TODO").
#
# Qué NO hace: no interpreta si los hallazgos de Codex son correctos, ni si
# el paso operativo descrito es el correcto — eso lo decide una persona. Solo
# comprueba que la sección exista, no esté vacía y no lleve un marcador de
# "esto se rellena luego".
#
# Uso:
#   scripts/verificar-gobernanza-pr.sh <fichero-cuerpo> <fichero-ficheros> [autor]
#
# <fichero-cuerpo>: el cuerpo de la PR tal cual (UTF-8, un fichero).
# <fichero-ficheros>: un path por línea, los ficheros que toca la PR. Debe
#   incluir tanto `.filename` como `.previous_filename` de cada entrada
#   (`gh api .../pulls/N/files --paginate --jq '.[] | .filename,
#   (.previous_filename // empty)'`) — si no, un rename que saque el fichero
#   vigilado de su ruta escapa de la regla 1 sin que este guion pueda verlo:
#   solo mira lo que le llega, y la ruta anterior no está en `.filename`.
# [autor]: opcional, el login de quien abrió la PR (`.user.login`).
#   "dependabot[bot]" exime de la regla 2 (§24 no le aplica: no hay diseño
#   que refutar en un bump de versión). Cualquier otro valor, incluido
#   vacío, exige la sección — el valor por defecto es EXIGIR, no eximir.
#
# Ver scripts/verificar-gobernanza-pr.tests.sh para la prueba sin red.
set -euo pipefail

FICHERO_ROLES_CLUSTER="deploy/bootstrap/roles-de-cluster.sql"
AUTOR_EXENTO_REVISION_CODEX="dependabot[bot]"

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
AUTOR="${3:-}"

if [[ -z "$CUERPO" || ! -f "$CUERPO" || -z "$FICHEROS" || ! -f "$FICHEROS" ]]; then
  echo "Uso: $0 <fichero-cuerpo> <fichero-ficheros-cambiados> [autor]" >&2
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

# Regla 2 acepta un conjunto explícito y pequeño de variantes del título —no
# un grep laxo—, probado en verificar-gobernanza-pr.tests.sh. Nace de un caso
# real: PR #688 (2026-09-18) tituló su sección "## Revisión de Codex" (con
# "de", redacción tan natural como la canónica) y el check dio rojo con la
# revisión ya hecha de verdad. Cada variante se compara con la misma igualdad
# exacta de extraer_seccion (sin regex, sin escapar tildes) — tolerar la
# REDACCIÓN del título no afloja la propiedad: seccion_no_vacia() y los
# marcadores provisionales se siguen exigiendo igual sobre el contenido que
# aparezca bajo cualquiera de las variantes.
TITULOS_REVISION_CODEX=(
  "## Revisión Codex"
  "## Revisión de Codex"
  "## Revision Codex"
  "## Revision de Codex"
)

# Prueba cada título de la lista, en orden, y devuelve el contenido de la
# primera variante que aparezca con contenido no vacío. Si ninguna tiene
# contenido (ausente o vacía en todas), devuelve el resultado de la primera
# variante de la lista — igual que extraer_seccion() cuando el título no
# aparece — para que el mensaje de "falta/vacía" de más abajo tenga algo
# consistente que reportar.
extraer_seccion_variantes() {
  local fichero="$1"
  shift
  local titulos=("$@")
  local titulo resultado
  for titulo in "${titulos[@]}"; do
    resultado="$(extraer_seccion "$fichero" "$titulo")"
    if [[ -n "$resultado" ]]; then
      printf '%s' "$resultado"
      return 0
    fi
  done
  extraer_seccion "$fichero" "${titulos[0]}"
}

seccion_no_vacia() {
  # Quita comentarios HTML de una sola línea antes de mirar si queda
  # contenido visible: "<!-- -->" no es texto real, y GitHub lo renderiza
  # como nada — sin este filtro, una sección "rellena" solo con un
  # comentario pasaba como si tuviera contenido (hallazgo de Codex sobre
  # esta misma PR). No cubre comentarios HTML repartidos en varias líneas:
  # ese caso queda como hueco aceptado, no vale la complejidad para un check
  # que no es `required`.
  printf '%s' "$1" | sed -E 's/<!--.*-->//g' | grep -qE '[^[:space:]]'
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

# --- Regla 2: revisión Codex en el cuerpo ---
if [[ "$AUTOR" == "$AUTOR_EXENTO_REVISION_CODEX" ]]; then
  echo "OK        Autor $AUTOR_EXENTO_REVISION_CODEX — regla de revisión Codex no aplica."
else
  SECCION="$(extraer_seccion_variantes "$CUERPO" "${TITULOS_REVISION_CODEX[@]}")"
  if ! seccion_no_vacia "$SECCION"; then
    echo "PROBLEMA  Falta la sección '## Revisión Codex' en el cuerpo (o está vacía)." \
         "Protocolo § 24.2 / skill protocolo-hydra-multimodelo: al menos una línea de" \
         "hallazgos aceptados o rechazados, o 'sin hallazgos' si no tocaba superficie" \
         "sensible."
    PROBLEMAS=$((PROBLEMAS + 1))
  elif printf '%s' "$SECCION" | grep -qiE 'resultados?[[:space:]-]+abajo'; then
    echo "PROBLEMA  La sección 'Revisión Codex' tiene el marcador provisional 'resultado abajo'" \
         "(el mismo que #673 dejó sin rellenar)."
    PROBLEMAS=$((PROBLEMAS + 1))
  # \b solo exige límite de palabra al PRINCIPIO de "pendiente": sin el \b de
  # cierre, esto casaba con "pendientes" dentro de una frase real (PR #828,
  # runs 35831818412 y 35832823379: "sin hallazgos nuevos pendientes de esta
  # pasada" dio rojo sin ser un marcador de relleno). El \b de cierre limita
  # la detección a la palabra exacta "pendiente".
  elif printf '%s' "$SECCION" | grep -qiE '\bpendiente\b'; then
    echo "PROBLEMA  La sección 'Revisión Codex' tiene el marcador provisional 'pendiente'."
    PROBLEMAS=$((PROBLEMAS + 1))
  elif printf '%s' "$SECCION" | grep -qE '\bTODOs?\b'; then
    echo "PROBLEMA  La sección 'Revisión Codex' tiene el marcador provisional 'TODO'."
    PROBLEMAS=$((PROBLEMAS + 1))
  else
    echo "OK        Sección 'Revisión Codex' presente, con contenido y sin marcadores provisionales."
  fi
fi

echo
echo "Problemas: $PROBLEMAS"

if (( PROBLEMAS > 0 )); then
  exit 1
fi
