#!/bin/bash
# Alcance del CI: decide si los jobs PESADOS de .github/workflows/ci.yml
# (integración, E2E y k6) tienen que correr en este run o si ya corrieron, o
# van a correr, en el grupo de fusión.
#
# POR QUÉ EXISTE. Medido sobre tres días (2026-10-06 a 2026-10-09, 338 runs):
# cada PR fusionada ejecutaba el CI entero TRES veces —en la PR, en el grupo de
# la cola de fusión y en el `push` a main— y las dos últimas sobre el mismo
# commit (77 de 78 SHA de `push` coincidían con el de un run `merge_group`
# verde: la cola avanza main por fast-forward al commit del grupo). 314 minutos
# de runner por PR, y 55 de 78 CI de `push` cancelados por la fusión siguiente
# antes de poder desplegar a staging.
#
# REGLA. La puerta es el grupo de fusión: ahí corre TODO, siempre, y este
# guion ni se consulta (los jobs pesados lo deciden por `github.event_name` en
# su propio `if:`). Fuera de él:
#
#   push          Se saltan los pesados SOLO si la API confirma un run de
#                 ESTE workflow, en ESTE repositorio, de evento `merge_group`,
#                 terminado en `success` y con el mismo SHA que se acaba de
#                 empujar. Un push directo o un revert manual no lo tienen:
#                 corre todo.
#   pull_request  Integración y E2E corren si el diff toca las rutas de abajo
#                 o si la PR lleva la etiqueta `CI: completo`. k6 solo con la
#                 etiqueta o si cambia la propia infraestructura de CI.
#   otro evento   Todo.
#
# FALLO CERRADO = CORRER. Cada salida es `true` salvo que este guion pueda
# demostrar que no hace falta. Un error de `gh`, una respuesta vacía, una lista
# de ficheros que no se pudo leer o un valor inesperado dejan las tres salidas
# en `true`. Lo contrario —saltar por no haber podido preguntar— convertiría un
# fallo de red en un check obligatorio omitido, que GitHub da por aprobado.
#
# RIESGO DECLARADO. Una PR de pantalla que no toque tests/CaeManager.E2ETests/
# ve sus E2E por primera vez en la cola. Es deliberado (src/CaeManager.Web/ no
# activa E2E: anularía el ahorro en las PR de pantallas); quien quiera verlos
# antes pone la etiqueta.
#
# Entradas (entorno): EVENTO, GITHUB_REPOSITORY, GITHUB_SHA, PR_NUMERO (solo en
# pull_request), GH_TOKEN (lo usa `gh`). Salidas: `integracion`, `e2e` y
# `carga`, `true` o `false`, en $GITHUB_OUTPUT y en la salida estándar.
# Sin `jq`: todo el filtrado usa el `--jq` integrado de `gh`.
set -uo pipefail

ETIQUETA_COMPLETO="CI: completo"
WORKFLOW_FICHERO="ci.yml"
WORKFLOW_RUTA=".github/workflows/ci.yml"
# La API de ficheros de una PR devuelve como mucho 3000: a partir de ahí la
# lista llega truncada y una ruta sensible podría quedar fuera.
MAX_FICHEROS_API=3000
INTENTOS_PUSH=3
# Solo los tests lo ponen a 0.
ESPERA_PUSH_S="${CI_ALCANCE_ESPERA_PUSH_S:-15}"

EVENTO="${EVENTO:-}"
REPO="${GITHUB_REPOSITORY:-}"
SHA="${GITHUB_SHA:-}"
PR_NUMERO="${PR_NUMERO:-}"

integracion=true
e2e=true
carga=true
motivo=""

emitir() {
  echo "Alcance del CI — evento: ${EVENTO:-<vacío>} — $motivo"
  echo "integracion=$integracion"
  echo "e2e=$e2e"
  echo "carga=$carga"
  if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
    {
      echo "integracion=$integracion"
      echo "e2e=$e2e"
      echo "carga=$carga"
    } >> "$GITHUB_OUTPUT"
  fi
  if [[ -n "${GITHUB_STEP_SUMMARY:-}" ]]; then
    {
      echo "### Alcance del CI"
      echo ""
      echo "Evento \`${EVENTO:-?}\`: integración=\`$integracion\`, E2E=\`$e2e\`, carga=\`$carga\`."
      echo ""
      echo "$motivo"
    } >> "$GITHUB_STEP_SUMMARY"
  fi
  exit 0
}

todo() {
  integracion=true; e2e=true; carga=true
  motivo="$1"
  emitir
}

# --- push: ¿este SHA ya lo probó la cola de fusión? --------------------------
alcance_push() {
  if [[ -z "$REPO" || -z "$SHA" ]]; then
    todo "sin repositorio o sin SHA: corre todo."
  fi
  local runs intento ultimo_error=""
  local sha_run evento concl ruta repo repo_cabeza
  # La cola fusiona en cuanto el run del grupo termina, y el `push` nace
  # segundos después (medido: 13 s y 34 s de margen). Si la API todavía no
  # lista ese run como terminado, se reintenta un par de veces antes de
  # rendirse: rendirse es «corre todo», que es seguro pero tira el ahorro.
  for (( intento = 1; intento <= INTENTOS_PUSH; intento++ )); do
    # El filtro de la consulta acota; la decisión NO se fía de él: cada campo
    # se vuelve a comprobar abajo, línea a línea. Separador `|` y no tabulador:
    # con IFS de espacio en blanco `read` colapsa los campos vacíos y los
    # desplaza; con `|` un campo vacío sigue siendo un campo.
    if runs="$(gh api "repos/$REPO/actions/workflows/$WORKFLOW_FICHERO/runs?head_sha=$SHA&event=merge_group&status=success&per_page=20" \
        --jq '.workflow_runs[] | [.head_sha, .event, .conclusion, .path, .repository.full_name, .head_repository.full_name] | map(. // "") | join("|")')"; then
      ultimo_error=""
      while IFS='|' read -r sha_run evento concl ruta repo repo_cabeza; do
        [[ -z "$sha_run" ]] && continue
        if [[ "$sha_run" == "$SHA" && "$evento" == "merge_group" && "$concl" == "success" \
              && "$ruta" == "$WORKFLOW_RUTA" && "$repo" == "$REPO" && "$repo_cabeza" == "$REPO" ]]; then
          integracion=false; e2e=false; carga=false
          motivo="el commit $SHA ya pasó el CI completo en el grupo de fusión: los jobs pesados no se repiten."
          emitir
        fi
      done <<< "$runs"
    else
      ultimo_error=1
    fi
    if (( intento < INTENTOS_PUSH )); then sleep "$ESPERA_PUSH_S"; fi
  done
  if [[ -n "$ultimo_error" ]]; then
    todo "no se pudo consultar los runs del grupo de fusión: corre todo."
  fi
  todo "ningún run del grupo de fusión en verde para $SHA (push directo, revert o commit ajeno a la cola): corre todo."
}

# --- pull_request: rutas del diff y etiqueta ---------------------------------
alcance_pr() {
  if [[ -z "$REPO" || ! "$PR_NUMERO" =~ ^[0-9]+$ ]]; then
    todo "sin repositorio o sin número de PR: corre todo."
  fi
  local etiquetas ficheros
  # Las etiquetas se leen en vivo, no del payload del evento: así basta poner
  # la etiqueta y relanzar el run, sin añadir `labeled` a los disparadores del
  # workflow (despertaría todo el CI con cada etiqueta de gobernanza).
  if ! etiquetas="$(gh api "repos/$REPO/issues/$PR_NUMERO/labels?per_page=100" --jq '.[].name')"; then
    todo "no se pudieron leer las etiquetas de la PR: corre todo."
  fi
  if grep -qxF "$ETIQUETA_COMPLETO" <<< "$etiquetas"; then
    todo "la PR lleva la etiqueta «$ETIQUETA_COMPLETO»: corre todo."
  fi
  if ! ficheros="$(gh api --paginate "repos/$REPO/pulls/$PR_NUMERO/files?per_page=100" \
      --jq '.[] | .filename, (.previous_filename // empty)')"; then
    todo "no se pudo leer la lista de ficheros de la PR: corre todo."
  fi
  if [[ -z "${ficheros//[$'\n\r\t ']/}" ]]; then
    todo "la lista de ficheros de la PR llegó vacía: corre todo."
  fi

  if (( $(grep -c . <<< "$ficheros") >= MAX_FICHEROS_API )); then
    todo "la PR toca $MAX_FICHEROS_API ficheros o más y la API trunca la lista: corre todo."
  fi

  # Infraestructura del propio CI y del despliegue, y lo que cambia la
  # compilación o las dependencias de TODOS los proyectos (un salto de versión
  # de Npgsql o de EF en un .csproj no toca ninguna ruta «de integración» y
  # rompe la integración igual): activa los tres.
  local re_infra='^(\.github/workflows/|deploy/|\.config/)|(^|/)(Dockerfile[^/]*|docker-[^/]*|Directory\.[^/]*|global\.json|packages\.lock\.json|[^/]*\.(csproj|props|targets|slnx?))$'
  # Integración: lo que toca base de datos, RLS, migraciones, guiones y
  # autorización. Los directorios de tests entran porque los 6 rojos propios
  # de integración medidos en PR tocaban tests/CaeManager.IntegrationTests/.
  local re_integracion='^(tests/CaeManager\.IntegrationTests/|src/CaeManager\.Infrastructure/|src/CaeManager\.Migrations\.PostgreSQL/|scripts/)'
  local re_nombre='autoriz|authoriz|alcance|permis|rls|tenant'
  # E2E: los 4 rojos propios de E2E medidos en PR tocaban este directorio.
  local re_e2e='^tests/CaeManager\.E2ETests/'

  integracion=false; e2e=false; carga=false
  local porque_integracion="" porque_e2e="" f
  while IFS= read -r f; do
    f="${f%$'\r'}"
    [[ -z "$f" ]] && continue
    if [[ "$f" =~ $re_infra ]]; then
      integracion=true; e2e=true; carga=true
      porque_integracion="${porque_integracion:-$f}"; porque_e2e="${porque_e2e:-$f}"
      continue
    fi
    if [[ "$f" =~ $re_integracion ]] || [[ "${f,,}" =~ $re_nombre ]]; then
      integracion=true; porque_integracion="${porque_integracion:-$f}"
    fi
    if [[ "$f" =~ $re_e2e ]]; then
      e2e=true; porque_e2e="${porque_e2e:-$f}"
    fi
  done <<< "$ficheros"

  local txt_i="se deja para el grupo de fusión" txt_e="se deja para el grupo de fusión"
  [[ -n "$porque_integracion" ]] && txt_i="corre por «$porque_integracion»"
  [[ -n "$porque_e2e" ]] && txt_e="corre por «$porque_e2e»"
  motivo="PR #$PR_NUMERO — integración: $txt_i; E2E: $txt_e. Etiqueta «$ETIQUETA_COMPLETO» para forzarlo todo."
  emitir
}

case "$EVENTO" in
  merge_group)  todo "grupo de fusión: corre todo, siempre." ;;
  push)         alcance_push ;;
  pull_request) alcance_pr ;;
  *)            todo "evento no previsto: corre todo." ;;
esac
