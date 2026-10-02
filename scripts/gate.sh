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
#   migraciones    dotnet tool restore + dotnet ef migrations has-pending-model-changes (CI: job
#                  «Detectar migraciones EF olvidadas»); solo fuera de --rapido
#   web            dotnet test tests/CaeManager.Web.Tests (CI: job de bUnit). Web.Tests referencia Web y
#                  esta arrastra Application, Infrastructure y Domain: un cambio en cualquier parte de src/
#                  puede romperlos, así que solo se OMITE si el diff no toca src/, ni los tests de Web, ni
#                  la configuración de compilación (Directory.Build.props, *.csproj, packages.lock.json,
#                  CaeManager.slnx). --web la fuerza siempre.
#   integracion    solo con --integracion; envuelta en scripts/turno-postgres.sh (clúster compartido)
#
# MODOS:
#   (sin flag)       todas las etapas salvo integracion; web salvo que el diff no toque código (ver arriba).
#   --rapido         formato + compilacion + arquitectura (la parte que más rojos de CI evita; es la que
#                    corre el gancho pre-push).
#   --web            fuerza la etapa web.   --integracion  añade la etapa integracion. Ninguna de las dos
#                    se combina con --rapido (que no las ejecuta): sería un verde sin la etapa pedida, y
#                    sale con código 2.
#   --todo           no corta en la primera etapa roja: las ejecuta todas y lista las rojas.
#
# LO QUE ESTE GATE NO CUBRE (declarado, no por inercia): E2E (Playwright), los bloques de integración
# (salvo --integracion), la cobertura por capas y sus umbrales ratchet, el build de la imagen Docker,
# paquetes vulnerables, licencias NuGet, gitleaks, Trivy, k6, los tests de los guiones de despliegue, los
# arneses de la extensión y la cola de fusión. Un verde aquí NO sustituye a CI
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

if [ "$RAPIDO" -eq 1 ] && { [ "$FORZAR_WEB" -eq 1 ] || [ "$INTEGRACION" -eq 1 ]; }; then
  echo "GATE: --rapido no ejecuta las etapas web ni integracion; no lo combines con --web ni --integracion (sería un verde sin la etapa pedida)" >&2
  exit 2
fi

LOGS="$(mktemp -d)"
trap 'rm -rf "$LOGS"' EXIT
INICIO=$SECONDS
FALLIDAS=()
EJECUTADAS=0

# Ficheros que toca el trabajo: lo comprometido desde la base MÁS lo que aún no está comprometido, con
# --no-renames para que mover un fichero cuente su origen Y su destino. Si la base no existe (clon sin
# `git fetch`), se dice y se asume que sí toca código: fallar hacia más pruebas, nunca hacia menos.
diff_toca_codigo() {
  local cambios
  if git rev-parse --verify --quiet "$BASE" >/dev/null; then
    cambios="$(git diff --name-only --no-renames "$BASE"...HEAD; git diff --name-only --no-renames HEAD; git ls-files --others --exclude-standard)"
  else
    echo "GATE: AVISO la base '$BASE' no existe (¿falta git fetch?); se asume que el diff toca código" >&2
    return 0
  fi
  grep -Eq '^(src/|tests/CaeManager\.Web\.Tests/|Directory\.Build\.props$|CaeManager\.slnx$|.*\.csproj$|.*packages\.lock\.json$)' <<<"$cambios"
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
  local nombrado
  nombrado="$(grep -E ' error |: error |\[FAIL\]|Failed |Con error|error CS|error MSB|WHITESPACE|CHARSET|FINALNEWLINE|IMPORTS' "$log" | head -n 25)"
  # Si ninguna línea nombra el fallo (p. ej. `dotnet ef` imprime una frase sin «error»), se enseña el final del log.
  [ -z "$nombrado" ] && nombrado="$(tail -n 8 "$log")"
  sed 's/^/    /' <<<"$nombrado"
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

# Lo que hace el job «Detectar migraciones EF olvidadas»: falla si el modelo cambió sin migración.
migraciones_pendientes() {
  "$DOTNET" tool restore \
    && "$DOTNET" ef migrations has-pending-model-changes \
         --project src/CaeManager.Migrations.PostgreSQL --startup-project src/CaeManager.Web
}

# `dotnet format --verify-no-changes` sale con 2 también cuando solo hay ADVERTENCIAS de analizador y
# ningún fichero que reformatear (medido el 2026-10-02 en Windows con el SDK 10.0.302: la advertencia CA2255
# de tests/CaeManager.IntegrationTests/BootstrapDeClusterEnTests.cs, presente en un origin/main limpio; el
# subcomando `whitespace` y el `style` salen con 0 y solo `analyzers` con 2; en CI, sobre Linux, ese mismo
# comando pasa). Un gate que sale rojo SIEMPRE enseña a ignorarlo, así que se tolera ESA situación y solo
# esa: código EXACTAMENTE 2, ninguna línea `<fichero>(l,c): error <REGLA>: …`, al menos una advertencia y
# todas las advertencias de la lista GATE_ADVERTENCIAS_TOLERADAS (por defecto CA2255). Cualquier otra cosa
# es rojo: otro código (1, 137…: puede ser un fallo del propio dotnet format), una advertencia que CI sí
# podría tratar como fijable (IDE…, CA… nuevos) o una salida vacía. HYPOTHESIS: la diferencia con CI viene
# de la versión del SDK/analizadores; si CI empezara a salir con 2 por lo mismo, esto no lo oculta.
etapa_formato() {
  local log="$LOGS/formato.log" t0=$SECONDS rc motivo=""
  local toleradas="${GATE_ADVERTENCIAS_TOLERADAS:-CA2255}"
  "$DOTNET" format "$SLN" --no-restore --verify-no-changes >"$log" 2>&1
  rc=$?
  if [ "$rc" -ne 0 ]; then
    if [ "$rc" -eq 2 ] && ! grep -Eq ': error ' "$log" \
       && grep -Eq ': warning ' "$log" && ! grep -E ': warning ' "$log" | grep -Evq "warning ($toleradas)[: ]"; then
      echo "GATE: AVISO formato: dotnet format salió con 2 sin ninguna línea de error de formato y solo con advertencias conocidas ($toleradas); no cuenta como rojo"
      grep -E ': warning ' "$log" | head -n 3 | sed 's/^/    /'
      rc=0
    elif ! grep -Eq ': error ' "$log"; then
      motivo="salió con $rc sin ninguna línea de error de formato ni solo advertencias conocidas: puede ser un fallo del propio dotnet format o una advertencia nueva"
    fi
  fi
  informar formato "$rc" "$log" "$motivo" $((SECONDS - t0))
}

etapa_formato
etapa_simple compilacion "$DOTNET" build "$SLN" --no-restore -warnaserror -v q
etapa_tests arquitectura "$DOTNET" test tests/CaeManager.Architecture.Tests --no-build -v q

if [ "$RAPIDO" -eq 0 ]; then
  etapa_tests dominio "$DOTNET" test tests/CaeManager.Domain.Tests --no-build -v q
  etapa_tests aplicacion "$DOTNET" test tests/CaeManager.Application.Tests --no-build -v q

  etapa_simple migraciones migraciones_pendientes

  if [ "$FORZAR_WEB" -eq 1 ] || diff_toca_codigo; then
    etapa_tests web "$DOTNET" test tests/CaeManager.Web.Tests --no-build -v q
  else
    echo "GATE: web OMITIDA — el diff no toca src/, ni los tests de Web, ni la configuración de compilación (usa --web para forzarla)"
  fi

  if [ "$INTEGRACION" -eq 1 ]; then
    etapa_tests integracion bash scripts/turno-postgres.sh -- "$DOTNET" test tests/CaeManager.IntegrationTests --no-build -v q
  fi
fi

final
