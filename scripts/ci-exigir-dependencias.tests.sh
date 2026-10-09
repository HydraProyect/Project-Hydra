#!/bin/bash
# Tests de scripts/ci-exigir-dependencias.sh: recorre la tabla de verdad del
# agregador «Build, format y tests».
#
# Dos barridos:
#   1. Producto completo de evento × resultado de integración × resultado de
#      `alcance` × salida `integracion` de alcance, con el resto en success
#      (4 × 5 × 5 × 3 = 300 combinaciones).
#   2. Cada una de las otras tres dependencias en cada valor distinto de
#      success, en dos contextos que por lo demás darían verde.
#
# El oráculo NO reutiliza el guion: enumera las únicas combinaciones que valen
# y da todo lo demás por rojo. Los casos con nombre del final son los que la
# revisión debe poder leer sin ejecutar nada.
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPT="$SCRIPT_DIR/ci-exigir-dependencias.sh"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

PRUEBAS=0
FALLOS=0

# correr EVENTO COMPILACION FORMATO EXTENSION INTEGRACION ALCANCE ALC_INT
# deja CODIGO y ESTADO (valor de la salida `integracion`, vacío si no la emitió)
correr() {
  : > "$TMP/out"
  EVENTO="$1" R_COMPILACION="$2" R_FORMATO="$3" R_EXTENSION="$4" R_INTEGRACION="$5" \
    R_ALCANCE="$6" ALCANCE_INTEGRACION="$7" GITHUB_OUTPUT="$TMP/out" \
    bash "$SCRIPT" > "$TMP/stdout" 2>&1
  CODIGO=$?
  ESTADO="$(sed -n 's/^integracion=//p' "$TMP/out" | tail -1)"
}

# esperado EVENTO INTEGRACION ALCANCE ALC_INT (resto en success) -> ejecutada|saltada|rojo
esperado() {
  local ev="$1" int="$2" alc="$3" alc_int="$4"
  [[ -z "$ev" ]] && { echo rojo; return; }
  [[ "$int" == "success" ]] && { echo ejecutada; return; }
  if [[ "$ev" != "merge_group" && "$int" == "skipped" && "$alc" == "success" && "$alc_int" == "false" ]]; then
    echo saltada; return
  fi
  echo rojo
}

comprobar() {
  local desc="$1" esp="$2"
  PRUEBAS=$((PRUEBAS + 1))
  local obtenido="rojo"
  [[ "$CODIGO" == 0 ]] && obtenido="$ESTADO"
  # Un rojo no puede dejar escrita la salida: un paso posterior la leería.
  if [[ "$CODIGO" != 0 && -n "$ESTADO" ]]; then obtenido="rojo-con-salida"; fi
  if [[ "$obtenido" != "$esp" ]]; then
    FALLOS=$((FALLOS + 1))
    echo "FALLO: $desc — esperado $esp, obtenido $obtenido (código $CODIGO)" >&2
  fi
}

RESULTADOS=(success failure cancelled skipped "")
EVENTOS=(merge_group pull_request push "")
SALIDAS=(true false "")

echo "=== Barrido 1: evento × integración × alcance × salida de alcance ==="
for ev in "${EVENTOS[@]}"; do
  for int in "${RESULTADOS[@]}"; do
    for alc in "${RESULTADOS[@]}"; do
      for alc_int in "${SALIDAS[@]}"; do
        correr "$ev" success success success "$int" "$alc" "$alc_int"
        comprobar "evento='$ev' integracion='$int' alcance='$alc' salida='$alc_int'" "$(esperado "$ev" "$int" "$alc" "$alc_int")"
      done
    done
  done
done
echo "Barrido 1: $PRUEBAS combinaciones"

echo "=== Barrido 2: compilación, formato y arneses solo valen en success ==="
for malo in failure cancelled skipped ""; do
  for pos in 2 3 4; do
    for contexto in "merge_group success success true" "pull_request skipped success false"; do
      read -r ev int alc alc_int <<< "$contexto"
      args=("$ev" success success success "$int" "$alc" "$alc_int")
      args[$((pos - 1))]="$malo"
      correr "${args[@]}"
      comprobar "dependencia nº $pos en '$malo' ($contexto)" rojo
    done
  done
done

echo "=== Casos con nombre ==="
caso() {
  local desc="$1" esp="$2"; shift 2
  correr "$@"
  comprobar "$desc" "$esp"
  local obtenido="rojo"; [[ "$CODIGO" == 0 ]] && obtenido="$ESTADO"
  [[ "$obtenido" == "$esp" ]] && echo "OK: $desc -> $esp"
}
caso "grupo de fusión, todo en verde"                         ejecutada merge_group  success success success success   success true
caso "grupo de fusión, integración saltada aunque alcance diga false" rojo merge_group  success success success skipped   success false
caso "grupo de fusión, integración cancelada"                 rojo      merge_group  success success success cancelled success true
caso "grupo de fusión, integración fallida"                   rojo      merge_group  success success success failure   success true
caso "grupo de fusión, alcance fallido pero integración verde" ejecutada merge_group  success success success success   failure ""
caso "PR ligera: integración saltada por alcance"             saltada   pull_request success success success skipped   success false
caso "PR con ruta sensible: integración verde"                ejecutada pull_request success success success success   success true
caso "PR: integración saltada pero alcance dijo true"         rojo      pull_request success success success skipped   success true
caso "PR: integración saltada y alcance falló"                rojo      pull_request success success success skipped   failure false
caso "PR: integración saltada y alcance sin salida"           rojo      pull_request success success success skipped   success ""
caso "PR: integración cancelada con alcance false"            rojo      pull_request success success success cancelled success false
caso "PR: integración fallida"                                rojo      pull_request success success success failure   success true
caso "push verificado por la cola: integración saltada"       saltada   push         success success success skipped   success false
caso "push no verificable: integración verde"                 ejecutada push         success success success success   success true
caso "push: integración fallida"                              rojo      push         success success success failure   success true
caso "evento vacío"                                           rojo      ""           success success success success   success true

echo
echo "Pruebas: $PRUEBAS · Fallos: $FALLOS"
(( FALLOS == 0 )) || exit 1
