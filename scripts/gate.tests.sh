#!/bin/bash
# Pruebas de scripts/gate.sh sin .NET real: un `dotnet` falso (en GATE_DOTNET) registra cada invocación y
# se comporta según variables de entorno, dentro de un repositorio git temporal con una base origin/main.
#
# La propiedad que importa no es «el guion corre»; es que:
#   (a) el formato se ejecuta CON --verify-no-changes y su fallo nombra el fichero y corta antes del build;
#   (b) la compilación lleva -warnaserror (un warning es un fallo);
#   (c) una etapa de tests con 0 pruebas, o sin resumen, es ROJA aunque `dotnet test` salga con 0;
#   (d) Web se ejecuta solo si el diff la toca (o con --web) y su omisión se IMPRIME;
#   (e) --rapido no ejecuta dominio, aplicación ni web; --todo lista TODAS las etapas rojas;
#   (f) sin turno de PostgreSQL (75) el guion sale con 75 y dice que la etapa NO se ejecutó;
#   (g) MUTACIÓN: quitar --verify-no-changes, quitar -warnaserror o quitar el control de 0 pruebas
#       del propio guion pone en rojo la aserción que lo vigila (un test que no puede fallar no cuenta).
set -uo pipefail

AQUI="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
GATE_REAL="$AQUI/gate.sh"
TMP_ROOT="$(mktemp -d)"
trap 'rm -rf "$TMP_ROOT"' EXIT

FALLOS=0
PRUEBAS=0
ok()  { PRUEBAS=$((PRUEBAS + 1)); echo "  ok    $1"; }
mal() { PRUEBAS=$((PRUEBAS + 1)); FALLOS=$((FALLOS + 1)); echo "  FALLO $1"; }
aserta() { # aserta <descripcion> <comando...>
  local d="$1"; shift
  if "$@"; then ok "$d"; else mal "$d"; fi
}
contiene()    { grep -qF -- "$2" <<<"$1"; }
no_contiene() { ! grep -qF -- "$2" <<<"$1"; }

# ── repositorio y dotnet falsos ─────────────────────────────────────────────
REPO="$TMP_ROOT/repo"
BIN="$TMP_ROOT/bin"
LOG="$TMP_ROOT/dotnet.log"
mkdir -p "$REPO/src/CaeManager.Web" "$REPO/src/CaeManager.Application" "$REPO/tests/CaeManager.Web.Tests" "$REPO/scripts" "$BIN"

cat >"$BIN/dotnet" <<'FALSO'
#!/bin/bash
# dotnet falso: registra "orden args" y simula formato, build y test segun entorno.
echo "$*" >>"$FAKE_DOTNET_LOG"
orden="$1"
case "$orden" in
  restore) exit 0 ;;
  format)
    if [ "${FORMATO_ROJO:-0}" = "1" ] && [[ " $* " == *" --verify-no-changes "* ]]; then
      echo "src/CaeManager.Application/Mal.cs(3,1): error WHITESPACE: Corrija el formato de espacio en blanco."
      exit 2
    fi
    if [ "${FORMATO_SOLO_AVISOS:-0}" = "1" ] && [[ " $* " == *" --verify-no-changes "* ]]; then
      echo "tests/CaeManager.IntegrationTests/Boot.cs(43,6): warning CA2255: El atributo 'ModuleInitializer' solo esta pensado para ..."
      exit 2
    fi
    if [ "${FORMATO_ADVERTENCIA_NUEVA:-0}" = "1" ] && [[ " $* " == *" --verify-no-changes "* ]]; then
      echo "tests/CaeManager.IntegrationTests/Boot.cs(43,6): warning CA2255: El atributo 'ModuleInitializer' solo esta pensado para ..."
      echo "src/CaeManager.Application/Nuevo.cs(7,1): warning IDE0005: Using innecesario"
      exit 2
    fi
    if [ "${FORMATO_EXCEPCION:-0}" = "1" ] && [[ " $* " == *" --verify-no-changes "* ]]; then
      echo "Unhandled exception: System.OutOfMemoryException"
      exit "${FORMATO_CODIGO:-1}"
    fi
    if [ "${FORMATO_RC2_SIN_SALIDA:-0}" = "1" ] && [[ " $* " == *" --verify-no-changes "* ]]; then
      exit 2
    fi
    if [ "${FORMATO_CODIGO_1_CON_CA2255:-0}" = "1" ] && [[ " $* " == *" --verify-no-changes "* ]]; then
      echo "tests/CaeManager.IntegrationTests/Boot.cs(43,6): warning CA2255: El atributo 'ModuleInitializer' solo esta pensado para ..."
      exit 1
    fi
    exit 0 ;;
  tool) exit 0 ;;
  ef)
    if [ "${MIGRACION_PENDIENTE:-0}" = "1" ]; then
      echo "Changes have been made to the model since the last migration. Add a new migration."
      exit 1
    fi
    exit 0 ;;
  build)
    if [ "${WARNING_EN_BUILD:-0}" = "1" ] && [[ " $* " == *" -warnaserror "* ]]; then
      echo "src/CaeManager.Application/X.cs(9,9): error CS8604: posible argumento de referencia nula"
      exit 1
    fi
    exit 0 ;;
  test)
    proyecto="$2"
    if [ "${SIN_RESUMEN:-0}" = "1" ]; then exit 0; fi
    n="${PRUEBAS_POR_DEFECTO:-10}"
    if [[ "$proyecto" == *Architecture* ]] && [ -n "${PRUEBAS_ARQUITECTURA:-}" ]; then n="$PRUEBAS_ARQUITECTURA"; fi
    if [[ "${TESTS_ROJOS:-}" == *"$(basename "$proyecto")"* ]]; then
      echo "  [FAIL] Una.Prueba"
      echo "Con error! - Con error: 1, Superado: $((n - 1)), Omitido: 0, Total: $n"
      exit 1
    fi
    if [ "${RESUMEN_DOBLE:-0}" = "1" ]; then
      # dos TFM o dos ensamblados: el primero corre 10 pruebas y el segundo ninguna
      echo "Correctas! - Con error: 0, Superado: 10, Omitido: 0, Total: 10"
      echo "Correctas! - Con error: 0, Superado: 0, Omitido: 0, Total: 0"
      exit 0
    fi
    echo "Correctas! - Con error: 0, Superado: $n, Omitido: 0, Total: $n"
    exit 0 ;;
esac
exit 0
FALSO
chmod +x "$BIN/dotnet"

cat >"$REPO/scripts/turno-postgres.sh" <<'FALSO'
#!/bin/bash
# turno falso: sin turno (TURNO=0) no ejecuta y sale con 75 como el real.
if [ "${TURNO:-1}" = "0" ]; then echo "TURNO-POSTGRES: ABORTADO_SIN_EJECUTAR"; exit 75; fi
shift   # quita --
"$@"
FALSO

(
  cd "$REPO" || exit 1
  git init -q -b main . && git config user.email t@t && git config user.name t
  : >CaeManager.slnx
  : >src/CaeManager.Application/A.cs
  : >src/CaeManager.Web/W.razor
  : >tests/CaeManager.Web.Tests/W.cs
  # El gancho invoca scripts/gate.sh del repositorio: la copia va en el commit base (si fuera un fichero
  # sin seguimiento, `git clean` de limpiar() la borraría y el gancho fallaría con 127 en vez de ejecutar el gate).
  cp "$GATE_REAL" scripts/gate.sh
  git add -A && git commit -q -m base
  git update-ref refs/remotes/origin/main HEAD
  git switch -q -c rama
)

export FAKE_DOTNET_LOG="$LOG" GATE_DOTNET="$BIN/dotnet"

# gate <guion> [args...]  → imprime la salida y deja el código en $RC
RC=0
gate() {
  local guion="$1"; shift
  : >"$LOG"
  SALIDA="$(cd "$REPO" && bash "$guion" "$@" 2>&1)"; RC=$?
  if [ -n "${DEPURAR:-}" ]; then echo "--- rc=$RC args=$*"; echo "$SALIDA" | sed 's/^/    | /'; echo "--- log"; sed 's/^/    > /' "$LOG"; fi
}
tocar() { ( cd "$REPO" && mkdir -p "$(dirname "$1")" && echo "x$RANDOM" >>"$1" ); }
limpiar() { ( cd "$REPO" && git checkout -q -- . && git clean -fdq ); }
# comprometer: deja un cambio YA COMPROMETIDO en la rama (el camino «base...HEAD» de diff_toca_codigo);
# reiniciar vuelve la rama al commit base.
# gancho_de_ficheros: una lista de ficheros de configuración que deben ejecutar Web.Tests
comprometer() { ( cd "$REPO" && echo "c$RANDOM" >>"$1" && git add -A && git commit -q -m "cambio $1" ); }
reiniciar() { ( cd "$REPO" && git reset -q --hard "$(git rev-parse refs/remotes/origin/main)" && git clean -fdq ); }
orden_de() { grep -n "^$1" "$LOG" | head -n1 | cut -d: -f1; }

echo "scripts/gate.sh"

# 1. todo verde, diff que no toca código (una nota)
tocar NOTAS.txt
gate "$GATE_REAL"
aserta "verde: sale con 0" test "$RC" -eq 0
aserta "verde: línea final GATE: VERDE" contiene "$SALIDA" "GATE: VERDE"
aserta "verde: formato lleva --verify-no-changes" grep -q "^format .*--verify-no-changes" "$LOG"
aserta "verde: la compilación lleva -warnaserror" grep -q "^build .*-warnaserror" "$LOG"
aserta "verde: el formato corre ANTES de la compilación" test "$(orden_de format)" -lt "$(orden_de build)"
aserta "verde: corren arquitectura, dominio y aplicación" bash -c "grep -c '^test ' '$LOG' | grep -qx 3"
aserta "verde: corre el job de migraciones pendientes" grep -q "^ef migrations has-pending-model-changes" "$LOG"
aserta "verde: Web se omite y se dice" contiene "$SALIDA" "GATE: web OMITIDA"
limpiar

# 2. Web se ejecuta si el diff toca CUALQUIER código de src/, sus tests o la configuración de compilación
tocar src/CaeManager.Application/A.cs
gate "$GATE_REAL"
aserta "web: un cambio solo en Application también ejecuta Web.Tests (Web depende de ella)" grep -q "^test tests/CaeManager.Web.Tests" "$LOG"
aserta "web: ya no se imprime la omisión" no_contiene "$SALIDA" "GATE: web OMITIDA"
limpiar
tocar src/CaeManager.Web/W.razor
gate "$GATE_REAL"
aserta "web: con el diff tocando Web, Web.Tests corre" grep -q "^test tests/CaeManager.Web.Tests" "$LOG"
limpiar
tocar tests/CaeManager.Web.Tests/W.cs
gate "$GATE_REAL"
aserta "web: tocar solo sus tests también la ejecuta" grep -q "^test tests/CaeManager.Web.Tests" "$LOG"
limpiar
tocar Directory.Build.props
gate "$GATE_REAL"
aserta "web: tocar la configuración de compilación también la ejecuta" grep -q "^test tests/CaeManager.Web.Tests" "$LOG"
limpiar
tocar NOTAS.txt
gate "$GATE_REAL" --web
aserta "web: --web la fuerza aunque el diff no toque código" grep -q "^test tests/CaeManager.Web.Tests" "$LOG"
limpiar
comprometer src/CaeManager.Application/A.cs
gate "$GATE_REAL"
aserta "web: un cambio YA COMPROMETIDO (base...HEAD) ejecuta Web.Tests" grep -q "^test tests/CaeManager.Web.Tests" "$LOG"
reiniciar
comprometer NOTAS.txt
gate "$GATE_REAL"
aserta "web: una nota ya comprometida no la ejecuta" bash -c "! grep -q '^test tests/CaeManager.Web.Tests' '$LOG'"
reiniciar

# 2a. la configuración de compilación también cuenta (cada rama de la expresión de diff_toca_codigo)
for fichero in CaeManager.slnx tests/CaeManager.Domain.Tests/CaeManager.Domain.Tests.csproj tests/CaeManager.Domain.Tests/packages.lock.json; do
  tocar "$fichero"
  gate "$GATE_REAL"
  aserta "web: tocar $fichero ejecuta Web.Tests" grep -q "^test tests/CaeManager.Web.Tests" "$LOG"
  limpiar
done

# 2b. migraciones pendientes
MIGRACION_PENDIENTE=1 gate "$GATE_REAL"
aserta "migraciones: un modelo cambiado sin migración pone el gate en rojo" test "$RC" -ne 0
aserta "migraciones: la etapa se llama migraciones y dice qué falta" bash -c "grep -q 'GATE: migraciones ROJO' <<<\"\$1\" && grep -q 'Add a new migration' <<<\"\$1\"" _ "$SALIDA"

# 3. formato rojo: nombra el fichero y corta antes del build
FORMATO_ROJO=1 gate "$GATE_REAL"
aserta "formato: sale distinto de 0" test "$RC" -ne 0
aserta "formato: nombra el fichero mal formateado" contiene "$SALIDA" "Mal.cs"
aserta "formato: la etapa se llama formato" contiene "$SALIDA" "GATE: formato ROJO"
aserta "formato: no llega a compilar" bash -c "! grep -q '^build ' '$LOG'"
aserta "formato: la línea final lo nombra" contiene "$SALIDA" "GATE: ROJO — fallaron: formato"

# 3b. dotnet format que sale con 2 solo por advertencias de analizador: verde con aviso, no rojo permanente
FORMATO_SOLO_AVISOS=1 gate "$GATE_REAL"
aserta "format solo con advertencias: el gate no se pone rojo" test "$RC" -eq 0
aserta "format solo con advertencias: lo dice (AVISO) y nombra el warning" bash -c "grep -q 'GATE: AVISO formato' <<<\"\$1\" && grep -q 'CA2255' <<<\"\$1\"" _ "$SALIDA"
aserta "format solo con advertencias: la compilación sigue corriendo" grep -q "^build " "$LOG"

# 3c. la tolerancia es ESTRECHA: otro código, una advertencia nueva o una salida vacía son rojo
FORMATO_EXCEPCION=1 FORMATO_CODIGO=1 gate "$GATE_REAL"
aserta "format con código 1 y sin líneas de error: rojo (puede ser un fallo del propio dotnet format)" test "$RC" -ne 0
aserta "format con código 1: lo dice" contiene "$SALIDA" "puede ser un fallo del propio dotnet format"
FORMATO_EXCEPCION=1 FORMATO_CODIGO=137 gate "$GATE_REAL"
aserta "format con código 137 (OOM): rojo" test "$RC" -ne 0
FORMATO_ADVERTENCIA_NUEVA=1 gate "$GATE_REAL"
aserta "format con código 2 y una advertencia que no es CA2255: rojo" test "$RC" -ne 0
FORMATO_RC2_SIN_SALIDA=1 gate "$GATE_REAL"
aserta "format con código 2 y salida vacía: rojo" test "$RC" -ne 0
FORMATO_CODIGO_1_CON_CA2255=1 gate "$GATE_REAL"
aserta "format con código 1 aunque solo haya CA2255: rojo (la tolerancia es solo para el código 2)" test "$RC" -ne 0
GATE_ADVERTENCIAS_TOLERADAS="CA2255|IDE0005" FORMATO_ADVERTENCIA_NUEVA=1 gate "$GATE_REAL"
aserta "format: ampliar la lista de advertencias toleradas es explícito y se respeta" test "$RC" -eq 0

# 4. un warning es un fallo (-warnaserror)
WARNING_EN_BUILD=1 gate "$GATE_REAL"
aserta "warning: sale distinto de 0" test "$RC" -ne 0
aserta "warning: nombra CS8604" contiene "$SALIDA" "CS8604"
aserta "warning: la etapa se llama compilacion" contiene "$SALIDA" "GATE: compilacion ROJO"

# 5. cero pruebas y sin resumen son rojo aunque dotnet salga con 0
PRUEBAS_ARQUITECTURA=0 gate "$GATE_REAL"
aserta "0 pruebas: sale distinto de 0" test "$RC" -ne 0
aserta "0 pruebas: lo dice con su nombre" contiene "$SALIDA" "0 pruebas ejecutadas"
SIN_RESUMEN=1 gate "$GATE_REAL"
aserta "sin resumen: sale distinto de 0" test "$RC" -ne 0
aserta "sin resumen: dice que no sabe cuántas corrieron" contiene "$SALIDA" "no puedo saber cuántas corrieron"

# 5b. el total suma todos los resúmenes (dos ensamblados o TFM)
RESUMEN_DOBLE=1 gate "$GATE_REAL"
aserta "total: suma 10 + 0 = 10 pruebas, no solo el último resumen" bash -c "grep -q 'arquitectura OK .* — 10 pruebas' <<<\"\$1\"" _ "$SALIDA"
aserta "total: con un resumen de 10 y otro de 0 el gate no se pone rojo" test "$RC" -eq 0

# 6. un test rojo
TESTS_ROJOS="CaeManager.Domain.Tests" gate "$GATE_REAL"
aserta "test rojo: sale distinto de 0" test "$RC" -ne 0
aserta "test rojo: nombra la prueba y la etapa" bash -c "grep -q 'GATE: dominio ROJO' <<<\"\$1\" && grep -q 'Una.Prueba' <<<\"\$1\"" _ "$SALIDA"

# 7. --rapido
gate "$GATE_REAL" --rapido
aserta "rapido: sale con 0" test "$RC" -eq 0
aserta "rapido: solo corre arquitectura" bash -c "grep -c '^test ' '$LOG' | grep -qx 1"
aserta "rapido: no ejecuta dominio" bash -c "! grep -q 'Domain.Tests' '$LOG'"

# 7b. --rapido no se combina con --web ni --integracion (sería un verde sin la etapa pedida)
gate "$GATE_REAL" --rapido --web
aserta "rapido+web: uso incorrecto, código 2" test "$RC" -eq 2
gate "$GATE_REAL" --rapido --integracion
aserta "rapido+integracion: uso incorrecto, código 2" test "$RC" -eq 2
aserta "rapido+integracion: no ejecuta nada" bash -c "test ! -s '$LOG'"

# 8. --todo lista todas las rojas
FORMATO_ROJO=1 WARNING_EN_BUILD=1 gate "$GATE_REAL" --todo
aserta "todo: sale distinto de 0" test "$RC" -ne 0
aserta "todo: las dos etapas rojas aparecen en la línea final" contiene "$SALIDA" "fallaron: formato compilacion"

# 9. sin turno de PostgreSQL
TURNO=0 gate "$GATE_REAL" --integracion
aserta "turno: sale con 75" test "$RC" -eq 75
aserta "turno: dice que la etapa NO se ejecutó" contiene "$SALIDA" "NO se ejecutó"
TURNO=1 gate "$GATE_REAL" --integracion
aserta "turno: con turno corre integración envuelta" grep -q "^test tests/CaeManager.IntegrationTests" "$LOG"

# 10. uso incorrecto
gate "$GATE_REAL" --nada
aserta "uso: opción desconocida sale con 2" test "$RC" -eq 2

# 11. clon sin la base: se asume que toca Web
( cd "$REPO" && git update-ref -d refs/remotes/origin/main )
gate "$GATE_REAL"
aserta "sin base: avisa y ejecuta Web (falla hacia más pruebas)" bash -c "grep -q '^test tests/CaeManager.Web.Tests' '$LOG' && grep -q 'AVISO la base' <<<\"\$1\"" _ "$SALIDA"
( cd "$REPO" && git update-ref refs/remotes/origin/main "$(git rev-parse main)" )

# 12. gancho pre-push: invoca scripts/gate.sh --rapido, bloquea si falla y no actúa al borrar una rama
HOOK_REAL="$AQUI/../.githooks/pre-push"
( cd "$REPO" && git update-ref refs/remotes/origin/main "$(git rev-parse main)" )
SHA="1111111111111111111111111111111111111111"
CERO="0000000000000000000000000000000000000000"
hook() { # hook <guion-del-gancho> <sha-local> → deja el código en $RC y la salida en $SALIDA
  : >"$LOG"
  SALIDA="$(cd "$REPO" && printf 'refs/heads/rama %s refs/heads/rama %s\n' "$2" "$CERO" | bash "$1" 2>&1)"; RC=$?
  if [ -n "${DEPURAR:-}" ]; then echo "--- gancho $1 rc=$RC"; echo "$SALIDA" | sed 's/^/    g| /'; fi
}
if [ -f "$HOOK_REAL" ]; then
  hook "$HOOK_REAL" "$SHA"
  aserta "gancho: con el gate verde deja empujar (0)" test "$RC" -eq 0
  aserta "gancho: ejecutó el gate rápido (arquitectura sí, dominio no)" bash -c "grep -q 'Architecture.Tests' '$LOG' && ! grep -q 'Domain.Tests' '$LOG'"
  FORMATO_ROJO=1 hook "$HOOK_REAL" "$SHA"
  aserta "gancho: con el gate rojo bloquea el push (código distinto de 0)" test "$RC" -ne 0
  aserta "gancho: dice que no empuja" contiene "$SALIDA" "NO se empuja"
  tocar NOTAS.txt
  hook "$HOOK_REAL" "$SHA"
  aserta "gancho: con cambios sin comprometer avisa (el gate valida el árbol, no el commit)" contiene "$SALIDA" "AVISO hay cambios sin comprometer"
  aserta "gancho: el aviso no bloquea el push" test "$RC" -eq 0
  limpiar
  hook "$HOOK_REAL" "$CERO"
  aserta "gancho: borrar una rama remota no ejecuta el gate" bash -c "test ! -s '$LOG'"
  aserta "gancho: borrar una rama remota sale con 0" test "$RC" -eq 0
else
  mal "gancho: no existe $HOOK_REAL"
fi

# ── MUTACIONES: el guion roto debe poner en rojo la aserción que lo vigila ──
echo "mutaciones del propio guion"
mutar() { # mutar <nombre> <sed-expr>  → deja el guion mutado en $TMP_ROOT/mutado.sh y comprueba que mutó
  cp "$GATE_REAL" "$TMP_ROOT/mutado.sh"
  sed -i "$2" "$TMP_ROOT/mutado.sh"
  if cmp -s "$GATE_REAL" "$TMP_ROOT/mutado.sh"; then mal "mutación '$1': el mutador NO mutó (patrón desfasado)"; return 1; fi
  return 0
}

if mutar "quitar --verify-no-changes" 's/ --verify-no-changes//'; then
  FORMATO_ROJO=1 gate "$TMP_ROOT/mutado.sh"
  aserta "mutación sin --verify-no-changes: el gate mutado da VERDE con un fichero mal formateado (la prueba lo detectaría)" test "$RC" -eq 0
fi
if mutar "format: no tolerar las advertencias" 's/if \[ "\$rc" -eq 2 \]/if false/'; then
  FORMATO_SOLO_AVISOS=1 gate "$TMP_ROOT/mutado.sh"
  aserta "mutación que no tolera advertencias: el gate mutado da ROJO con solo advertencias (la prueba lo detectaría)" test "$RC" -ne 0
fi
if mutar "quitar -warnaserror" 's/ -warnaserror//'; then
  WARNING_EN_BUILD=1 gate "$TMP_ROOT/mutado.sh"
  aserta "mutación sin -warnaserror: el gate mutado da VERDE con un warning (la prueba lo detectaría)" test "$RC" -eq 0
fi
if mutar "quitar el control de 0 pruebas" 's/elif \[ "\$total" -eq 0 \]; then/elif false; then/'; then
  PRUEBAS_ARQUITECTURA=0 gate "$TMP_ROOT/mutado.sh"
  aserta "mutación sin control de 0 pruebas: el gate mutado da VERDE con 0 pruebas (la prueba lo detectaría)" test "$RC" -eq 0
fi
if mutar "web ignora los cambios comprometidos" 's/git diff --name-only --no-renames "\$BASE"...HEAD; //'; then
  comprometer src/CaeManager.Application/A.cs
  gate "$TMP_ROOT/mutado.sh"
  aserta "mutación sin el diff comprometido: el gate mutado omite Web con un cambio ya comprometido (la prueba lo detectaría)" bash -c "! grep -q '^test tests/CaeManager.Web.Tests' '$LOG'"
  reiniciar
fi
if mutar "el total solo cuenta el último resumen" 's/s += \$1/s = $1/'; then
  RESUMEN_DOBLE=1 gate "$TMP_ROOT/mutado.sh"
  aserta "mutación del total (último resumen): el gate mutado da ROJO con 10+0 pruebas (la prueba lo detectaría)" test "$RC" -ne 0
fi
if mutar "format: tolerar cualquier código" 's/\[ "\$rc" -eq 2 \] \&\& //'; then
  FORMATO_CODIGO_1_CON_CA2255=1 gate "$TMP_ROOT/mutado.sh"
  aserta "mutación que tolera cualquier código: el gate mutado da VERDE con código 1 y solo CA2255 (la prueba lo detectaría)" test "$RC" -eq 0
fi
if mutar "format: tolerar cualquier advertencia" 's/ \&\& ! grep -Evq "warning (\$toleradas)\[: \]" <<<"\$advertencias"//'; then
  FORMATO_ADVERTENCIA_NUEVA=1 gate "$TMP_ROOT/mutado.sh"
  aserta "mutación que tolera toda advertencia: el gate mutado da VERDE con IDE0005 (la prueba lo detectaría)" test "$RC" -eq 0
fi
if mutar "web siempre omitida" 's/if \[ "\$FORZAR_WEB" -eq 1 \] || diff_toca_codigo; then/if false; then/'; then
  tocar src/CaeManager.Web/W.razor
  gate "$TMP_ROOT/mutado.sh"
  aserta "mutación Web siempre omitida: el gate mutado no ejecuta Web aunque el diff la toque (la prueba lo detectaría)" bash -c "! grep -q '^test tests/CaeManager.Web.Tests' '$LOG'"
  limpiar
fi

if [ -f "$HOOK_REAL" ]; then
  cp "$HOOK_REAL" "$TMP_ROOT/hook-mutado"
  sed -i 's/^exit "\$estado"$/exit 0/' "$TMP_ROOT/hook-mutado"
  if cmp -s "$HOOK_REAL" "$TMP_ROOT/hook-mutado"; then
    mal "mutación del gancho: el mutador NO mutó (patrón desfasado)"
  else
    FORMATO_ROJO=1 hook "$TMP_ROOT/hook-mutado" "$SHA"
    aserta "mutación del gancho que no propaga el fallo: deja empujar con el gate rojo (la prueba lo detectaría)" test "$RC" -eq 0
  fi
  cp "$HOOK_REAL" "$TMP_ROOT/hook-mutado"
  sed -i 's/--rapido/--todo/' "$TMP_ROOT/hook-mutado"
  hook "$TMP_ROOT/hook-mutado" "$SHA"
  aserta "mutación del gancho que ejecuta el gate completo: corre Domain.Tests (la prueba lo detectaría)" grep -q "Domain.Tests" "$LOG"
fi

echo
echo "$PRUEBAS comprobaciones, $FALLOS fallos"
[ "$FALLOS" -eq 0 ]
