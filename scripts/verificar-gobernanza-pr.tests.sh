#!/bin/bash
# Prueba de verificar-gobernanza-pr.sh sin red: cuerpo, lista de ficheros y
# autor son datos locales sintéticos, no una PR real.
#
# La propiedad que importa no es "el guion se ejecuta" — es que HABRÍA
# CAZADO los incidentes reales que lo motivan (PROTOCOLO-TURNO-NOCTURNO.md
# § 8, registros 2026-09-17):
#
#   #674  añadió cae_app_aprovisionamiento a roles-de-cluster.sql sin paso
#         operativo declarado -> staging roto (42704).
#   #673  dejó "2ª pasada — (resultado abajo)" sin rellenar en el cuerpo.
#   #678  se abrió sin sección de revisión Codex.
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

# Autor por defecto de las pruebas centradas en la regla 1 (paso operativo):
# dependabot[bot] exime de la regla 2 (revisión Codex), así que estas pruebas
# quedan aisladas de esa regla y no tienen que llevar su sección. Las
# pruebas de la regla 2, y las de combinación, pasan su propio autor.
AUTOR_AISLA_REGLA1="dependabot[bot]"

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
  local cuerpo="$1" ficheros="$2" autor="${3-$AUTOR_AISLA_REGLA1}" salida codigo
  set +e
  salida="$("${VERIFICADOR[@]}" "$cuerpo" "$ficheros" "$autor" 2>&1)"
  codigo=$?
  set -e
  printf '%s|%s' "$codigo" "$salida"
}

assert_codigo() {
  local descripcion="$1" esperado="$2" cuerpo="$3" ficheros="$4" autor="${5-$AUTOR_AISLA_REGLA1}"
  local resultado codigo
  resultado="$(ejecutar "$cuerpo" "$ficheros" "$autor")"
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
  local descripcion="$1" patron="$2" cuerpo="$3" ficheros="$4" autor="${5-$AUTOR_AISLA_REGLA1}"
  local resultado
  resultado="$(ejecutar "$cuerpo" "$ficheros" "$autor")"
  PRUEBAS=$((PRUEBAS + 1))
  if printf '%s' "${resultado#*|}" | grep -qi -- "$patron"; then
    echo "OK: $descripcion"
  else
    echo "FALLO: $descripcion — la salida no menciona '$patron'" >&2
    printf '%s\n' "${resultado#*|}" >&2
    FALLOS=$((FALLOS + 1))
  fi
}

echo "=== Regla 1: paso operativo de roles de clúster (aislada de la regla 2) ==="

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
echo "=== Regla 2: revisión Codex en el cuerpo (ficheros que no tocan roles-de-cluster.sql) ==="

FICHEROS_NEUTROS="$(ficheros "src/Foo.cs")"

# #678 real: PR sin sección de revisión Codex -> falla.
assert_codigo "sin sección de revisión Codex: falla" 1 \
  "$(cuerpo "## Resumen" "Cambio cualquiera.")" \
  "$FICHEROS_NEUTROS" \
  ""

# #673 real: marcador provisional sin rellenar -> falla.
assert_codigo "marcador 'resultado abajo' sin rellenar: falla" 1 \
  "$(cuerpo "## Revisión Codex" "2ª pasada — (resultado abajo)")" \
  "$FICHEROS_NEUTROS" \
  ""
assert_menciona "y el motivo cita el marcador" "resultado abajo" \
  "$(cuerpo "## Revisión Codex" "2ª pasada — (resultado abajo)")" \
  "$FICHEROS_NEUTROS" \
  ""

# "resultado    abajo" con varios espacios también cuenta como el mismo
# marcador — no es una forma de burlar la regla con espaciado distinto.
assert_codigo "'resultado' y 'abajo' separados por varios espacios: sigue fallando" 1 \
  "$(cuerpo "## Revisión Codex" "resultado    abajo")" \
  "$FICHEROS_NEUTROS" \
  ""

assert_codigo "marcador 'pendiente': falla" 1 \
  "$(cuerpo "## Revisión Codex" "pendiente de ejecutar")" \
  "$FICHEROS_NEUTROS" \
  ""

assert_codigo "marcador 'TODO': falla" 1 \
  "$(cuerpo "## Revisión Codex" "TODO: pedirle a Codex que revise esto")" \
  "$FICHEROS_NEUTROS" \
  ""

# "todo" en minúsculas es español normal, no marcador — no debe dar falso
# positivo. Tampoco "independiente" por contener "pendiente" como substring.
assert_codigo "'todo' en minúsculas (español normal) no es un marcador" 0 \
  "$(cuerpo "## Revisión Codex" "Codex revisó todo el diff, sin hallazgos.")" \
  "$FICHEROS_NEUTROS" \
  ""
assert_codigo "'independiente' no dispara el marcador 'pendiente'" 0 \
  "$(cuerpo "## Revisión Codex" "El hallazgo es independiente del diseño actual; sin acción.")" \
  "$FICHEROS_NEUTROS" \
  ""

# Sección presente y con contenido real: pasa, con hallazgos aceptados y
# rechazados.
assert_codigo "hallazgos aceptados y rechazados: pasa" 0 \
  "$(cuerpo "## Revisión Codex" "" \
            "- Aceptado: falta validar el tenant objetivo en el comando." \
            "- Rechazado: el guardarraíl de RLS ya cubre ese camino (falso positivo).")" \
  "$FICHEROS_NEUTROS" \
  ""

# "sin hallazgos" solo, también es una revisión completa.
assert_codigo "'sin hallazgos' sola: pasa" 0 \
  "$(cuerpo "## Revisión Codex" "sin hallazgos")" \
  "$FICHEROS_NEUTROS" \
  ""

# Sección vacía (título sin ningún contenido antes del siguiente '## '): falla.
assert_codigo "sección de revisión Codex vacía: falla" 1 \
  "$(cuerpo "## Revisión Codex" "" "## Otra sección" "texto")" \
  "$FICHEROS_NEUTROS" \
  ""

# Dependabot exime de la regla 2.
assert_codigo "dependabot[bot] exime de la sección de revisión Codex" 0 \
  "$(cuerpo "## Resumen" "Bump de Serilog 4.4.0 a 4.4.1.")" \
  "$FICHEROS_NEUTROS" \
  "dependabot[bot]"

# El valor por defecto es EXIGIR, no eximir: un autor vacío o cualquier otro
# valor que no sea exactamente "dependabot[bot]" no exime.
assert_codigo "autor vacío no exime (por defecto se exige la sección)" 1 \
  "$(cuerpo "## Resumen" "Cambio cualquiera.")" \
  "$FICHEROS_NEUTROS" \
  ""
assert_codigo "un bot con nombre parecido no exime ('dependabot' sin '[bot]')" 1 \
  "$(cuerpo "## Resumen" "Cambio cualquiera.")" \
  "$FICHEROS_NEUTROS" \
  "dependabot"

echo
echo "=== Hallazgos de Codex (PR2): comentario HTML, plurales ==="

# Un comentario HTML no es contenido real — GitHub lo renderiza como nada.
# Sin filtrarlo, "<!-- -->" contaba como sección rellena.
assert_codigo "sección con solo un comentario HTML: falla (no es contenido real)" 1 \
  "$(cuerpo "## Revisión Codex" "<!-- -->")" \
  "$FICHEROS_NEUTROS" \
  ""
# Con contenido real ADEMÁS del comentario, sí pasa — el comentario no debe
# contaminar el contenido legítimo que lo acompaña.
assert_codigo "comentario HTML junto a contenido real: pasa" 0 \
  "$(cuerpo "## Revisión Codex" "<!-- nota interna -->sin hallazgos")" \
  "$FICHEROS_NEUTROS" \
  ""

# Plural de los marcadores: "TODOs" y "resultados abajo" son variantes
# naturales de los mismos marcadores singulares ya cubiertos.
assert_codigo "marcador 'TODOs' (plural): falla" 1 \
  "$(cuerpo "## Revisión Codex" "TODOs: repasar con Codex")" \
  "$FICHEROS_NEUTROS" \
  ""
assert_codigo "marcador 'resultados abajo' (plural): falla" 1 \
  "$(cuerpo "## Revisión Codex" "2ª pasada — resultados abajo")" \
  "$FICHEROS_NEUTROS" \
  ""
assert_codigo "marcador 'resultado-abajo' (con guion): falla" 1 \
  "$(cuerpo "## Revisión Codex" "resultado-abajo")" \
  "$FICHEROS_NEUTROS" \
  ""

# "Todo" con solo la inicial en mayúscula, a secas, NO es un marcador —
# hacer el marcador TODO insensible a mayúsculas volvería a disparar con el
# español normal ("Todo el equipo revisó esto."), que es exactamente el
# falso positivo que el marcador en mayúsculas evita a propósito.
assert_codigo "'Todo' con solo la inicial en mayúscula no es un marcador" 0 \
  "$(cuerpo "## Revisión Codex" "Todo el equipo revisó esto, sin hallazgos.")" \
  "$FICHEROS_NEUTROS" \
  ""

echo
echo "=== Combinación de las dos reglas ==="

# PR que toca roles-de-cluster.sql Y no tiene revisión Codex: dos problemas
# (no uno), y el mensaje cubre las dos secciones — no solo que falle, sino
# que falle por las DOS razones (si una regresión dejara de evaluar la regla
# 2 tras fallar la regla 1, esto lo cazaría; el "Problemas: 2" del contador
# también, ver assert_menciona de abajo).
assert_codigo "faltan las dos secciones a la vez: falla" 1 \
  "$(cuerpo "## Resumen" "Cambio grande.")" \
  "$(ficheros "deploy/bootstrap/roles-de-cluster.sql")" \
  ""
assert_menciona "...y el diagnóstico de roles de clúster aparece" "Paso operativo en servidores" \
  "$(cuerpo "## Resumen" "Cambio grande.")" \
  "$(ficheros "deploy/bootstrap/roles-de-cluster.sql")" \
  ""
assert_menciona "...y el diagnóstico de revisión Codex aparece" "Revisión Codex" \
  "$(cuerpo "## Resumen" "Cambio grande.")" \
  "$(ficheros "deploy/bootstrap/roles-de-cluster.sql")" \
  ""
assert_menciona "...y el contador de problemas es 2, no 1" "Problemas: 2" \
  "$(cuerpo "## Resumen" "Cambio grande.")" \
  "$(ficheros "deploy/bootstrap/roles-de-cluster.sql")" \
  ""

# Las dos secciones completas: pasa.
assert_codigo "las dos secciones completas: pasa" 0 \
  "$(cuerpo "## Revisión Codex" "sin hallazgos" "" \
            "## Paso operativo en servidores" \
            "GRANT en staging y producción antes del despliegue.")" \
  "$(ficheros "deploy/bootstrap/roles-de-cluster.sql" "src/Foo.cs")" \
  ""

echo
echo "Pruebas: $PRUEBAS · Fallos: $FALLOS"
(( FALLOS == 0 )) || exit 1
