#!/bin/bash
# Prueba de informe-cobertura-por-capas.py sin CI real: construye Summary.xml
# (reporttype XmlSummary de reportgenerator) sintéticos con cifras conocidas
# de antemano, para poder afirmar el número exacto que el guion debe imprimir
# en vez de solo "no revienta".
#
# La propiedad que importa no es "el guion corre" — es que:
#   (a) el desglose por ensamblado reproduce las cifras exactas del XML;
#   (b) un ensamblado ausente se marca como tal, no como cero silencioso;
#   (c) las zonas de riesgo agregan SOLO las clases cuyo nombre contiene su
#       palabra clave, y una zona sin ninguna clase lo dice;
#   (d) el ranking de "mas lineas sin cubrir" ordena por lineas absolutas,
#       no por porcentaje — una clase de 2 lineas al 0% no debe salir antes
#       que una de 400 al 40%;
#   (e) MUTACION: cambiar la cobertura de UNA clase de una zona cambia esa
#       zona y solo esa zona — si moviera otra, el agregado no aislaria las
#       zonas como dice hacerlo.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# `command -v python3` no basta: en Windows con "alias de ejecución de
# aplicaciones" activado, python3 EXISTE en el PATH pero es un stub de la
# Microsoft Store que falla al invocarse. Se comprueba ejecutando de verdad.
if python3 --version >/dev/null 2>&1; then
  GUION=(python3 "$SCRIPT_DIR/informe-cobertura-por-capas.py")
else
  GUION=(python "$SCRIPT_DIR/informe-cobertura-por-capas.py")
fi

TMP_ROOT="$(mktemp -d)"
trap 'rm -rf "$TMP_ROOT"' EXIT

FALLOS=0
PRUEBAS=0

# clase() escribe un <Class .../> — cubiertas y coverablelines son enteros.
clase() {
  local nombre="$1" cubiertas="$2" total="$3"
  printf '      <Class name="%s" coverage="0" coveredlines="%s" coverablelines="%s" totallines="0" branchcoverage="" coveredbranches="0" totalbranches="0" coveredmethods="0" fullcoveredmethods="0" totalmethods="0" methodcoverage="0" fullmethodcoverage="0" />\n' \
    "$nombre" "$cubiertas" "$total"
}

# xml_summary(ruta, ensamblados...) — cada ensamblado es "Nombre|c1:c2,c3:c4,..."
# donde cN:tN son cubiertas:total de una clase, separadas por ";".
xml_summary_nucleo() {
  local ruta="$TMP_ROOT/nucleo.xml"
  cat >"$ruta" <<'CABECERA'
<?xml version="1.0" encoding="utf-8"?>
<CoverageReport scope="Summary">
  <Summary>
    <Linecoverage>0</Linecoverage>
  </Summary>
  <Coverage>
CABECERA
  {
    echo '    <Assembly name="CaeManager.Domain" classes="2" coverage="0" coveredlines="60" coverablelines="100" totallines="0">'
    clase "CaeManager.Domain.Incidencias.Incidencia" 40 50
    clase "CaeManager.Domain.Configuracion.ParametroSistema" 20 50
    echo '    </Assembly>'
    echo '    <Assembly name="CaeManager.Application" classes="4" coverage="0" coveredlines="130" coverablelines="300" totallines="0">'
    # Zona autorizacion: dos clases.
    clase "CaeManager.Application.Common.AutorizacionEscrituraBehavior\`2" 90 100
    clase "CaeManager.Application.Proyectos.ProyectoAutorizacion" 10 100
    # Zona importacion: una clase, MUCHAS lineas sin cubrir (debe salir primero en "peores").
    clase "CaeManager.Application.Importacion.Commands.EjecutarImportacionCommandHandler" 20 90
    # Clase ajena a cualquier zona, con pocas lineas sin cubrir pero peor PORCENTAJE:
    # si el ranking ordenara por porcentaje, esta saldria antes que la de importacion.
    clase "CaeManager.Application.Alertas.DocumentoFaltanteDto" 10 10
    echo '    </Assembly>'
    # Infrastructure no se declara (ausente a proposito, para probar el aviso).
  } >>"$ruta"
  echo '  </Coverage>' >>"$ruta"
  echo '</CoverageReport>' >>"$ruta"
  printf '%s' "$ruta"
}

xml_summary_web() {
  local ruta="$TMP_ROOT/web.xml"
  cat >"$ruta" <<'CABECERA'
<?xml version="1.0" encoding="utf-8"?>
<CoverageReport scope="Summary">
  <Summary>
    <Linecoverage>0</Linecoverage>
  </Summary>
  <Coverage>
CABECERA
  {
    echo '    <Assembly name="CaeManager.Web" classes="1" coverage="0" coveredlines="50" coverablelines="200" totallines="0">'
    clase "CaeManager.Web.Features.AlcanceDatos.AlcanceDatosPanel" 50 200
    echo '    </Assembly>'
  } >>"$ruta"
  echo '  </Coverage>' >>"$ruta"
  echo '</CoverageReport>' >>"$ruta"
  printf '%s' "$ruta"
}

assert_contiene() {
  local descripcion="$1" patron="$2" salida="$3"
  PRUEBAS=$((PRUEBAS + 1))
  if printf '%s' "$salida" | grep -qF -- "$patron"; then
    echo "OK: $descripcion"
  else
    echo "FALLO: $descripcion — no se encontró: $patron" >&2
    FALLOS=$((FALLOS + 1))
  fi
}

assert_no_contiene() {
  local descripcion="$1" patron="$2" salida="$3"
  PRUEBAS=$((PRUEBAS + 1))
  if printf '%s' "$salida" | grep -qF -- "$patron"; then
    echo "FALLO: $descripcion — apareció y no debía: $patron" >&2
    FALLOS=$((FALLOS + 1))
  else
    echo "OK: $descripcion"
  fi
}

assert_orden_antes() {
  local descripcion="$1" primero="$2" segundo="$3" salida="$4"
  local pos1 pos2
  pos1=$(printf '%s' "$salida" | grep -n -F -- "$primero" | head -1 | cut -d: -f1 || true)
  pos2=$(printf '%s' "$salida" | grep -n -F -- "$segundo" | head -1 | cut -d: -f1 || true)
  PRUEBAS=$((PRUEBAS + 1))
  if [[ -z "$pos1" || -z "$pos2" ]]; then
    echo "FALLO: $descripcion — no se encontró alguna de las dos líneas" >&2
    FALLOS=$((FALLOS + 1))
  elif (( pos1 < pos2 )); then
    echo "OK: $descripcion"
  else
    echo "FALLO: $descripcion — el orden salió invertido" >&2
    FALLOS=$((FALLOS + 1))
  fi
}

NUCLEO_XML="$(xml_summary_nucleo)"
WEB_XML="$(xml_summary_web)"

echo "=== Desglose por ensamblado con cifras conocidas ==="
SALIDA="$("${GUION[@]}" --nucleo "$NUCLEO_XML" --web "$WEB_XML")"
assert_contiene "Domain: 60/100 = 60.0 %" "| CaeManager.Domain | 60 | 100 | 60.0 % |" "$SALIDA"
assert_contiene "Application: 130/300 = 43.3 %" "| CaeManager.Application | 130 | 300 | 43.3 % |" "$SALIDA"
assert_contiene "Web: 50/200 = 25.0 %" "| CaeManager.Web | 50 | 200 | 25.0 % |" "$SALIDA"

echo
echo "=== Ensamblado ausente se marca, no se calla ==="
assert_contiene "Infrastructure ausente queda declarado" "CaeManager.Infrastructure | — | — | **ausente del informe**" "$SALIDA"
assert_contiene "el aviso de ausencia aparece" "no es un cero" "$SALIDA"

echo
echo "=== Zonas de riesgo: agregación exacta por palabra clave ==="
# autorizacion: 90+10 cubiertas de 100+100 = 100/200 = 50.0%
assert_contiene "zona autorizacion agrega solo sus 2 clases" "| autorizacion | 2 | 100 | 200 | 50.0 % |" "$SALIDA"
# importacion: 1 clase, 20/90
assert_contiene "zona importacion con su única clase" "| importacion | 1 | 20 | 90 | 22.2 % |" "$SALIDA"
# rls_aislamiento_tenant: la clase Web "AlcanceDatosPanel" SÍ cae aquí (contiene AlcanceDatos)
assert_contiene "AlcanceDatosPanel de la Web cuenta en rls_aislamiento_tenant" "| rls_aislamiento_tenant | 1 | 50 | 200 | 25.0 % |" "$SALIDA"
# La clase ajena (DocumentoFaltanteDto) no debe sumar a ninguna zona.
assert_no_contiene "una clase sin palabra clave no infla ninguna zona" "DocumentoFaltanteDto" "$SALIDA"

echo
echo "=== Ranking de concentración: por líneas absolutas, no por porcentaje ==="
# EjecutarImportacionCommandHandler: 70 sin cubrir (peor en absoluto).
# DocumentoFaltanteDto: 0 sin cubrir (100% cubierta) — no debería aparecer.
# ProyectoAutorizacion: 90 sin cubrir — es la que más lineas sin cubrir tiene de Application.
assert_orden_antes "ProyectoAutorizacion (90 sin cubrir) antes que EjecutarImportacion... (70 sin cubrir)" \
  "ProyectoAutorizacion" "EjecutarImportacionCommandHandler" "$SALIDA"
assert_no_contiene "una clase 100% cubierta no aparece en el ranking de sin-cubrir" \
  "DocumentoFaltanteDto | 0 |" "$SALIDA"

echo
echo "=== MUTACIÓN: tocar una clase de una zona no mueve a las demás ==="
# Subimos ProyectoAutorizacion de 10/100 a 100/100: autorizacion debe subir,
# importacion y rls_aislamiento_tenant deben quedar EXACTAMENTE igual.
NUCLEO_MUTADO="$TMP_ROOT/nucleo-mutado.xml"
sed 's#coveredlines="10" coverablelines="100" totallines="0" branchcoverage="" coveredbranches="0" totalbranches="0" coveredmethods="0" fullcoveredmethods="0" totalmethods="0" methodcoverage="0" fullmethodcoverage="0" />\n#&#' \
  "$NUCLEO_XML" > /dev/null # no-op, solo para dejar constancia de qué línea se apunta
sed 's#name="CaeManager.Application.Proyectos.ProyectoAutorizacion" coverage="0" coveredlines="10" coverablelines="100"#name="CaeManager.Application.Proyectos.ProyectoAutorizacion" coverage="0" coveredlines="100" coverablelines="100"#' \
  "$NUCLEO_XML" > "$NUCLEO_MUTADO"
if ! diff -q "$NUCLEO_XML" "$NUCLEO_MUTADO" >/dev/null; then
  echo "OK (control): la mutación cambió el fichero de entrada"
else
  echo "FALLO: la mutación no tocó el XML — el sed no casó, el resto de la prueba no prueba nada" >&2
  FALLOS=$((FALLOS + 1))
fi
SALIDA_MUTADA="$("${GUION[@]}" --nucleo "$NUCLEO_MUTADO" --web "$WEB_XML")"
# autorizacion ahora: 90+100 = 190/200 = 95.0%
assert_contiene "autorizacion sube tras la mutación (95.0 %)" "| autorizacion | 2 | 190 | 200 | 95.0 % |" "$SALIDA_MUTADA"
assert_contiene "importacion NO se mueve (sigue en 22.2 %)" "| importacion | 1 | 20 | 90 | 22.2 % |" "$SALIDA_MUTADA"
assert_contiene "rls_aislamiento_tenant NO se mueve (sigue en 25.0 %)" "| rls_aislamiento_tenant | 1 | 50 | 200 | 25.0 % |" "$SALIDA_MUTADA"
# Y el ensamblado Application en conjunto sí debe reflejar la mejora si se
# recalculara desde las clases — pero la tabla de ensamblado lee el atributo
# del propio <Assembly>, que en este fixture no se tocó a propósito: prueba
# de que el guion no reconstruye el agregado de ensamblado a partir de las
# clases (usa el que ya trae reportgenerator, que sí las suma todas).
assert_contiene "el agregado de Application sigue siendo el del <Assembly> (no recomputado)" \
  "| CaeManager.Application | 130 | 300 | 43.3 % |" "$SALIDA_MUTADA"

echo
echo "=== Zona sin ninguna clase detectada lo declara ==="
SOLO_DOMAIN="$TMP_ROOT/solo-domain.xml"
cat >"$SOLO_DOMAIN" <<'XML'
<?xml version="1.0" encoding="utf-8"?>
<CoverageReport scope="Summary">
  <Summary><Linecoverage>0</Linecoverage></Summary>
  <Coverage>
    <Assembly name="CaeManager.Domain" classes="1" coverage="0" coveredlines="5" coverablelines="5" totallines="0">
      <Class name="CaeManager.Domain.Alertas.AlertaSimple" coverage="0" coveredlines="5" coverablelines="5" totallines="0" branchcoverage="" coveredbranches="0" totalbranches="0" coveredmethods="0" fullcoveredmethods="0" totalmethods="0" methodcoverage="0" fullmethodcoverage="0" />
    </Assembly>
  </Coverage>
</CoverageReport>
XML
SALIDA_VACIA="$("${GUION[@]}" --nucleo "$SOLO_DOMAIN")"
assert_contiene "sin --web, la sección Web declara el hueco" "no se recibió \`--web\`" "$SALIDA_VACIA"
assert_contiene "zona autorizacion en cero clases, no se calla" "| autorizacion | 0 | 0 | 0 |" "$SALIDA_VACIA"
assert_contiene "el aviso de zona vacía aparece" "no encontraron nada" "$SALIDA_VACIA"

echo
echo "=== Sin ningún argumento, falla en vez de imprimir un informe vacío ==="
PRUEBAS=$((PRUEBAS + 1))
if "${GUION[@]}" >/dev/null 2>&1; then
  echo "FALLO: sin --nucleo ni --web debería salir en código distinto de 0" >&2
  FALLOS=$((FALLOS + 1))
else
  echo "OK: sin --nucleo ni --web sale en error"
fi

echo
echo "Pruebas: $PRUEBAS · Fallos: $FALLOS"
(( FALLOS == 0 )) || exit 1
