#!/bin/bash
# Prueba de scripts/metricas-proceso.py (S15): las pruebas de metricas-proceso.tests.py contra el guion
# real, y después contra copias MUTADAS del guion, que DEBEN fallar. Una prueba que no puede fallar no
# cuenta como evidencia (§ 3 del protocolo): cada mutante cambia una regla de medida y la prueba que la
# vigila tiene que ponerse en rojo por esa razón.
set -uo pipefail

AQUI="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# `command -v python3` no basta: en Windows el alias de la Microsoft Store existe y falla al invocarse.
if python3 --version >/dev/null 2>&1; then PY=python3; else PY=python; fi

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

FALLOS=0
echo "metricas-proceso.py contra sus pruebas"
if "$PY" "$AQUI/metricas-proceso.tests.py" >"$TMP/base.txt" 2>&1; then
  echo "  ok    el guion real pasa sus pruebas ($(grep -Eo 'Ran [0-9]+ tests' "$TMP/base.txt"))"
else
  echo "  FALLO el guion real NO pasa sus pruebas"; tail -n 30 "$TMP/base.txt"; FALLOS=$((FALLOS + 1))
fi

mutante() { # mutante <nombre> <expresion sed> <fragmento que debe aparecer en el fallo>
  local nombre="$1" expr="$2" esperado="$3"
  cp "$AQUI/metricas-proceso.py" "$TMP/mutado.py"
  sed -i "$expr" "$TMP/mutado.py"
  if cmp -s "$AQUI/metricas-proceso.py" "$TMP/mutado.py"; then
    echo "  FALLO mutante '$nombre': el mutador NO mutó (patrón desfasado)"; FALLOS=$((FALLOS + 1)); return
  fi
  if METRICAS_SCRIPT="$TMP/mutado.py" "$PY" "$AQUI/metricas-proceso.tests.py" >"$TMP/mut.txt" 2>&1; then
    echo "  FALLO mutante '$nombre': las pruebas siguen en VERDE (no vigilan esa regla)"; FALLOS=$((FALLOS + 1))
  elif grep -q "$esperado" "$TMP/mut.txt"; then
    echo "  ok    mutante '$nombre': rojo en $esperado"
  else
    echo "  FALLO mutante '$nombre': rojo, pero NO por el motivo previsto ($esperado)"; grep -E "^(FAIL|ERROR)" "$TMP/mut.txt" | head -n 5; FALLOS=$((FALLOS + 1))
  fi
}

echo "mutantes (cada uno debe poner en rojo la prueba que vigila su regla)"
mutante "M2 exige tres PR en vez de dos"            's/len(ps) >= 2/len(ps) >= 3/'                                   "test_M2_cuenta_defectos"
mutante "M2 mira todo el cuerpo, no la 1.ª sección" 's/primera_seccion(pr.get("body") or "")/(pr.get("body") or "")/'  "test_M2_cuenta_defectos"
mutante "M3 ignora el nivel alto"                    's/if alto:/if False:/'                                          "test_M3_distingue_alto_medio"
mutante "M3 cuenta la negación («sin hallazgos altos») como hallazgo" 's/sec = NEGACION_DE_GRAVEDAD.sub(" ", sec)/pass/' "test_M3_la_negacion"
mutante "M3 la sección no termina en el encabezado"  's/(?=^## |\\Z)/(?=\\Z)/'                                         "test_M3_la_seccion_termina"
mutante "M11 no excluye comentarios Razor"           's/    texto = re.sub(r"(?s)@\\\*.\*?\\\*@", "", texto)/    pass/' "test_un_comentario_razor_no_cuenta"
mutante "M11 casa BotonPlus por prefijo"             's/(?!\[\\w.\])//'                                                "test_no_se_cierra_en_una_flecha"
mutante "M11 ignora las comillas dentro de la etiqueta" 's/elif c in "\\"'"'"'":/elif False:/'                         "test_no_se_cierra_en_una_flecha"
mutante "M11 ignora los paréntesis dentro de la etiqueta" 's/elif c in "({\[":/elif False:/'                          "test_no_se_cierra_en_una_flecha"
mutante "M15 cuenta el First de LINQ"                's/(?!\\s\*\[(<\])//'                                             "test_M15_locators_e2e"
mutante "gh que falla devuelve datos vacíos"         's/raise GhNoDisponible(f"gh {/return [] if True else (f"gh {/'  "test_gh_que_falla_es_NO_MEDIDA"
mutante "M16 cuenta Cliente empresarial"             's/(?!\\s+(?:empresarial|empresariales|comercial|de servicio|delegante))//' "test_M16_cliente_a_secas"
mutante "M9 no reconoce 2ª parte"                    's/|2\\.?ª parte//'                                               "test_M9_continuaciones"

echo
[ "$FALLOS" -eq 0 ] && echo "OK: 0 fallos" || echo "FALLOS: $FALLOS"
[ "$FALLOS" -eq 0 ]
