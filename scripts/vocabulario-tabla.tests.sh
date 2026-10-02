#!/bin/bash
# Prueba de scripts/vocabulario-tabla.py (S1): las pruebas de vocabulario-tabla.tests.py contra el guion real,
# y después contra copias MUTADAS del guion, que DEBEN fallar. Una prueba que no puede fallar no cuenta como
# evidencia (§ 3 del protocolo): cada mutante cambia una regla del generador y la prueba que la vigila tiene
# que ponerse en rojo por esa razón. Además comprueba el vocabulario REAL (los ejemplos de cada patrón con el
# motor de Python) y que el JSON versionado es el que el guion lee por defecto.
set -uo pipefail

AQUI="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# `command -v python3` no basta: en Windows el alias de la Microsoft Store existe y falla al invocarse.
if python3 --version >/dev/null 2>&1; then PY=python3; else PY=python; fi

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

FALLOS=0
echo "vocabulario-tabla.py contra sus pruebas"
if "$PY" "$AQUI/vocabulario-tabla.tests.py" >"$TMP/base.txt" 2>&1; then
  echo "  ok    el guion real pasa sus pruebas ($(grep -Eo 'Ran [0-9]+ tests' "$TMP/base.txt"))"
else
  echo "  FALLO el guion real NO pasa sus pruebas"; tail -n 30 "$TMP/base.txt"; FALLOS=$((FALLOS + 1))
fi

if "$PY" "$AQUI/vocabulario-tabla.py" ejemplos >"$TMP/ejemplos.txt" 2>&1; then
  echo "  ok    el vocabulario real pasa los ejemplos de sus patrones ($(tail -n 1 "$TMP/ejemplos.txt"))"
else
  echo "  FALLO el vocabulario real NO pasa los ejemplos de sus patrones"; cat "$TMP/ejemplos.txt"; FALLOS=$((FALLOS + 1))
fi

mutante() { # mutante <nombre> <expresion sed> <prueba que debe ponerse en rojo>
  local nombre="$1" expr="$2" esperado="$3"
  cp "$AQUI/vocabulario-tabla.py" "$TMP/mutado.py"
  sed -i "$expr" "$TMP/mutado.py"
  if cmp -s "$AQUI/vocabulario-tabla.py" "$TMP/mutado.py"; then
    echo "  FALLO mutante '$nombre': el mutador NO mutó (patrón desfasado)"; FALLOS=$((FALLOS + 1)); return
  fi
  if VOCABULARIO_SCRIPT="$TMP/mutado.py" "$PY" "$AQUI/vocabulario-tabla.tests.py" >"$TMP/mut.txt" 2>&1; then
    echo "  FALLO mutante '$nombre': las pruebas siguen en VERDE (no vigilan esa regla)"; FALLOS=$((FALLOS + 1))
  elif grep -Eq "^(FAIL|ERROR): $esperado" "$TMP/mut.txt"; then
    echo "  ok    mutante '$nombre': rojo en $esperado"
  else
    echo "  FALLO mutante '$nombre': rojo, pero NO por el motivo previsto ($esperado)"; grep -E "^(FAIL|ERROR)" "$TMP/mut.txt" | head -n 5; FALLOS=$((FALLOS + 1))
  fi
}

echo "mutantes (cada uno debe poner en rojo la prueba que vigila su regla)"
mutante "no escapa la barra vertical de una celda"       's/t.replace("|", "\\\\|") if t else/t if t else/'                        "test_la_barra_vertical"
mutante "aplicar no conserva el CRLF del documento"       's/    crlf = "\\r\\n" in destino/    crlf = False/'                          "test_aplicar_solo_toca"
mutante "verificar da siempre bien"                       's/    if actual == esperado:/    if True:/'                                "test_verificar_ok_tras_aplicar"
mutante "aplicar reescribe aunque no haya cambio"         's/    if actual == nuevo:/    if False:/'                                 "test_aplicar_es_idempotente"
mutante "los ejemplos legítimos no se comprueban"         's/for ejemplo in p.get("noCasa", \[\]):/for ejemplo in []:/'             "test_los_ejemplos_detectan"
mutante "los ejemplos que deben cazar no se comprueban"   's/for ejemplo in p.get("casa", \[\]):/for ejemplo in []:/'               "test_los_ejemplos_detectan"
mutante "las marcas no se exigen únicas"                  's/    if texto.count(MARCA_INICIO) != 1 or texto.count(MARCA_FIN) != 1:/    if False:/' "test_las_marcas_ausentes"
mutante "la tabla omite las excepciones"                  's/    if datos\["excepciones"\]:/    if False:/'                          "test_la_tabla_sintetica"
mutante "la tabla de canónicos omite el plural"           's/c.get("plural"), lista(/None, lista(/'                                 "test_la_tabla_sintetica"
mutante "el patrón se compila sin ignorar mayúsculas"     's/re.IGNORECASE if p.get("ignorarMayusculas") else 0/0/'                 "test_los_ejemplos_detectan"

echo
[ "$FALLOS" -eq 0 ] && echo "OK: 0 fallos" || echo "FALLOS: $FALLOS"
[ "$FALLOS" -eq 0 ]
