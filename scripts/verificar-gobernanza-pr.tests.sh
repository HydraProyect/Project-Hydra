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
echo "Pruebas: $PRUEBAS · Fallos: $FALLOS"
(( FALLOS == 0 )) || exit 1
