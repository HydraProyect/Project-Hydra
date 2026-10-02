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
  elif grep -Eq "^(FAIL|ERROR): $esperado" "$TMP/mut.txt"; then
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
mutante "M16 ignora Vocabulario.json y usa su regex local" 's/    if vocabulario.is_file():/    if False:/'                "test_M16_toma_el_patron"
mutante "M16 da 0 si Vocabulario.json está roto"    's/            patron = None/            patron = re.compile("")/'                                "test_M16_con_Vocabulario_json_ilegible"
mutante "M3 la negación no admite la barra («sin ALTA/MEDIA»)" 's/(?:,|\/|y|ni|o)/(?:,|y|ni|o)/'  "test_M3_la_negacion_admite_la_barra"
mutante "M5 cuenta cualquier rama, no solo main"      's/r\["headBranch"\] == "main"/True/'                           "test_M5_cuenta_los_rojos"
mutante "M5 cuenta cualquier evento, no solo push"    's/r\["event"\] == "push" and r\["headBranch"\]/r["headBranch"]/' "test_M5_cuenta_los_rojos"
mutante "M7 usa la media en vez de la mediana"        's/med_todas = statistics.median(\[p\["changedFiles"\] for p in humanos\])/med_todas = statistics.mean([p["changedFiles"] for p in humanos])/' "test_M2_M3_M6_M7_M8_M9_M10"
mutante "M7 incluye a Dependabot"                     's/not in ("dependabot\[bot\]", "app\/dependabot")/not in ()/'  "test_M2_M3_M6_M7_M8_M9_M10"
mutante "M7 ignora la ventana --desde"                's/ >= args.desde\]/ >= ""]/'                                   "test_M2_M3_M6_M7_M8_M9_M10"
mutante "M8 mide al revés (fusión menos creación)"    's/return (b - a).total_seconds() \/ 60/return (a - b).total_seconds() \/ 60/' "test_minutos"
mutante "M10 cuenta ficheros de una sola PR"          's/if n >= 2)/if n >= 1)/'                                      "test_M2_M3_M6_M7_M8_M9_M10"
mutante "M6 divide por PR en vez de por defecto"      's/toques \/ len(mapa)/toques \/ len(num_con_d)/'              "test_M2_M3_M6_M7_M8_M9_M10"
mutante "la ventana del lote ignora el máximo"        's/args.pr_min <= p\["number"\] <= args.pr_max\]/args.pr_min <= p["number"]]/' "test_M2_M3_M6_M7_M8_M9_M10"
mutante "el tope de --limite-pr no se avisa"          's/    if lote_recortado:/    if False:/'                          "test_el_tope_de_limite_pr"
mutante "el tope avisa aunque cubra la ventana"       's/lote_recortado = tope and min(p\["number"\] for p in prs) > args.pr_min/lote_recortado = tope/' "test_el_tope_de_limite_pr"
mutante "el tope de la ventana --desde no se avisa"   's/desde_recortado = tope and /desde_recortado = False and /'         "test_el_tope_que_corta_la_ventana_desde"
mutante "M18 da 0 si la lista no tiene el formato"    's/        if not archivos:/        if False:/'                    "test_directorios_existentes_pero_vacios"
mutante "--comparar no tolera el BOM de PowerShell"    's/encoding="utf-8-sig"/encoding="utf-8"/'                    "test_comparar_lee_una_linea_base_con_BOM"
mutante "M9 no reconoce 2ª parte"                    's/|2\\.?ª parte//'                                               "test_M9_continuaciones"

echo
[ "$FALLOS" -eq 0 ] && echo "OK: 0 fallos" || echo "FALLOS: $FALLOS"
[ "$FALLOS" -eq 0 ]
