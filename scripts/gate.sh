#!/bin/bash
# Gate local único (S8 del análisis de causas raíz de 2026-10-02): ejecuta, en local, lo que el job
# «Build, format y tests» de .github/workflows/ci.yml ejecuta y que hasta ahora se escapaba.
#
# POR QUÉ EXISTE. Un verde local no equivalía al gate de CI: `dotnet format --verify-no-changes` es un
# job propio que el flujo local no ejecutaba (la PR #983 salió en rojo con todo verde en local), y un
# `dotnet build` sin -warnaserror oculta CS8604 que CI convierte en error. Formato fue 7 de los 54 rojos
# de PR medidos en la ventana 2026-09-20..2026-10-02. Este guion es el ÚNICO sitio donde el orden y los
# flags están escritos; el gancho .githooks/pre-push solo lo invoca.
#
# ETAPAS (en este orden; la primera que falla corta, salvo --todo):
#   formato        dotnet format CaeManager.slnx --no-restore --verify-no-changes   (CI: job «Formato»)
#   compilacion    dotnet build  CaeManager.slnx --no-restore -warnaserror          (CI: «Build, format y tests»)
#   arquitectura   dotnet test tests/CaeManager.Architecture.Tests                  (CI: ídem)
#   dominio        dotnet test tests/CaeManager.Domain.Tests                        (CI: ídem)
#   aplicacion     dotnet test tests/CaeManager.Application.Tests                   (CI: ídem)
#   web            dotnet test tests/CaeManager.Web.Tests — SOLO si el diff toca Web o sus tests,
#                  o con --web (CI: job de bUnit)
#   integracion    solo con --integracion; envuelta en scripts/turno-postgres.sh (clúster compartido)
#
# MODOS:
#   (sin flag)       todas las etapas salvo integracion; web solo si el diff la toca.
#   --rapido         formato + compilacion + arquitectura (la parte que más rojos de CI evita; es la que
#                    corre el gancho pre-push).
#   --web            fuerza la etapa web.   --integracion  añade la etapa integracion.
#   --todo           no corta en la primera etapa roja: las ejecuta todas y lista las rojas.
#
# LO QUE ESTE GATE NO CUBRE (declarado, no por inercia): E2E (Playwright), gitleaks, licencias NuGet,
# Trivy, k6, los tests de los guiones de despliegue y la cola de fusión. Un verde aquí NO sustituye a CI
# (§ 17 del protocolo): es el mismo instrumento solo en las etapas que enumera arriba.
#
# CONTROL «cuántos tests corrieron» (§ 3): una etapa de tests que termina en 0 con 0 pruebas, o cuya salida
# no trae el resumen, es ROJA, no verde: sin base de datos o con un filtro que no casa, `dotnet test` sale
# con 0 y no corre nada.
#
# SALIDA: una línea `GATE: <etapa> OK|ROJO (<s>s)` por etapa y una final `GATE: VERDE ...` o `GATE: ROJO ...`.
# Códigos de salida: 0 verde · 1 rojo · 2 uso incorrecto · 75 sin turno de PostgreSQL (propagado).
#
# VARIABLES (para las pruebas del guion): GATE_SLN (por defecto CaeManager.slnx), GATE_BASE (por defecto
# origin/main), GATE_DOTNET (por defecto dotnet).
set -uo pipefail

RAIZ="$(git rev-parse --show-toplevel)" || { echo "GATE: no estoy dentro de un repositorio git" >&2; exit 2; }
cd "$RAIZ" || exit 2

SLN="${GATE_SLN:-CaeManager.slnx}"
BASE="${GATE_BASE:-origin/main}"
DOTNET="${GATE_DOTNET:-dotnet}"

RAPIDO=0; FORZAR_WEB=0; INTEGRACION=0; TODO=0
for arg in "$@"; do
  case "$arg" in
    --rapido) RAPIDO=1 ;;
    --web) FORZAR_WEB=1 ;;
    --integracion) INTEGRACION=1 ;;
    --todo) TODO=1 ;;
    -h|--help) sed -n '2,45p' "$0"; exit 0 ;;
    *) echo "GATE: opción desconocida '$arg' (usa --rapido, --web, --integracion, --todo)" >&2; exit 2 ;;
  esac
done

LOGS="$(mktemp -d)"
trap 'rm -rf "$LOGS"' EXIT
INICIO=$SECONDS
FALLIDAS=()
EJECUTADAS=0

# Ficheros que toca el trabajo: lo comprometido desde la base MÁS lo que aún no está comprometido.
# Si la base no existe (clon sin `git fetch`), se dice y se asume que sí toca Web: fallar hacia más
# pruebas, nunca hacia menos.
diff_toca_web() {
  local cambios
  if git rev-parse --verify --quiet "$BASE" >/dev/null; then
    cambios="$(git diff --name-only "$BASE"...HEAD; git diff --name-only HEAD; git ls-files --others --exclude-standard)"
  else
    echo "GATE: AVISO la base '$BASE' no existe (¿falta git fetch?); se asume que el diff toca Web" >&2
    return 0
  fi
  grep -Eq '^(src/CaeManager\.Web/|tests/CaeManager\.Web\.Tests/)' <<<"$cambios"
}

# Una etapa de compilación o de formato: éxito = código 0.
etapa_simple() {
  local nombre="$1"; shift
  local log="$LOGS/$nombre.log" t0=$SECONDS rc
  "$@" >"$log" 2>&1
  rc=$?
  informar "$nombre" "$rc" "$log" "" $((SECONDS - t0))
}

# Una etapa de tests: además del código 0 exige que corrieran pruebas (>0) y que el resumen exista.
etapa_tests() {
  local nombre="$1"; shift
  local log="$LOGS/$nombre.log" t0=$SECONDS rc total motivo=""
  "$@" >"$log" 2>&1
  rc=$?
  if [ "$rc" -eq 75 ]; then
    cat "$log"
    echo "GATE: $nombre ROJO — sin turno de PostgreSQL (turno-postgres.sh salió con 75, no ejecutó nada)"
    echo "GATE: ROJO — la etapa $nombre NO se ejecutó; esto no es un verde"
    exit 75
  fi
  total="$(grep -Eo 'Total( de pruebas)?:[[:space:]]*[0-9]+' "$log" | grep -Eo '[0-9]+$' | awk '{ s += $1 } END { if (NR > 0) print s }')"
  if [ "$rc" -eq 0 ]; then
    if [ -z "$total" ]; then
      motivo="el resultado no trae el resumen de pruebas: no puedo saber cuántas corrieron"
      rc=1
    elif [ "$total" -eq 0 ]; then
      motivo="0 pruebas ejecutadas (filtro que no casa, o descubrimiento roto): un verde sin pruebas no cuenta"
      rc=1
    else
      motivo="$total pruebas"
    fi
  fi
  informar "$nombre" "$rc" "$log" "$motivo" $((SECONDS - t0))
}

informar() {
  local nombre="$1" rc="$2" log="$3" motivo="$4" seg="$5"
  EJECUTADAS=$((EJECUTADAS + 1))
  if [ "$rc" -eq 0 ]; then
    echo "GATE: $nombre OK (${seg}s)${motivo:+ — $motivo}"
    return 0
  fi
  FALLIDAS+=("$nombre")
  echo "GATE: $nombre ROJO (${seg}s)${motivo:+ — $motivo}"
  # Lo que nombra el fallo: errores de formato/compilación y pruebas rojas, acotado.
  grep -E ' error |: error |\[FAIL\]|Failed |Con error|error CS|error MSB|WHITESPACE|CHARSET|FINALNEWLINE|IMPORTS' "$log" | head -n 25 | sed 's/^/    /'
  echo "    (salida completa: $log; se borra al salir — relanza la etapa a mano si la necesitas)"
  if [ "$TODO" -eq 0 ]; then
    final
  fi
}

final() {
  local seg=$((SECONDS - INICIO))
  if [ "${#FALLIDAS[@]}" -eq 0 ]; then
    echo "GATE: VERDE ($EJECUTADAS etapas, ${seg}s) — recuerda: no sustituye a CI (E2E, gitleaks, licencias, Trivy)"
    exit 0
  fi
  echo "GATE: ROJO — fallaron: ${FALLIDAS[*]} (${seg}s)"
  exit 1
}

# El restore de CI. Se hace aquí y no se deja implícito en el build para que los dos pasos siguientes
# puedan llevar --no-restore, igual que CI; y para detectar si reescribe los packages.lock.json
# (rangos con comodín, memoria hydra-packages-lock-drift-restore-local).
LOCKS_ANTES="$(git status --short -- '*packages.lock.json' | sort)"
"$DOTNET" restore "$SLN" >"$LOGS/restore.log" 2>&1 || {
  echo "GATE: restauracion ROJO"; tail -n 20 "$LOGS/restore.log" | sed 's/^/    /'; echo "GATE: ROJO — fallaron: restauracion"; exit 1; }
LOCKS_DESPUES="$(git status --short -- '*packages.lock.json' | sort)"
if [ "$LOCKS_ANTES" != "$LOCKS_DESPUES" ]; then
  echo "GATE: AVISO el restore modificó packages.lock.json; revisa 'git status' antes de comprometer"
fi

# `dotnet format --verify-no-changes` sale con 2 también cuando solo hay ADVERTENCIAS de analizador y
# ningún fichero que reformatear (medido el 2026-10-02 en Windows con el SDK 10.0.302: CA2255 de
# tests/CaeManager.IntegrationTests/BootstrapDeClusterEnTests.cs, presente en un origin/main limpio; el
# subcomando `whitespace` y el `style` salen con 0 y solo `analyzers` con 2; en CI, sobre Linux, ese mismo
# comando pasa). Un gate que sale rojo SIEMPRE enseña a ignorarlo, así que la etapa distingue: un fallo de
# formato es una línea `<fichero>(l,c): error <REGLA>: …`; si el código es distinto de 0 pero no hay
# ninguna, se avisa y no cuenta como rojo. La prueba de este guion fija las dos mitades: sin líneas
# `error` es verde con aviso; con ellas, rojo que nombra el fichero. HYPOTHESIS: la diferencia con CI
# viene de la versión del SDK/analizadores; si CI empezara a salir con 2 por lo mismo, esto no lo oculta.
etapa_formato() {
  local log="$LOGS/formato.log" t0=$SECONDS rc
  "$DOTNET" format "$SLN" --no-restore --verify-no-changes >"$log" 2>&1
  rc=$?
  if [ "$rc" -ne 0 ] && ! grep -Eq ': error ' "$log"; then
    echo "GATE: AVISO formato: dotnet format salió con $rc sin ninguna línea de error de formato (solo advertencias de analizador); no cuenta como rojo"
    grep -E 'warning ' "$log" | head -n 3 | sed 's/^/    /'
    rc=0
  fi
  informar formato "$rc" "$log" "" $((SECONDS - t0))
}

etapa_formato
etapa_simple compilacion "$DOTNET" build "$SLN" --no-restore -warnaserror -v q
etapa_tests arquitectura "$DOTNET" test tests/CaeManager.Architecture.Tests --no-build -v q

if [ "$RAPIDO" -eq 0 ]; then
  etapa_tests dominio "$DOTNET" test tests/CaeManager.Domain.Tests --no-build -v q
  etapa_tests aplicacion "$DOTNET" test tests/CaeManager.Application.Tests --no-build -v q

  if [ "$FORZAR_WEB" -eq 1 ] || diff_toca_web; then
    etapa_tests web "$DOTNET" test tests/CaeManager.Web.Tests --no-build -v q
  else
    echo "GATE: web OMITIDA — el diff no toca src/CaeManager.Web ni tests/CaeManager.Web.Tests (usa --web para forzarla)"
  fi

  if [ "$INTEGRACION" -eq 1 ]; then
    etapa_tests integracion bash scripts/turno-postgres.sh -- "$DOTNET" test tests/CaeManager.IntegrationTests --no-build -v q
  fi
fi

final
