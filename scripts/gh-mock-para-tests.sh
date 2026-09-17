#!/bin/bash
# Mock de `gh` para scripts/esperar-ci.tests.sh — NO se instala ni se copia a
# ninguna ruta real de `gh`; el test lo antepone al PATH en un directorio
# temporal.
#
# Detecta el endpoint por los argumentos que esperar-ci.sh usa de verdad
# (confirmados en vivo el 2026-09-17, ver cabecera del test) y devuelve la
# fixture de la LLAMADA Nº N a ese endpoint, en $FIXTURE_DIR/<ENDPOINT>/N.txt
# — ya en la forma final (post --jq) que el `gh` real produciría, porque eso
# es lo único que esperar-ci.sh consume. Si se piden más llamadas de las que
# hay fixtures, repite la última (estado estable).
set -uo pipefail

: "${FIXTURE_DIR:?FIXTURE_DIR sin definir — este mock solo se usa desde el test}"
mkdir -p "$FIXTURE_DIR/.contadores"

siguiente() {
  local endpoint="$1"
  local contador_file="$FIXTURE_DIR/.contadores/$endpoint"
  local n=1
  if [[ -f "$contador_file" ]]; then
    n=$(( $(cat "$contador_file") + 1 ))
  fi
  echo "$n" > "$contador_file"

  local dir="$FIXTURE_DIR/$endpoint"
  local archivo="$dir/$n.txt"
  if [[ ! -f "$archivo" ]]; then
    archivo="$(ls "$dir"/*.txt 2>/dev/null | sort -t/ -k1 -V | tail -1)"
  fi
  if [[ -z "$archivo" || ! -f "$archivo" ]]; then
    echo "MOCK gh: no hay fixture para '$endpoint' (llamada nº $n) — args: $*" >&2
    return 1
  fi
  cat "$archivo"
}

args=("$@")
todo="$*"

case "${args[0]:-}" in
  pr)
    case "${args[1]:-}" in
      view)   siguiente "PR_VIEW";   exit 0 ;;
      checks) siguiente "PR_CHECKS"; exit 0 ;;
    esac
    ;;
  api)
    case "${args[1]:-}" in
      graphql)
        if [[ "$todo" == *mergeQueueEntry* ]]; then
          siguiente "MERGE_QUEUE"; exit 0
        elif [[ "$todo" == *timelineItems* ]]; then
          siguiente "TIMELINE"; exit 0
        fi
        echo "MOCK gh: graphql no reconocido: $todo" >&2
        exit 1
        ;;
      *branches/main/protection*)
        siguiente "BRANCH_PROTECTION"; exit 0
        ;;
    esac
    ;;
  run)
    case "${args[1]:-}" in
      list) siguiente "RUN_LIST"; exit 0 ;;
      view)
        if [[ "$todo" == *--log-failed* ]]; then
          siguiente "RUN_LOG_FAILED"; exit 0
        else
          siguiente "RUN_VIEW_JOBS"; exit 0
        fi
        ;;
    esac
    ;;
  repo)
    if [[ "${args[1]:-}" == "view" ]]; then
      echo "Owner/repo-de-prueba"
      exit 0
    fi
    ;;
esac

echo "MOCK gh: comando no reconocido: $todo" >&2
exit 1
