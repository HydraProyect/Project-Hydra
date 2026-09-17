#!/bin/bash
# Espera CI/cola de merge/despliegue de una PR y devuelve UN veredicto parseable.
#
# Por qué existe (Turno nocturno 2026-09-18, T4): sondear "¿ya terminó CI?" desde
# el propio modelo, mensaje a mensaje, generó cientos de "Esperando..." en una
# sesión anterior y no distinguía los fallos de instrumento medidos en
# PROTOCOLO-TURNO-NOCTURNO.md § 4.1. Este guion se lanza UNA vez, en segundo
# plano, y hace él solo todo el sondeo — el modelo no debe invocarlo en bucle.
#
# Uso:
#   scripts/esperar-ci.sh <numero-PR> [--hasta checks|merge|despliegue]
#                         [--timeout-min N] [--intervalo-s N] [--repo OWNER/REPO]
#
#   --hasta checks     Espera solo a que los checks OBLIGATORIOS del HEAD
#                       vigente de la PR terminen (verde o rojo).
#   --hasta merge       Además, espera a que la PR entre en la cola de fusión
#                       de GitHub (NO auto-merge: son mecanismos distintos, ver
#                       más abajo) y a que la cola la fusione de verdad.
#   --hasta despliegue  (por defecto) Además, espera al run del workflow
#                       "Desplegar" sobre el commit de fusión, hasta que el job
#                       "Desplegar a staging" concluya (nunca espera la
#                       aprobación humana de producción: sería un bloqueo sin
#                       límite razonable, y no lo exige el objetivo de este
#                       guion — declarado como hueco, ver el test).
#
#   --timeout-min N     Por defecto 240 (4 h). Cubre cola de merge congestionada
#                       (ver hydra-merge-queue-congestion-github-side.md, ~1h20
#                       medida) más CI completo más despliegue.
#   --intervalo-s N     Por defecto 45. No bajar de esto: cada vuelta hace hasta
#                       4 llamadas a la API de GitHub.
#   --repo OWNER/REPO   Por defecto, el repositorio de `gh` en el directorio
#                       actual (`gh repo view`).
#
# Salida: líneas de progreso en fecha ISO a stderr; UNA línea final a stdout
# con el contrato `VEREDICTO: <ESTADO> <detalle...>` y el código de salida
# correspondiente:
#
#   VERDE               0   Lo pedido con --hasta terminó bien.
#   ROJO                1   Un check, la cola o un job de despliegue fallaron.
#   EXPULSADA_DE_COLA   2   La cola de fusión expulsó la PR (no un fallo de check).
#   TIMEOUT             3   Se agotó --timeout-min sin ver un desenlace. La
#                           línea lleva el ÚLTIMO ESTADO OBSERVADO, nunca un
#                           "terminó" disfrazado (§ 3 protocolo-hydra-verificacion:
#                           "un vigía que agota su tiempo sin observar el
#                           suceso sale con código 0" es exactamente lo que
#                           este código de salida evita).
#   OBSOLETO            4   El run de "Desplegar" se descartó porque su commit
#                           ya no era la punta de `main` (main avanzó mientras
#                           tanto: CI reejecutado, o dos merges seguidos).
#
# Un error de USO o de ENTORNO (PR inexistente, --hasta inválido, `gh` sin
# autenticar) sale con código 64 y NUNCA imprime una línea `VEREDICTO:` — un
# consumidor que solo mire el prefijo `VEREDICTO:` no debe poder confundir
# "no pude ni empezar a mirar" con uno de los cinco desenlaces de arriba.
#
# --- Las tres trampas de instrumento que este guion evita a propósito -------
#
# 1. "Condición que termina antes" (§ 4.1): el HEAD de la PR puede cambiar a
#    mitad de espera (push nuevo). Cada vuelta relee `headRefOid`; si cambió,
#    el progreso de checks acumulado se descarta y se avisa — los checks
#    vistos en verde eran de un commit que ya no es el HEAD.
#
# 2. "Campo parecido, mecanismo distinto" (§ 4.1): `autoMergeRequest` (REST/
#    `gh pr view --json`) NO es pertenencia a la cola de fusión — son
#    mecanismos distintos y ese campo puede estar vacío con la PR igualmente
#    en cola. La pertenencia real se mide por GraphQL
#    `pullRequest.mergeQueueEntry`, que `gh pr view --json` ni siquiera expone
#    (confirmado: no está en la lista de campos de `gh pr view --json` en esta
#    versión de gh). Y dentro de esa propia entrada hay UNA MÁS: su
#    `headCommit` es el commit SINTÉTICO que la cola construye sobre
#    `gh-readonly-queue/main/...`, distinto del `headRefOid` de la PR — medido
#    en vivo (PR #681, 2026-09-17): los checks de la PR ya estaban en verde
#    mientras `mergeQueueEntry.headCommit.statusCheckRollup.state` seguía en
#    `PENDING`. Los checks "obligatorios" de `gh pr checks --required` solo
#    contestan la fase 1 (--hasta checks); la fase 2 vigila el rollup del
#    commit de la cola, no el de la PR.
#
# 3. "Datos ciertos del árbol equivocado" (§ 4.1): un check con `bucket=cancel`
#    en el HEAD vigente de la PR es un run cancelado que no se relanza solo
#    (p. ej. por `concurrency` al llegar un push nuevo) — se trata como ROJO,
#    nunca como "sigue corriendo". Y el run de "Desplegar" se busca por
#    `headSha` exacto del commit de fusión, nunca por "el último run del
#    workflow": un `head_sha` que ya no es la punta de `main` genera un run
#    real, con checks reales, que el propio `deploy.yml` descarta por
#    obsoleto — ver la constante MENSAJE_RUN_OBSOLETO abajo, tomada literal de
#    `deploy.yml`.
set -uo pipefail

PROG="$(basename "$0")"

log() {
  printf '[%s] %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$*" >&2
}

uso() {
  cat >&2 <<EOF
Uso: $PROG <numero-PR> [--hasta checks|merge|despliegue] [--timeout-min N]
                       [--intervalo-s N] [--repo OWNER/REPO]
EOF
  exit 64
}

# --- Argumentos ---------------------------------------------------------
PR=""
HASTA="despliegue"
TIMEOUT_MIN=240
INTERVALO_S=45
REPO=""

if [[ $# -lt 1 ]]; then
  uso
fi
PR="$1"; shift
if ! [[ "$PR" =~ ^[0-9]+$ ]]; then
  echo "$PROG: '<numero-PR>' debe ser numérico, recibido '$PR'" >&2
  uso
fi

while [[ $# -gt 0 ]]; do
  case "$1" in
    --hasta)
      HASTA="${2:-}"; shift 2 || uso
      ;;
    --timeout-min)
      TIMEOUT_MIN="${2:-}"; shift 2 || uso
      ;;
    --intervalo-s)
      INTERVALO_S="${2:-}"; shift 2 || uso
      ;;
    --repo)
      REPO="${2:-}"; shift 2 || uso
      ;;
    *)
      echo "$PROG: opción desconocida '$1'" >&2
      uso
      ;;
  esac
done

case "$HASTA" in
  checks|merge|despliegue) ;;
  *)
    echo "$PROG: --hasta debe ser checks, merge o despliegue (recibido '$HASTA')" >&2
    uso
    ;;
esac
if ! [[ "$TIMEOUT_MIN" =~ ^[0-9]+$ ]] || [[ "$TIMEOUT_MIN" -le 0 ]]; then
  echo "$PROG: --timeout-min debe ser un entero positivo" >&2
  uso
fi
if ! [[ "$INTERVALO_S" =~ ^[0-9]+$ ]] || [[ "$INTERVALO_S" -le 0 ]]; then
  echo "$PROG: --intervalo-s debe ser un entero positivo" >&2
  uso
fi

if [[ -z "$REPO" ]]; then
  REPO="$(gh repo view --json nameWithOwner --jq '.nameWithOwner' 2>/dev/null || true)"
  if [[ -z "$REPO" ]]; then
    echo "$PROG: no se pudo determinar el repositorio; usa --repo OWNER/REPO" >&2
    exit 64
  fi
fi

OWNER="${REPO%%/*}"
NOMBRE_REPO="${REPO##*/}"

TIMEOUT_S=$(( TIMEOUT_MIN * 60 ))
# Solo para el test de este guion: --timeout-min tiene granularidad de minuto,
# demasiado grosero para probar un TIMEOUT real sin esperar 60 s de verdad.
# Nunca se documenta como flag ni se usa en producción.
if [[ -n "${ESPERAR_CI_TEST_TIMEOUT_S:-}" ]]; then
  TIMEOUT_S="$ESPERAR_CI_TEST_TIMEOUT_S"
fi
INICIO_EPOCH=$(date +%s)

# Tomado literal de deploy.yml (paso "Comprobar que el SHA sigue siendo la
# punta de main"). Si ese mensaje cambia allí, este guion deja de detectar
# OBSOLETO y hay que actualizarlo — el test cubre la mutación.
MENSAJE_RUN_OBSOLETO="run obsoleto para despliegue"

tiempo_agotado() {
  local ahora
  ahora=$(date +%s)
  [[ $(( ahora - INICIO_EPOCH )) -ge $TIMEOUT_S ]]
}

# --- Envolturas sobre `gh`, un endpoint por función ----------------------
# Cada una imprime como mucho una línea (o varias en *_tsv de varias filas) ya
# reducida con --jq: el mock de los tests sustituye `gh` completo y devuelve
# exactamente esta forma de salida, así que el guion real y el mock comparten
# el mismo contrato observable.

pr_view_tsv() {
  gh pr view "$PR" --repo "$REPO" \
    --json state,headRefOid,mergeStateStatus,mergeCommit,mergedAt \
    --jq '[.state, .headRefOid, .mergeStateStatus, (.mergeCommit.oid // ""), (.mergedAt // "")] | @tsv' \
    2>/dev/null
}

checks_requeridos_esperados() {
  gh api "repos/$REPO/branches/main/protection" \
    --jq '.required_status_checks.contexts[]' 2>/dev/null
}

pr_checks_requeridos_tsv() {
  gh pr checks "$PR" --repo "$REPO" --required \
    --json name,bucket --jq '.[] | [.name, .bucket] | @tsv' \
    2>/dev/null
}

merge_queue_entry_tsv() {
  gh api graphql -f query='
    query($owner:String!, $repo:String!, $number:Int!) {
      repository(owner:$owner, name:$repo) {
        pullRequest(number:$number) {
          mergeQueueEntry {
            state
            headCommit { oid statusCheckRollup { state } }
          }
        }
      }
    }' -f owner="$OWNER" -f repo="$NOMBRE_REPO" -F number="$PR" \
    --jq '.data.repository.pullRequest.mergeQueueEntry
          | if . == null then "NULL"
            else [.state, .headCommit.oid, (.headCommit.statusCheckRollup.state // "")] | @tsv
            end' \
    2>/dev/null
}

ultima_expulsion_cola_tsv() {
  gh api graphql -f query='
    query($owner:String!, $repo:String!, $number:Int!) {
      repository(owner:$owner, name:$repo) {
        pullRequest(number:$number) {
          timelineItems(last: 20, itemTypes: [REMOVED_FROM_MERGE_QUEUE_EVENT]) {
            nodes { ... on RemovedFromMergeQueueEvent { createdAt reason } }
          }
        }
      }
    }' -f owner="$OWNER" -f repo="$NOMBRE_REPO" -F number="$PR" \
    --jq '.data.repository.pullRequest.timelineItems.nodes
          | if length == 0 then "NINGUNO"
            else (.[-1] | [.createdAt, (.reason // "")] | @tsv)
            end' \
    2>/dev/null
}

run_desplegar_para_sha_tsv() {
  local sha="$1"
  gh run list --repo "$REPO" --workflow=deploy.yml \
    --json databaseId,headSha,status,conclusion,createdAt -L 30 \
    --jq "[.[] | select(.headSha==\"$sha\")] | sort_by(.createdAt) | reverse
          | .[0] | if . == null then \"NINGUNO\"
            else [(.databaseId|tostring), .status, (.conclusion // \"\"), .createdAt] | @tsv
            end" \
    2>/dev/null
}

run_jobs_tsv() {
  local run_id="$1"
  gh run view "$run_id" --repo "$REPO" --json jobs \
    --jq '.jobs[] | [.name, .status, (.conclusion // "")] | @tsv' \
    2>/dev/null
}

run_es_obsoleto() {
  local run_id="$1"
  gh run view "$run_id" --repo "$REPO" --log-failed 2>/dev/null \
    | grep -qi -- "$MENSAJE_RUN_OBSOLETO"
}

# --- Veredicto final: UNA línea a stdout, log de progreso a stderr -------
veredicto() {
  local estado="$1"; shift
  echo "VEREDICTO: $estado $*"
}

salir_verde()      { veredicto VERDE "$*"; exit 0; }
salir_rojo()        { veredicto ROJO "$*"; exit 1; }
salir_expulsada()   { veredicto EXPULSADA_DE_COLA "$*"; exit 2; }
salir_timeout()     { veredicto TIMEOUT "fase=$1 ultimo_estado=\"${2:-}\""; exit 3; }
salir_obsoleto()    { veredicto OBSOLETO "$*"; exit 4; }

log "PR #$PR ($REPO) · esperando hasta '$HASTA' · timeout ${TIMEOUT_MIN} min · intervalo ${INTERVALO_S}s"

# =========================================================================
# FASE 0: leer el estado inicial de la PR — usada por las tres fases.
# =========================================================================
leer_pr() {
  local linea
  linea="$(pr_view_tsv)"
  if [[ -z "$linea" ]]; then
    echo "$PROG: no se pudo leer la PR #$PR en $REPO (¿existe? ¿hay sesión de gh?)" >&2
    exit 64
  fi
  IFS=$'\t' read -r PR_ESTADO PR_HEAD PR_MERGE_STATE PR_MERGE_SHA PR_MERGED_AT <<<"$linea"
}

leer_pr

if [[ "$PR_ESTADO" == "CLOSED" ]]; then
  salir_rojo "PR #$PR cerrada sin fusionar"
fi

# =========================================================================
# FASE 1: checks obligatorios del HEAD vigente.
# =========================================================================
fase_checks() {
  local head_vigilado="$PR_HEAD"
  local fallos_branch_protection=0
  log "fase checks: vigilando HEAD $head_vigilado"

  while true; do
    leer_pr
    if [[ "$PR_ESTADO" == "CLOSED" ]]; then
      salir_rojo "PR #$PR cerrada sin fusionar"
    fi
    if [[ "$PR_ESTADO" == "MERGED" ]]; then
      log "fase checks: la PR ya está fusionada (los checks obligatorios pasaron por definición)"
      return 0
    fi
    if [[ "$PR_HEAD" != "$head_vigilado" ]]; then
      log "fase checks: el HEAD cambió de $head_vigilado a $PR_HEAD — descarto el progreso anterior, ese commit ya no es el HEAD"
      head_vigilado="$PR_HEAD"
    fi

    # Distinguir "gh falló al leer branch protection" (error de entorno: 403,
    # rate limit, permisos) de "todavía no hay checks obligatorios que ver"
    # (Codex, P2, 2026-09-17): con el `2>/dev/null` de la función, ambos
    # daban antes una lista vacía indistinguible, y el guion agotaba el
    # timeout entero sin decir que el problema real era leer la API, no
    # esperar a CI.
    local esperados_texto
    if ! esperados_texto="$(checks_requeridos_esperados)"; then
      fallos_branch_protection=$((fallos_branch_protection + 1))
      log "fase checks: ERROR leyendo la protección de main (branch protection) — intento $fallos_branch_protection de 3"
      if [[ "$fallos_branch_protection" -ge 3 ]]; then
        echo "$PROG: no se pudo leer la protección de main (branch protection) tras $fallos_branch_protection intentos — es un fallo de entorno/permisos, no un TIMEOUT de CI" >&2
        exit 64
      fi
      sleep "$INTERVALO_S"
      continue
    fi
    fallos_branch_protection=0
    mapfile -t esperados <<<"$esperados_texto"
    # Filtrar la línea vacía que deja `mapfile` cuando $esperados_texto es "".
    local esperados_filtrados=() nombre_esperado
    for nombre_esperado in "${esperados[@]}"; do
      [[ -n "$nombre_esperado" ]] && esperados_filtrados+=("$nombre_esperado")
    done
    esperados=("${esperados_filtrados[@]}")

    declare -A vistos=()
    local rojo=""
    while IFS=$'\t' read -r nombre bucket; do
      [[ -z "$nombre" ]] && continue
      vistos["$nombre"]="$bucket"
      if [[ "$bucket" == "fail" || "$bucket" == "cancel" ]]; then
        rojo="check obligatorio '$nombre' del HEAD $head_vigilado: $bucket"
      fi
    done < <(pr_checks_requeridos_tsv)

    if [[ -n "$rojo" ]]; then
      salir_rojo "$rojo"
    fi

    local faltan=() pendientes=()
    for nombre in "${esperados[@]}"; do
      if [[ -z "${vistos[$nombre]+x}" ]]; then
        faltan+=("$nombre")
      elif [[ "${vistos[$nombre]}" != "pass" ]]; then
        pendientes+=("$nombre=${vistos[$nombre]}")
      fi
    done

    if [[ ${#esperados[@]} -gt 0 && ${#faltan[@]} -eq 0 && ${#pendientes[@]} -eq 0 ]]; then
      log "fase checks: los ${#esperados[@]} checks obligatorios están en verde sobre $head_vigilado"
      return 0
    fi

    local detalle=""
    [[ ${#faltan[@]} -gt 0 ]] && detalle+="ausentes=[${faltan[*]}] "
    [[ ${#pendientes[@]} -gt 0 ]] && detalle+="pendientes=[${pendientes[*]}]"
    log "fase checks: aún no completos — $detalle"

    if tiempo_agotado; then
      salir_timeout checks "head=$head_vigilado $detalle"
    fi
    sleep "$INTERVALO_S"
  done
}

fase_checks

if [[ "$HASTA" == "checks" ]]; then
  salir_verde "PR #$PR: checks obligatorios en verde sobre $PR_HEAD"
fi

# =========================================================================
# FASE 2: cola de fusión real (nunca auto-merge) hasta MERGED.
# =========================================================================
SHA_FUSION=""

fase_merge() {
  leer_pr
  if [[ "$PR_ESTADO" == "MERGED" ]]; then
    SHA_FUSION="$PR_MERGE_SHA"
    log "fase merge: la PR ya estaba fusionada en $SHA_FUSION"
    return 0
  fi

  local inicio_fase
  inicio_fase="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  log "fase merge: esperando entrada en la cola de fusión (no auto-merge) y fusión real"

  while true; do
    leer_pr
    if [[ "$PR_ESTADO" == "CLOSED" ]]; then
      salir_rojo "PR #$PR cerrada sin fusionar (mientras se esperaba la cola)"
    fi
    if [[ "$PR_ESTADO" == "MERGED" ]]; then
      SHA_FUSION="$PR_MERGE_SHA"
      log "fase merge: fusionada en $SHA_FUSION"
      return 0
    fi

    local linea_cola
    linea_cola="$(merge_queue_entry_tsv)"
    if [[ -z "$linea_cola" ]]; then
      log "fase merge: no se pudo consultar la cola de fusión (GraphQL); reintentando"
    elif [[ "$linea_cola" == "NULL" ]]; then
      local linea_expulsion
      linea_expulsion="$(ultima_expulsion_cola_tsv)"
      if [[ -n "$linea_expulsion" && "$linea_expulsion" != "NINGUNO" ]]; then
        local exp_fecha exp_motivo
        IFS=$'\t' read -r exp_fecha exp_motivo <<<"$linea_expulsion"
        if [[ "$exp_fecha" > "$inicio_fase" || "$exp_fecha" == "$inicio_fase" ]]; then
          salir_expulsada "motivo=\"$exp_motivo\" en $exp_fecha"
        fi
      fi
      log "fase merge: la PR no está en la cola de fusión ahora mismo (ni auto-merge armado la mete sola: son mecanismos distintos)"
    else
      local cola_estado cola_sha cola_rollup
      IFS=$'\t' read -r cola_estado cola_sha cola_rollup <<<"$linea_cola"
      log "fase merge: en cola, estado=$cola_estado commit_sintetico=$cola_sha rollup=$cola_rollup"
      if [[ "$cola_estado" == "UNMERGEABLE" ]]; then
        log "fase merge: UNMERGEABLE puede ser congestión transitoria del lado de GitHub (ver hydra-merge-queue-congestion-github-side.md) — sigo esperando, no lo trato como rojo inmediato"
      fi
    fi

    if tiempo_agotado; then
      salir_timeout merge "mergeStateStatus=$PR_MERGE_STATE cola=${linea_cola:-desconocida}"
    fi
    sleep "$INTERVALO_S"
  done
}

fase_merge

if [[ "$HASTA" == "merge" ]]; then
  salir_verde "PR #$PR fusionada en $SHA_FUSION"
fi

# =========================================================================
# FASE 3: run de "Desplegar" sobre el SHA de fusión, hasta que concluya
# "Desplegar a staging" (nunca espera la aprobación humana de producción).
# =========================================================================
fase_despliegue() {
  log "fase despliegue: buscando el run de 'Desplegar' para $SHA_FUSION"
  local fallos_lectura_jobs=0
  while true; do
    local linea_run
    linea_run="$(run_desplegar_para_sha_tsv "$SHA_FUSION")"
    if [[ -z "$linea_run" || "$linea_run" == "NINGUNO" ]]; then
      log "fase despliegue: aún no existe un run de 'Desplegar' para $SHA_FUSION"
    else
      local run_id run_status run_conclusion run_creado
      IFS=$'\t' read -r run_id run_status run_conclusion run_creado <<<"$linea_run"
      log "fase despliegue: run $run_id (status=$run_status conclusion=${run_conclusion:-<ninguna>})"

      # Se consultan los jobs SIEMPRE, aunque el run entero siga "in_progress":
      # "Desplegar a staging" puede haber concluido ya mientras el run global
      # sigue vivo esperando la aprobación humana de producción (Codex, P1,
      # 2026-09-17) — si solo se mirara tras `status==completed`, el modo por
      # defecto esperaría esa aprobación sin límite razonable.
      local jobs_ok=1
      local jobs_tsv
      if ! jobs_tsv="$(run_jobs_tsv "$run_id")"; then
        jobs_ok=0
      fi
      mapfile -t jobs <<<"$jobs_tsv"

      local job_staging_estado="" job_staging_conclusion="" detalle_jobs=""
      local j nombre estado conclusion
      for j in "${jobs[@]}"; do
        [[ -z "$j" ]] && continue
        IFS=$'\t' read -r nombre estado conclusion <<<"$j"
        detalle_jobs+="$nombre:$estado/${conclusion:-<pendiente>}; "
        if [[ "$nombre" == "Desplegar a staging" ]]; then
          job_staging_estado="$estado"
          job_staging_conclusion="$conclusion"
        fi
      done

      if [[ "$jobs_ok" -eq 0 || ${#jobs[@]} -eq 0 ]]; then
        # No se pudieron leer los jobs (fallo transitorio de `gh run view`).
        # NUNCA se infiere el resultado de staging desde la conclusión
        # agregada del run (Codex, P1, 2026-09-17): un run puede concluir
        # "success" con staging OMITIDO si el CI de main no fue verde, así
        # que ese atajo daría un VERDE sin despliegue real. Se trata como
        # fallo de lectura transitorio: reintentar, y si persiste, es el
        # propio timeout el que lo saca a la luz con el último estado.
        fallos_lectura_jobs=$((fallos_lectura_jobs + 1))
        log "fase despliegue: no se pudieron leer los jobs del run $run_id (intento $fallos_lectura_jobs) — reintentando, sin inferir nada de la conclusión agregada"
      elif [[ -n "$job_staging_estado" && "$job_staging_estado" == "completed" ]]; then
        fallos_lectura_jobs=0
        if [[ "$job_staging_conclusion" == "failure" ]] && run_es_obsoleto "$run_id"; then
          salir_obsoleto "run=$run_id sha=$SHA_FUSION ya no era la punta de main al desplegar"
        elif [[ "$job_staging_conclusion" == "success" ]]; then
          salir_verde "PR #$PR desplegada a staging (run=$run_id, $detalle_jobs)"
        else
          salir_rojo "job 'Desplegar a staging' (run=$run_id) concluyó '$job_staging_conclusion' — $detalle_jobs"
        fi
      else
        fallos_lectura_jobs=0
        log "fase despliegue: 'Desplegar a staging' aún no concluye (job=${job_staging_estado:-<sin crear>}) — $detalle_jobs"
      fi
    fi

    if tiempo_agotado; then
      salir_timeout despliegue "sha=$SHA_FUSION ultimo_run=${linea_run:-ninguno}"
    fi
    sleep "$INTERVALO_S"
  done
}

fase_despliegue
