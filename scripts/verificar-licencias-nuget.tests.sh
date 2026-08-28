#!/bin/bash
# Prueba de verificar-licencias-nuget.sh sin red: los .nuspec son ficheros
# locales servidos por NUSPEC_DIR.
#
# La propiedad que importa no es "el guion se ejecuta" — es que HABRÍA CAZADO
# los dos cambios de licencia reales de agosto de 2026, que llegaron como PRs
# de dependabot indistinguibles de una actualización rutinaria:
#
#   FluentAssertions 7.2.2 -> 8.0.0   (Apache-2.0 -> licencia comercial Xceed)
#   MediatR          12.5.0 -> 13.0.0 (Apache-2.0 -> RPL-1.5 o pago)
#
# Los .nuspec de abajo reproducen la forma real de esos paquetes: en ambos, la
# versión libre declara `type="expression"` y la comercial `type="file"`.
#
# Corre en CI (ver .github/workflows/ci.yml, job licencias-dependencias).
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# Invocado como `bash <script>` para no depender de que el bit ejecutable
# sobreviva al checkout — se ha visto perder en Windows.
VERIFICADOR=(bash "$SCRIPT_DIR/verificar-licencias-nuget.sh")

TMP_ROOT="$(mktemp -d)"
trap 'rm -rf "$TMP_ROOT"' EXIT

NUSPECS="$TMP_ROOT/nuspecs"
mkdir -p "$NUSPECS"
export NUSPEC_DIR="$NUSPECS"

FALLOS=0
PRUEBAS=0

nuspec_expresion() {
  local ruta="$1" id="$2" version="$3" expr="$4" aceptacion="${5:-false}"
  cat >"$ruta" <<XML
<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
  <metadata>
    <id>$id</id>
    <version>$version</version>
    <requireLicenseAcceptance>$aceptacion</requireLicenseAcceptance>
    <license type="expression">$expr</license>
  </metadata>
</package>
XML
}

nuspec_fichero() {
  local ruta="$1" id="$2" version="$3" fichero="$4" aceptacion="${5:-true}"
  cat >"$ruta" <<XML
<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
  <metadata>
    <id>$id</id>
    <version>$version</version>
    <requireLicenseAcceptance>$aceptacion</requireLicenseAcceptance>
    <license type="file">$fichero</license>
  </metadata>
</package>
XML
}

nuspec_sin_licencia() {
  local ruta="$1" id="$2" version="$3"
  cat >"$ruta" <<XML
<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
  <metadata>
    <id>$id</id>
    <version>$version</version>
    <licenseUrl>https://example.invalid/licencia</licenseUrl>
  </metadata>
</package>
XML
}

# --- Los cuatro paquetes reales, en sus dos versiones ---
nuspec_expresion "$NUSPECS/mediatr.12.5.0.nuspec"          MediatR          12.5.0 "Apache-2.0"
nuspec_fichero   "$NUSPECS/mediatr.13.0.0.nuspec"          MediatR          13.0.0 "LICENSE.md"
nuspec_expresion "$NUSPECS/fluentassertions.7.2.2.nuspec"  FluentAssertions 7.2.2  "Apache-2.0"
nuspec_fichero   "$NUSPECS/fluentassertions.8.0.0.nuspec"  FluentAssertions 8.0.0  "LICENSE" false

# --- Casos sintéticos ---
nuspec_expresion    "$NUSPECS/serilog.4.4.0.nuspec"    Serilog  4.4.0 "MIT"
nuspec_expresion    "$NUSPECS/copyleft.1.0.0.nuspec"   Copyleft 1.0.0 "GPL-3.0-only"
nuspec_expresion    "$NUSPECS/conterminos.1.0.0.nuspec" ConTerminos 1.0.0 "MIT" true
nuspec_expresion    "$NUSPECS/conpostgres.1.0.0.nuspec" ConPostgres 1.0.0 "PostgreSQL"
nuspec_sin_licencia "$NUSPECS/anonima.1.0.0.nuspec"    Anonima  1.0.0
nuspec_fichero      "$NUSPECS/confichero.1.0.0.nuspec" ConFichero 1.0.0 "LICENSE" false
nuspec_fichero      "$NUSPECS/confichero.2.0.0.nuspec" ConFichero 2.0.0 "LICENSE" false

lista() {
  local ruta="$TMP_ROOT/lista.txt"
  printf '%s\n' "$@" >"$ruta"
  printf '%s' "$ruta"
}

# Ejecuta el verificador y devuelve "codigo|salida".
ejecutar() {
  local ruta_lista="$1" salida codigo
  set +e
  salida="$("${VERIFICADOR[@]}" "$ruta_lista" 2>&1)"
  codigo=$?
  set -e
  printf '%s|%s' "$codigo" "$salida"
}

assert_codigo() {
  local descripcion="$1" esperado="$2" ruta_lista="$3"
  local resultado codigo
  resultado="$(ejecutar "$ruta_lista")"
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
  local descripcion="$1" patron="$2" ruta_lista="$3"
  local resultado
  resultado="$(ejecutar "$ruta_lista")"
  PRUEBAS=$((PRUEBAS + 1))
  if printf '%s' "${resultado#*|}" | grep -qi -- "$patron"; then
    echo "OK: $descripcion"
  else
    echo "FALLO: $descripcion — la salida no menciona '$patron'" >&2
    printf '%s\n' "${resultado#*|}" >&2
    FALLOS=$((FALLOS + 1))
  fi
}

echo "=== Las versiones libres pasan ==="
assert_codigo "MediatR 12.5.0 (Apache-2.0) pasa" 0 "$(lista "MediatR 12.5.0")"
assert_codigo "FluentAssertions 7.2.2 (Apache-2.0) pasa" 0 "$(lista "FluentAssertions 7.2.2")"
assert_codigo "conjunto libre completo pasa" 0 \
  "$(lista "MediatR 12.5.0" "FluentAssertions 7.2.2" "Serilog 4.4.0")"

echo
echo "=== Los dos cambios de licencia REALES se detectan ==="
assert_codigo   "MediatR 13.0.0 se detecta" 1 "$(lista "MediatR 13.0.0")"
assert_menciona "MediatR 13.0.0 se explica como licencia en fichero" "fichero" \
  "$(lista "MediatR 13.0.0")"
assert_codigo   "FluentAssertions 8.0.0 se detecta" 1 "$(lista "FluentAssertions 8.0.0")"
assert_menciona "FluentAssertions 8.0.0 se explica como licencia en fichero" "fichero" \
  "$(lista "FluentAssertions 8.0.0")"

echo
echo "=== Un solo paquete problemático tumba el conjunto ==="
assert_codigo "un paquete de pago entre varios libres se detecta" 1 \
  "$(lista "Serilog 4.4.0" "MediatR 13.0.0" "FluentAssertions 7.2.2")"

echo
echo "=== Otras señales ==="
assert_codigo   "licencia copyleft no permitida se detecta" 1 "$(lista "Copyleft 1.0.0")"
assert_menciona "copyleft se explica como no permitida" "no permitida" "$(lista "Copyleft 1.0.0")"
assert_codigo   "paquete sin <license> se detecta" 1 "$(lista "Anonima 1.0.0")"
assert_codigo   "paquete inexistente se detecta en vez de pasar en silencio" 1 \
  "$(lista "NoExiste 9.9.9")"

# requireLicenseAcceptance NO bloquea, aunque MediatR 13.0.0 lo activara. Al
# calibrar contra las 50 dependencias reales resultó que lo ponen a true 13
# paquetes de Microsoft y 5 de OpenTelemetry, todos con MIT o Apache-2.0: como
# criterio de fallo daba 17 falsos positivos de 22. Se informa, no bloquea.
assert_codigo   "MIT exigiendo aceptar términos NO bloquea" 0 "$(lista "ConTerminos 1.0.0")"
assert_menciona "pero sí se informa de que exige aceptar términos" "aceptar términos" \
  "$(lista "ConTerminos 1.0.0")"

# La licencia PostgreSQL (la de Npgsql) es permisiva y está en la lista.
assert_codigo "licencia PostgreSQL se acepta" 0 "$(lista "ConPostgres 1.0.0")"

echo
echo "=== Excepciones revisadas por una persona ==="
# La propiedad crítica: una excepción vale para la VERSIÓN EXACTA revisada y
# para ninguna otra. Sin esto, exceptuar un paquete una vez lo dejaría exento
# para siempre — incluida la versión futura que cambie a licencia de pago, que
# es justo lo que este guion existe para detectar.
EXCEPCIONES_PRUEBA="$TMP_ROOT/excepciones.txt"
cat >"$EXCEPCIONES_PRUEBA" <<'TXT'
# comentario que debe ignorarse
ConFichero 1.0.0   # revisado: Apache-2.0
TXT
export EXCEPCIONES="$EXCEPCIONES_PRUEBA"

assert_codigo   "la versión revisada pasa" 0 "$(lista "ConFichero 1.0.0")"
assert_menciona "y se marca como revisada, no como OK a secas" "REVISADO" \
  "$(lista "ConFichero 1.0.0")"
assert_codigo   "una versión NO revisada del mismo paquete sigue bloqueando" 1 \
  "$(lista "ConFichero 2.0.0")"

# Y la excepción no debe abrir la mano a otros paquetes con licencia en fichero.
assert_codigo "exceptuar un paquete no exceptúa a MediatR 13.0.0" 1 \
  "$(lista "ConFichero 1.0.0" "MediatR 13.0.0")"

unset EXCEPCIONES

echo
echo "=== El instrumento distingue versión, no solo nombre ==="
# Sin esto, un guion que solo mirase el nombre del paquete pasaría igual.
assert_codigo "el mismo paquete pasa en 12.5.0 y falla en 13.0.0 (a)" 0 "$(lista "MediatR 12.5.0")"
assert_codigo "el mismo paquete pasa en 12.5.0 y falla en 13.0.0 (b)" 1 "$(lista "MediatR 13.0.0")"

echo
echo "Pruebas: $PRUEBAS · Fallos: $FALLOS"
(( FALLOS == 0 )) || exit 1
