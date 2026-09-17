#!/bin/bash
# Prueba de verificar-gobernanza-pr.sh sin red: cuerpo y lista de ficheros
# son ficheros locales sintéticos, no una PR real.
#
# La propiedad que importa no es "el guion se ejecuta" — es que HABRÍA
# CAZADO el incidente real que lo motiva (PROTOCOLO-TURNO-NOCTURNO.md § 8,
# registro 2026-09-17): #674 añadió cae_app_aprovisionamiento a
# roles-de-cluster.sql sin paso operativo declarado -> staging roto (42704).
#
# Corre en gobernanza-pr.yml (job gobernanza-pr-tests) — NO en ci.yml: el
# alcance de esta misión (T3) prohíbe tocar ci.yml, y el propio workflow que
# usa este guion es el sitio más cercano al patrón existente
# (deploy-script-tests) sin cruzar esa frontera.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
VERIFICADOR=(bash "$SCRIPT_DIR/verificar-gobernanza-pr.sh")

TMP_ROOT="$(mktemp -d)"
trap 'rm -rf "$TMP_ROOT"' EXIT

FALLOS=0
PRUEBAS=0

cuerpo() {
  local ruta="$TMP_ROOT/cuerpo-$RANDOM.md"
  printf '%s\n' "$@" >"$ruta"
  printf '%s' "$ruta"
}

ficheros() {
  local ruta="$TMP_ROOT/ficheros-$RANDOM.txt"
  printf '%s\n' "$@" >"$ruta"
  printf '%s' "$ruta"
}

ejecutar() {
  local cuerpo="$1" ficheros="$2" salida codigo
  set +e
  salida="$("${VERIFICADOR[@]}" "$cuerpo" "$ficheros" 2>&1)"
  codigo=$?
  set -e
  printf '%s|%s' "$codigo" "$salida"
}

assert_codigo() {
  local descripcion="$1" esperado="$2" cuerpo="$3" ficheros="$4"
  local resultado codigo
  resultado="$(ejecutar "$cuerpo" "$ficheros")"
  codigo="${resultado%%|*}"
  PRUEBAS=$((PRUEBAS + 1))
  if [[ "$codigo" != "$esperado" ]]; then
    echo "FALLO: $descripcion — código esperado $esperado, obtenido $codigo" >&2
    echo "--- salida ---" >&2
    printf '%s\n' "${resultado#*|}" >&2
    FALLOS=$((FALLOS + 1))
  else
    echo "OK: $descripcion"
  fi
}

assert_menciona() {
  local descripcion="$1" patron="$2" cuerpo="$3" ficheros="$4"
  local resultado
  resultado="$(ejecutar "$cuerpo" "$ficheros")"
  PRUEBAS=$((PRUEBAS + 1))
  if printf '%s' "${resultado#*|}" | grep -qi -- "$patron"; then
    echo "OK: $descripcion"
  else
    echo "FALLO: $descripcion — la salida no menciona '$patron'" >&2
    printf '%s\n' "${resultado#*|}" >&2
    FALLOS=$((FALLOS + 1))
  fi
}

echo "=== Paso operativo de roles de clúster ==="

# #674 real: toca roles-de-cluster.sql, sin sección -> debe fallar.
assert_codigo "toca roles-de-cluster.sql sin sección: falla" 1 \
  "$(cuerpo "## Resumen" "Añade un rol nuevo.")" \
  "$(ficheros "deploy/bootstrap/roles-de-cluster.sql" "src/Foo.cs")"
assert_menciona "y lo explica citando la sección esperada" "Paso operativo en servidores" \
  "$(cuerpo "## Resumen" "Añade un rol nuevo.")" \
  "$(ficheros "deploy/bootstrap/roles-de-cluster.sql")"

# Con la sección pero sin nombrar producción: sigue fallando.
assert_codigo "sección presente pero solo nombra staging: falla" 1 \
  "$(cuerpo "## Paso operativo en servidores" "Aplicar en staging con psql.")" \
  "$(ficheros "deploy/bootstrap/roles-de-cluster.sql")"

# Con la sección pero sin nombrar staging: sigue fallando.
assert_codigo "sección presente pero solo nombra producción: falla" 1 \
  "$(cuerpo "## Paso operativo en servidores" "Aplicar en producción con psql.")" \
  "$(ficheros "deploy/bootstrap/roles-de-cluster.sql")"

# Sección vacía (título sin ningún contenido antes del siguiente '## '): falla.
assert_codigo "sección presente pero vacía: falla" 1 \
  "$(cuerpo "## Paso operativo en servidores" "" "## Otra sección" "texto")" \
  "$(ficheros "deploy/bootstrap/roles-de-cluster.sql")"

# Con la sección completa: pasa.
assert_codigo "sección completa (staging y producción): pasa" 0 \
  "$(cuerpo "## Resumen" "cambio" "" \
            "## Paso operativo en servidores" \
            "Ejecutar el GRANT en staging y en producción antes de desplegar.")" \
  "$(ficheros "deploy/bootstrap/roles-de-cluster.sql" "deploy/bootstrap/otro.sql")"

# Mención de producción sin tilde también cuenta ("producci" cubre ambas).
assert_codigo "'produccion' sin tilde también cuenta" 0 \
  "$(cuerpo "## Paso operativo en servidores" "Aplicar en staging y produccion.")" \
  "$(ficheros "deploy/bootstrap/roles-de-cluster.sql")"

# Si la PR no toca el fichero, la regla no aplica aunque falte la sección.
assert_codigo "no toca roles-de-cluster.sql: la regla no aplica" 0 \
  "$(cuerpo "## Resumen" "Cambio cualquiera.")" \
  "$(ficheros "src/Foo.cs" "tests/FooTests.cs")"

# Tocar un fichero con nombre parecido pero distinto no dispara la regla —
# la comparación es de ruta exacta, no de substring.
assert_codigo "fichero de nombre parecido no dispara la regla" 0 \
  "$(cuerpo "## Resumen" "Cambio cualquiera.")" \
  "$(ficheros "deploy/bootstrap/roles-de-cluster-otro.sql")"

echo
echo "=== Hallazgos de Codex (PR1): rename, límite de paginación, tabulador ==="

# Rename dentro de la misma PR: el fichero vigilado sale por
# previous_filename, no por filename (así lo emite el jq de gobernanza-pr.yml:
# '.[] | .filename, (.previous_filename // empty)'). Sin mirar
# previous_filename, esto escapaba la regla en silencio.
assert_codigo "rename de roles-de-cluster.sql (visto por previous_filename) sí dispara" 1 \
  "$(cuerpo "## Resumen" "Renombra el fichero de roles.")" \
  "$(ficheros "deploy/bootstrap/roles-de-cluster-2027.sql" "deploy/bootstrap/roles-de-cluster.sql")"

# Límite de paginación de la API de GitHub (3000 ficheros): con ese volumen
# no se puede confirmar si toca o no el fichero vigilado — falla cerrado en
# vez de dar un OK que el instrumento no puede respaldar.
generar_muchos_ficheros() {
  local n="$1" ruta="$TMP_ROOT/muchos-$RANDOM.txt"
  for ((i = 0; i < n; i++)); do echo "src/Archivo$i.cs"; done >"$ruta"
  printf '%s' "$ruta"
}
assert_codigo "3000 ficheros o más: falla cerrado aunque no se vea el fichero vigilado" 1 \
  "$(cuerpo "## Resumen" "PR enorme.")" \
  "$(generar_muchos_ficheros 3000)"
assert_menciona "y explica el motivo (límite de paginación)" "trunca" \
  "$(cuerpo "## Resumen" "PR enorme.")" \
  "$(generar_muchos_ficheros 3000)"
assert_codigo "2999 ficheros: no dispara el límite (queda por debajo)" 0 \
  "$(cuerpo "## Resumen" "PR grande pero no al límite.")" \
  "$(generar_muchos_ficheros 2999)"

# Encabezado "##\tOtra sección" (tabulador, válido en CommonMark) debe cerrar
# la sección igual que "## Otra sección" — si no, una sección objetivo vacía
# hereda en silencio el contenido de la siguiente.
SECCION_CON_TAB="$TMP_ROOT/cuerpo-tab.md"
printf '## Paso operativo en servidores\n##\tOtra sección\nAplicar en staging y en producción.\n' \
  >"$SECCION_CON_TAB"
assert_codigo "'##<TAB>Otra sección' cierra la sección objetivo (queda vacía): falla" 1 \
  "$SECCION_CON_TAB" \
  "$(ficheros "deploy/bootstrap/roles-de-cluster.sql")"

echo
echo "Pruebas: $PRUEBAS · Fallos: $FALLOS"
(( FALLOS == 0 )) || exit 1
