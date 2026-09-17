#!/bin/bash
# Prueba de esperar-ci.sh sin red: un `gh` falso en el PATH sirve respuestas
# fijadas por escenario, en la MISMA forma (ya reducida con --jq) que el `gh`
# real devuelve — confirmada llamada a llamada contra el repositorio real el
# 2026-09-17 (PR #681, `deploy.yml` runs #35219497010/#35182511383) antes de
# escribir este guion.
#
# La propiedad que importa no es "el guion corre" — es que DISTINGUE los
# cinco desenlaces (VERDE/ROJO/EXPULSADA_DE_COLA/TIMEOUT/OBSOLETO) usando el
# dato correcto y no uno parecido, cubriendo las variantes de instrumento de
# PROTOCOLO-TURNO-NOCTURNO.md § 4.1: condición que termina antes, campo
# parecido/mecanismo distinto (mergeQueueEntry vs. checks del HEAD de la PR;
# cola vs. auto-merge) y datos ciertos del árbol equivocado (check cancelado,
# run de despliegue de un SHA ya obsoleto).
#
# Corre en CI si el job deploy-script-tests (o equivalente) lo engancha; hasta
# entonces, ejecutable a mano: bash scripts/esperar-ci.tests.sh
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPT="$SCRIPT_DIR/esperar-ci.sh"
REPO_TEST="Owner/repo-de-prueba"

TMP_ROOT="$(mktemp -d)"
trap 'rm -rf "$TMP_ROOT"' EXIT

MOCK_BIN="$TMP_ROOT/bin"
mkdir -p "$MOCK_BIN"
cp "$SCRIPT_DIR/gh-mock-para-tests.sh" "$MOCK_BIN/gh"
chmod +x "$MOCK_BIN/gh"

FALLOS=0
PRUEBAS=0

# --- Utilidades de fixtures ------------------------------------------------
# fixture ENDPOINT N linea1 [linea2 ...]  -> respuesta de la llamada Nº N a
# ese endpoint (1-based). Si el guion pide más llamadas de las fixturadas, el
# mock repite la última.
fixture() {
  local endpoint="$1" n="$2"; shift 2
  mkdir -p "$FIXTURE_DIR/$endpoint"
  printf '%s\n' "$@" > "$FIXTURE_DIR/$endpoint/$n.txt"
}

fixture_vacia() {
  local endpoint="$1" n="$2"
  mkdir -p "$FIXTURE_DIR/$endpoint"
  : > "$FIXTURE_DIR/$endpoint/$n.txt"
}

nueva_fixture_dir() {
  FIXTURE_DIR="$(mktemp -d "$TMP_ROOT/fixtures.XXXXXX")"
}

# ejecutar <numero-PR> [flags del guion...]  -> deja CODIGO y SALIDA (stdout)
# --intervalo-s 1 y el timeout corto vía variable de entorno (solo de test,
# ver esperar-ci.sh) mantienen cada escenario en el orden de 1-2 s reales.
ejecutar() {
  local pr="$1"; shift
  set +e
  SALIDA="$(FIXTURE_DIR="$FIXTURE_DIR" ESPERAR_CI_TEST_TIMEOUT_S="${TIMEOUT_S_PRUEBA:-2}" \
    PATH="$MOCK_BIN:$PATH" \
    bash "$SCRIPT" "$pr" --repo "$REPO_TEST" --intervalo-s 1 "$@" \
    2>"$TMP_ROOT/ultimo-stderr.log")"
  CODIGO=$?
  set -e
}

assert_veredicto() {
  local descripcion="$1" estado_esperado="$2" codigo_esperado="$3" contiene="${4:-}"
  PRUEBAS=$((PRUEBAS + 1))
  local linea
  linea="$(printf '%s\n' "$SALIDA" | grep '^VEREDICTO: ' || true)"
  local ok=1
  if [[ "$CODIGO" != "$codigo_esperado" ]]; then
    ok=0
  fi
  if [[ "$linea" != "VEREDICTO: $estado_esperado"* ]]; then
    ok=0
  fi
  if [[ -n "$contiene" ]] && [[ "$linea" != *"$contiene"* ]]; then
    ok=0
  fi
  if [[ "$ok" == 1 ]]; then
    echo "OK: $descripcion"
  else
    echo "FALLO: $descripcion" >&2
    echo "  esperado: estado=$estado_esperado codigo=$codigo_esperado contiene='$contiene'" >&2
    echo "  obtenido: codigo=$CODIGO salida='$linea'" >&2
    echo "  --- stderr (progreso) ---" >&2
    sed 's/^/  /' "$TMP_ROOT/ultimo-stderr.log" >&2
    FALLOS=$((FALLOS + 1))
  fi
}

BP3=("Check A" "Check B" "Check C")
fixture_branch_protection() {
  fixture BRANCH_PROTECTION "$1" "${BP3[@]}"
}

echo "=== --hasta checks: VERDE cuando los tres obligatorios pasan ==="
nueva_fixture_dir
fixture PR_VIEW 1 "OPEN	AAA	CLEAN		"
fixture_branch_protection 1
fixture PR_CHECKS 1 $'Check A\tpass' $'Check B\tpass' $'Check C\tpass'
ejecutar 100 --hasta checks
assert_veredicto "los tres obligatorios en verde -> VERDE" VERDE 0 "AAA"

echo
echo "=== 'Condición que termina antes': NO da VERDE con checks solo parcialmente en pass ==="
nueva_fixture_dir
fixture PR_VIEW 1 "OPEN	AAA	CLEAN		"
fixture PR_VIEW 2 "OPEN	AAA	CLEAN		"
fixture_branch_protection 1
fixture_branch_protection 2
# Primera consulta: A ya en pass, B y C aún pendientes — un guion que se
# conformara con "al menos uno en pass" o "el primero de la lista" daría
# VERDE aquí, que sería falso.
fixture PR_CHECKS 1 $'Check A\tpass' $'Check B\tpending' $'Check C\tpending'
fixture PR_CHECKS 2 $'Check A\tpass' $'Check B\tpass' $'Check C\tpass'
ejecutar 101 --hasta checks
assert_veredicto "solo tras la segunda vuelta, con los tres en pass, sale VERDE" VERDE 0 ""
PRUEBAS=$((PRUEBAS + 1))
if grep -q "aún no completos" "$TMP_ROOT/ultimo-stderr.log"; then
  echo "OK: el progreso quedó registrado como incompleto en la primera vuelta"
else
  echo "FALLO: no se registró la espera de la primera vuelta (¿el guion decidió con datos parciales?)" >&2
  FALLOS=$((FALLOS + 1))
fi

echo
echo "=== 'Datos del árbol equivocado': un check CANCELADO en el HEAD vigente es ROJO, no 'sigue corriendo' ==="
nueva_fixture_dir
fixture PR_VIEW 1 "OPEN	AAA	CLEAN		"
fixture_branch_protection 1
fixture PR_CHECKS 1 $'Check A\tpass' $'Check B\tcancel' $'Check C\tpending'
ejecutar 102 --hasta checks
assert_veredicto "check B cancelado -> ROJO inmediato" ROJO 1 "Check B"

echo
echo "=== check obligatorio ausente: nunca aparece -> TIMEOUT lo nombra, no un falso 'terminado' ==="
nueva_fixture_dir
fixture PR_VIEW 1 "OPEN	AAA	CLEAN		"
fixture_branch_protection 1
# Check C nunca llega a existir como check run (workflow que nunca se disparó).
fixture PR_CHECKS 1 $'Check A\tpass' $'Check B\tpass'
ejecutar 103 --hasta checks
assert_veredicto "Check C nunca aparece -> TIMEOUT" TIMEOUT 3 "Check C"

echo
echo "=== PR cerrada sin fusionar -> ROJO, en cualquier fase pedida ==="
nueva_fixture_dir
fixture PR_VIEW 1 "CLOSED	AAA	CLOSED		"
ejecutar 104 --hasta despliegue
assert_veredicto "PR cerrada sin fusionar -> ROJO" ROJO 1 "cerrada sin fusionar"

echo
echo "=== 'Campo parecido, mecanismo distinto': mergeStateStatus=CLEAN NO es estar en cola ni fusionada ==="
nueva_fixture_dir
fixture PR_VIEW 1 "OPEN	AAA	CLEAN		"
fixture_branch_protection 1
fixture PR_CHECKS 1 $'Check A\tpass' $'Check B\tpass' $'Check C\tpass'
# mergeStateStatus se queda en CLEAN (mergeable, sin conflicto) en TODAS las
# vueltas de fase merge — eso no implica fusión. Solo el cambio de PR_VIEW a
# MERGED (llamada 3) debe producir el VERDE.
fixture PR_VIEW 2 "OPEN	AAA	CLEAN		"
fixture PR_VIEW 3 "MERGED	AAA	MERGED	SHAFUSION	2026-09-17T10:00:00Z"
fixture MERGE_QUEUE 1 $'QUEUED\tSINT1\tPENDING'
fixture MERGE_QUEUE 2 $'AWAITING_CHECKS\tSINT1\tSUCCESS'
ejecutar 105 --hasta merge
assert_veredicto "espera la fusión real, no el mergeStateStatus" VERDE 0 "SHAFUSION"

echo
echo "=== EXPULSADA_DE_COLA: mergeQueueEntry pasa a NULL con un evento de expulsión reciente ==="
nueva_fixture_dir
fixture PR_VIEW 1 "OPEN	AAA	CLEAN		"
fixture_branch_protection 1
fixture PR_CHECKS 1 $'Check A\tpass' $'Check B\tpass' $'Check C\tpass'
fixture PR_VIEW 2 "OPEN	AAA	CLEAN		"
fixture PR_VIEW 3 "OPEN	AAA	CLEAN		"
fixture MERGE_QUEUE 1 $'QUEUED\tSINT1\tPENDING'
fixture MERGE_QUEUE 2 "NULL"
fixture TIMELINE 1 $'2099-01-01T00:00:00Z\tfailed_checks'
TIMEOUT_S_PRUEBA=6 ejecutar 106 --hasta merge
assert_veredicto "expulsión detectada por el evento de timeline, no adivinada" EXPULSADA_DE_COLA 2 "failed_checks"

echo
echo "=== auto-merge NO es cola: mergeQueueEntry siempre NULL, sin evento de expulsión -> TIMEOUT, nunca VERDE ni EXPULSADA ==="
nueva_fixture_dir
fixture PR_VIEW 1 "OPEN	AAA	CLEAN		"
fixture_branch_protection 1
fixture PR_CHECKS 1 $'Check A\tpass' $'Check B\tpass' $'Check C\tpass'
fixture PR_VIEW 2 "OPEN	AAA	CLEAN		"
fixture MERGE_QUEUE 1 "NULL"
fixture TIMELINE 1 "NINGUNO"
ejecutar 107 --hasta merge
assert_veredicto "nunca entró en cola (aunque tuviera auto-merge armado aparte) -> TIMEOUT" TIMEOUT 3 ""

echo
echo "=== Despliegue VERDE: 'Desplegar a staging' concluye success sobre el SHA de fusión ==="
nueva_fixture_dir
fixture PR_VIEW 1 "MERGED	AAA	MERGED	SHAOK	2026-09-17T10:00:00Z"
fixture RUN_LIST 1 $'999\tcompleted\tsuccess\t2026-09-17T10:05:00Z'
fixture RUN_VIEW_JOBS 1 $'Desplegar a staging\tcompleted\tsuccess' $'Aprobar despliegue a producción\tcompleted\tsuccess'
ejecutar 108 --hasta despliegue
assert_veredicto "staging en verde -> VERDE" VERDE 0 "999"

echo
echo "=== Despliegue ROJO: 'Desplegar a staging' falla y NO es por SHA obsoleto ==="
nueva_fixture_dir
fixture PR_VIEW 1 "MERGED	AAA	MERGED	SHAMAL	2026-09-17T10:00:00Z"
fixture RUN_LIST 1 $'998\tcompleted\tfailure\t2026-09-17T10:05:00Z'
fixture RUN_VIEW_JOBS 1 $'Desplegar a staging\tcompleted\tfailure'
fixture RUN_LOG_FAILED 1 "algo se rompió compilando la imagen, nada que ver con la punta de main"
ejecutar 109 --hasta despliegue
assert_veredicto "falla real de staging -> ROJO, no OBSOLETO" ROJO 1 "Desplegar a staging"

echo
echo "=== OBSOLETO: el run de Desplegar se descarta porque main avanzó (mensaje literal de deploy.yml) ==="
nueva_fixture_dir
fixture PR_VIEW 1 "MERGED	AAA	MERGED	SHAVIEJO	2026-09-17T10:00:00Z"
fixture RUN_LIST 1 $'997\tcompleted\tfailure\t2026-09-17T10:05:00Z'
fixture RUN_VIEW_JOBS 1 $'Desplegar a staging\tcompleted\tfailure'
fixture RUN_LOG_FAILED 1 '::error::run obsoleto para despliegue — SHAVIEJO ya no es la punta de main (SHANUEVO)'
ejecutar 110 --hasta despliegue
assert_veredicto "mensaje de run obsoleto en el log -> OBSOLETO, no ROJO" OBSOLETO 4 "997"

echo
echo "=== 'Datos del árbol equivocado' (despliegue): un run todavía no existe para el SHA -> sigue esperando, no falso VERDE ni falso OBSOLETO ==="
nueva_fixture_dir
fixture PR_VIEW 1 "MERGED	AAA	MERGED	SHANUEVO	2026-09-17T10:00:00Z"
fixture RUN_LIST 1 "NINGUNO"
fixture RUN_LIST 2 "NINGUNO"
fixture RUN_LIST 3 $'1000\tcompleted\tsuccess\t2026-09-17T10:06:00Z'
fixture RUN_VIEW_JOBS 1 $'Desplegar a staging\tcompleted\tsuccess'
TIMEOUT_S_PRUEBA=6 ejecutar 111 --hasta despliegue
assert_veredicto "espera a que el run de Desplegar exista antes de fallar u opinar" VERDE 0 "1000"

echo
echo "=== TIMEOUT de despliegue: el run nunca llega, último estado observado se informa (no 'terminó') ==="
nueva_fixture_dir
fixture PR_VIEW 1 "MERGED	AAA	MERGED	SHALENTO	2026-09-17T10:00:00Z"
fixture RUN_LIST 1 "NINGUNO"
ejecutar 112 --hasta despliegue
assert_veredicto "sin run tras agotar el tiempo -> TIMEOUT con el último estado" TIMEOUT 3 "SHALENTO"

echo
echo "=== Revisión Codex (2026-09-17), P1: staging concluye mientras el run global sigue 'in_progress' ==="
echo "    esperando la aprobación humana de producción — no debe dar TIMEOUT por eso ==="
nueva_fixture_dir
fixture PR_VIEW 1 "MERGED	AAA	MERGED	SHASTAGINGOK	2026-09-17T10:00:00Z"
fixture RUN_LIST 1 $'995\tin_progress\t\t2026-09-17T10:05:00Z'
fixture RUN_LIST 2 $'995\tin_progress\t\t2026-09-17T10:05:00Z'
# El run global sigue "in_progress" (esperando el job de aprobación) en TODAS
# las vueltas de RUN_LIST de este escenario, pero el job de staging ya
# concluyó en la primera consulta de jobs.
fixture RUN_VIEW_JOBS 1 $'Desplegar a staging\tcompleted\tsuccess' $'Aprobar despliegue a producción\tin_progress\t'
TIMEOUT_S_PRUEBA=6 ejecutar 113 --hasta despliegue
assert_veredicto "staging ya en verde aunque el run siga esperando aprobación -> VERDE, no TIMEOUT" VERDE 0 "995"

echo
echo "=== Revisión Codex (2026-09-17), P1: un fallo transitorio leyendo los jobs NO se infiere de la conclusión agregada ==="
nueva_fixture_dir
fixture PR_VIEW 1 "MERGED	AAA	MERGED	SHAJOBSFALLAN	2026-09-17T10:00:00Z"
fixture RUN_LIST 1 $'994\tcompleted\tsuccess\t2026-09-17T10:05:00Z'
fixture RUN_LIST 2 $'994\tcompleted\tsuccess\t2026-09-17T10:05:00Z'
# `gh run view --json jobs` falla la primera vez (mock: MOCK_ERROR) aunque la
# conclusión AGREGADA del run ya diga "success" — un guion que se fiara de esa
# agregada daría VERDE sin haber visto que staging de verdad pasó. Solo tras
# poder leer los jobs de verdad (segunda vuelta) debe decidir.
fixture RUN_VIEW_JOBS 1 "MOCK_ERROR"
fixture RUN_VIEW_JOBS 2 $'Desplegar a staging\tcompleted\tsuccess'
TIMEOUT_S_PRUEBA=6 ejecutar 114 --hasta despliegue
assert_veredicto "fallo de lectura de jobs no se disfraza de VERDE por la agregada" VERDE 0 "994"
PRUEBAS=$((PRUEBAS + 1))
if grep -q "sin inferir nada de la conclusión agregada" "$TMP_ROOT/ultimo-stderr.log"; then
  echo "OK: el fallo de lectura de jobs quedó registrado como tal, no como 'sigue corriendo'"
else
  echo "FALLO: no se registró el fallo de lectura de jobs" >&2
  FALLOS=$((FALLOS + 1))
fi

echo
echo "=== Revisión Codex (2026-09-17), P2: un fallo de API leyendo branch protection NO se confunde con 'cero checks obligatorios' ==="
nueva_fixture_dir
fixture PR_VIEW 1 "OPEN	AAA	CLEAN		"
# Las tres primeras consultas a branch protection fallan (403/rate-limit
# simulados); a la cuarta ya se habría agotado el margen de reintentos (3) y
# el guion debe salir con el código de error de ENTORNO (64), nunca con
# VEREDICTO: TIMEOUT — que dejaría pensar que el problema fue esperar a CI.
fixture BRANCH_PROTECTION 1 "MOCK_ERROR"
fixture BRANCH_PROTECTION 2 "MOCK_ERROR"
fixture BRANCH_PROTECTION 3 "MOCK_ERROR"
TIMEOUT_S_PRUEBA=600 ejecutar 115 --hasta checks
PRUEBAS=$((PRUEBAS + 1))
if [[ "$CODIGO" == "64" ]] && ! printf '%s\n' "$SALIDA" | grep -q '^VEREDICTO:'; then
  echo "OK: fallo de API en branch protection -> error de entorno (64), no un VEREDICTO: TIMEOUT disfrazado"
else
  echo "FALLO: se esperaba código 64 sin línea VEREDICTO — obtenido código=$CODIGO salida='$SALIDA'" >&2
  FALLOS=$((FALLOS + 1))
fi

echo
echo "Pruebas: $PRUEBAS · Fallos: $FALLOS"
(( FALLOS == 0 )) || exit 1
