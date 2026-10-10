#!/bin/bash
# Tests de scripts/ci-exigir-dependencias.sh: recorre la tabla de verdad del
# agregador «Build, format y tests», y comprueba que ci.yml lo usa como el
# guion supone.
#
# Barridos:
#   1. Integración: producto completo de evento × resultado de integración ×
#      resultado de `alcance` × salida `integracion` de alcance, con el resto
#      en success (4 × 5 × 5 × 3 = 300 combinaciones).
#   2. E2E: el mismo producto para el resultado de E2E y la salida `e2e`.
#   3. Compilación, formato y arneses en cada valor distinto de success, en dos
#      contextos que por lo demás darían verde.
#   4. Modo SOLO_E2E (el del agregador «Tests E2E (Playwright)»): el mismo
#      producto para el resultado de los bloques, sin ninguna otra entrada.
#
# El oráculo NO reutiliza el guion: enumera las únicas combinaciones que valen
# y da todo lo demás por rojo. Los casos con nombre del final son los que la
# revisión debe poder leer sin ejecutar nada.
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPT="$SCRIPT_DIR/ci-exigir-dependencias.sh"
CI_YML="${CI_YML:-$SCRIPT_DIR/../.github/workflows/ci.yml}"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

PRUEBAS=0
FALLOS=0

# correr EVENTO COMPILACION FORMATO EXTENSION INTEGRACION E2E ALCANCE ALC_INT ALC_E2E
# deja CODIGO, E_INT y E_E2E (valores de las salidas; vacíos si no las emitió)
correr() {
  : > "$TMP/out"
  EVENTO="$1" R_COMPILACION="$2" R_FORMATO="$3" R_EXTENSION="$4" R_INTEGRACION="$5" R_E2E="$6" \
    R_ALCANCE="$7" ALCANCE_INTEGRACION="$8" ALCANCE_E2E="$9" GITHUB_OUTPUT="$TMP/out" \
    bash "$SCRIPT" > "$TMP/stdout" 2>&1
  CODIGO=$?
  E_INT=""; E_E2E=""
  local linea
  while IFS= read -r linea; do
    case "$linea" in
      integracion=*) E_INT="${linea#integracion=}" ;;
      e2e=*)         E_E2E="${linea#e2e=}" ;;
    esac
  done < "$TMP/out"
}

# esperado EVENTO RESULTADO ALCANCE SALIDA -> deja ESP en ejecutada|saltada|rojo
esperado() {
  ESP=rojo
  [[ -z "$1" ]] && return
  if [[ "$2" == "success" ]]; then ESP=ejecutada; return; fi
  if [[ "$1" != "merge_group" && "$2" == "skipped" && "$3" == "success" && "$4" == "false" ]]; then ESP=saltada; fi
}

# comprobar DESCRIPCION ESPERADO_INT ESPERADO_E2E   (rojo en cualquiera = rojo)
comprobar() {
  local desc="$1" esp_int="$2" esp_e2e="$3"
  PRUEBAS=$((PRUEBAS + 1))
  local esp="$esp_int $esp_e2e" obtenido="rojo"
  [[ "$esp_int" == rojo || "$esp_e2e" == rojo ]] && esp="rojo"
  [[ "$CODIGO" == 0 ]] && obtenido="$E_INT $E_E2E"
  # Un rojo no puede dejar escrita la salida: un paso posterior la leería.
  if [[ "$CODIGO" != 0 && -n "$E_INT$E_E2E" ]]; then obtenido="rojo-con-salida"; fi
  if [[ "$obtenido" != "$esp" ]]; then
    FALLOS=$((FALLOS + 1))
    echo "FALLO: $desc — esperado '$esp', obtenido '$obtenido' (código $CODIGO)" >&2
    return 1
  fi
}

RESULTADOS=(success failure cancelled skipped "")
EVENTOS=(merge_group pull_request push "")
SALIDAS=(true false "")

echo "=== Barrido 1: evento × integración × alcance × salida de alcance (E2E en success) ==="
for ev in "${EVENTOS[@]}"; do
  for res in "${RESULTADOS[@]}"; do
    for alc in "${RESULTADOS[@]}"; do
      for sal in "${SALIDAS[@]}"; do
        correr "$ev" success success success "$res" success "$alc" "$sal" true
        esperado "$ev" "$res" "$alc" "$sal"; esp_int="$ESP"
        esp_e2e=ejecutada; [[ -z "$ev" ]] && esp_e2e=rojo
        comprobar "evento='$ev' integracion='$res' alcance='$alc' salida='$sal'" "$esp_int" "$esp_e2e"
      done
    done
  done
done

echo "=== Barrido 2: evento × E2E × alcance × salida de alcance (integración en success) ==="
for ev in "${EVENTOS[@]}"; do
  for res in "${RESULTADOS[@]}"; do
    for alc in "${RESULTADOS[@]}"; do
      for sal in "${SALIDAS[@]}"; do
        correr "$ev" success success success success "$res" "$alc" true "$sal"
        esperado "$ev" "$res" "$alc" "$sal"; esp_e2e="$ESP"
        esp_int=ejecutada; [[ -z "$ev" ]] && esp_int=rojo
        comprobar "evento='$ev' e2e='$res' alcance='$alc' salida='$sal'" "$esp_int" "$esp_e2e"
      done
    done
  done
done
echo "Barridos 1 y 2: $PRUEBAS combinaciones"

echo "=== Barrido 3: compilación, formato y arneses solo valen en success ==="
for malo in failure cancelled skipped ""; do
  for pos in 2 3 4; do
    for contexto in "merge_group success success success true true" "pull_request skipped skipped success false false"; do
      read -r ev int e2e alc alc_int alc_e2e <<< "$contexto"
      args=("$ev" success success success "$int" "$e2e" "$alc" "$alc_int" "$alc_e2e")
      args[$((pos - 1))]="$malo"
      correr "${args[@]}"
      comprobar "dependencia nº $pos en '$malo' ($contexto)" rojo rojo
    done
  done
done

echo "=== Barrido 4: modo SOLO_E2E (agregador de los bloques de E2E): evento × bloques × alcance × salida ==="
# En este modo solo cuenta R_E2E: compilación, formato, arneses e integración
# van vacíos a propósito, para fijar que no se miran.
for ev in "${EVENTOS[@]}"; do
  for res in "${RESULTADOS[@]}"; do
    for alc in "${RESULTADOS[@]}"; do
      for sal in "${SALIDAS[@]}"; do
        : > "$TMP/out"
        SOLO_E2E=1 EVENTO="$ev" R_COMPILACION="" R_FORMATO="" R_EXTENSION="" R_INTEGRACION="" R_E2E="$res"           R_ALCANCE="$alc" ALCANCE_INTEGRACION="" ALCANCE_E2E="$sal" GITHUB_OUTPUT="$TMP/out"           bash "$SCRIPT" > "$TMP/stdout" 2>&1
        CODIGO=$?
        esperado "$ev" "$res" "$alc" "$sal"
        PRUEBAS=$((PRUEBAS + 1))
        obtenido="rojo"
        [[ "$CODIGO" == 0 ]] && obtenido="$(tr -d '' < "$TMP/out")"
        [[ "$CODIGO" != 0 && -s "$TMP/out" ]] && obtenido="rojo-con-salida"
        quiero="rojo"; [[ "$ESP" != rojo ]] && quiero="e2e=$ESP"
        if [[ "$obtenido" != "$quiero" ]]; then
          FALLOS=$((FALLOS + 1))
          echo "FALLO: SOLO_E2E evento='$ev' bloques='$res' alcance='$alc' salida='$sal' — esperado '$quiero', obtenido '$obtenido' (código $CODIGO)" >&2
        fi
      done
    done
  done
done

echo "=== Casos con nombre ==="
# caso DESCRIPCION ESP_INT ESP_E2E  EVENTO COMP FORM EXT INT E2E ALCANCE ALC_INT ALC_E2E
caso() {
  local desc="$1" esp_int="$2" esp_e2e="$3"; shift 3
  correr "$@"
  comprobar "$desc" "$esp_int" "$esp_e2e" && echo "OK: $desc -> $esp_int / $esp_e2e"
}
V=success
caso "grupo de fusión, todo en verde"                              ejecutada ejecutada merge_group  $V $V $V success   success   success true  true
caso "grupo de fusión, integración saltada aunque alcance diga false" rojo   ejecutada merge_group  $V $V $V skipped   success   success false true
caso "grupo de fusión, E2E saltados aunque alcance diga false"     ejecutada rojo      merge_group  $V $V $V success   skipped   success true  false
caso "grupo de fusión, integración cancelada"                      rojo      ejecutada merge_group  $V $V $V cancelled success   success true  true
caso "grupo de fusión, E2E cancelados"                             ejecutada rojo      merge_group  $V $V $V success   cancelled success true  true
caso "grupo de fusión, integración fallida"                        rojo      ejecutada merge_group  $V $V $V failure   success   success true  true
caso "grupo de fusión, alcance fallido pero pesados en verde"      ejecutada ejecutada merge_group  $V $V $V success   success   failure ""    ""
caso "PR ligera: integración y E2E saltados por alcance"           saltada   saltada   pull_request $V $V $V skipped   skipped   success false false
caso "PR que toca tests E2E: integración saltada, E2E en verde"    saltada   ejecutada pull_request $V $V $V skipped   success   success false true
caso "PR con ruta sensible o etiqueta: todo en verde"              ejecutada ejecutada pull_request $V $V $V success   success   success true  true
caso "PR: integración saltada pero alcance dijo true"              rojo      saltada   pull_request $V $V $V skipped   skipped   success true  false
caso "PR: E2E saltados pero alcance dijo true"                     saltada   rojo      pull_request $V $V $V skipped   skipped   success false true
caso "PR: pesados saltados y alcance falló"                        rojo      rojo      pull_request $V $V $V skipped   skipped   failure false false
caso "PR: pesados saltados y alcance sin salida"                   rojo      rojo      pull_request $V $V $V skipped   skipped   success ""    ""
caso "PR: integración cancelada con alcance false"                 rojo      saltada   pull_request $V $V $V cancelled skipped   success false false
caso "PR: E2E fallidos"                                            ejecutada rojo      pull_request $V $V $V success   failure   success true  true
caso "push verificado por la cola: pesados saltados"               saltada   saltada   push         $V $V $V skipped   skipped   success false false
caso "push no verificable: todo en verde"                          ejecutada ejecutada push         $V $V $V success   success   success true  true
caso "push: integración fallida"                                   rojo      ejecutada push         $V $V $V failure   success   success true  true
caso "evento vacío"                                                rojo      rojo      ""           $V $V $V success   success   success true  true

echo "=== Contrato con ci.yml ==="
# El guion no sirve de nada si el workflow deja de llamarlo como él supone.
# Estas comprobaciones leen el fichero real; no validan la semántica de GitHub
# Actions, solo que las piezas de las que depende la regla siguen escritas.
contrato() {
  local desc="$1" esperado="$2" obtenido="$3"
  PRUEBAS=$((PRUEBAS + 1))
  if [[ "$obtenido" == "$esperado" ]]; then
    echo "OK: $desc"
  else
    FALLOS=$((FALLOS + 1)); echo "FALLO: $desc — esperado $esperado, obtenido $obtenido" >&2
  fi
}
yml="$(tr -d '\r' < "$CI_YML")"
cuenta() { grep -cF -- "$1" <<< "$yml" || true; }
contrato "los dos pasos que ejecutan el guion tienen id: exigir (agregador general y agregador de E2E)" 2 "$(grep -cE '^        id: exigir$' <<< "$yml" || true)"
contrato "el guion se ejecuta en esos dos pasos y en ninguno más" 2 "$(cuenta 'run: bash scripts/ci-exigir-dependencias.sh')"
contrato "seis pasos se saltan solo con integracion == saltada" 6 "$(cuenta "if: steps.exigir.outputs.integracion != 'saltada'")"
contrato "ningún paso exige == 'ejecutada' (fallaría abierto si faltara la salida)" 0 "$(cuenta "steps.exigir.outputs.integracion == 'ejecutada'")"
for paso in "Recoger el reparto de los bloques" "Comprobar que los cuatro bloques descubrieron lo mismo" "Comprobar que el reparto cubrio todas las clases" "Recoger la cobertura de los bloques de integración" "Comprobar que se ejecutaron todos los tests" "Umbral de cobertura del núcleo (ratchet)"; do
  contrato "«$paso» lleva la condición justo debajo" 1 "$(grep -FA1 -- "      - name: $paso" <<< "$yml" | grep -cF "if: steps.exigir.outputs.integracion != 'saltada'" || true)"
done
contrato "el agregador corre siempre y depende de alcance, integración y E2E" 1 "$(sed -n '/^    needs: \[alcance, compilacion-y-unitarios, formato, tests-integracion, e2e-tests, bunit-tests, extension-arneses\]$/,/^    steps:$/p' <<< "$yml" | grep -cE '^    if: always\(\)$' || true)"
for par in "R_INTEGRACION: \${{ needs.tests-integracion.result }}" "R_E2E: \${{ needs.e2e-tests.result }}" "R_ALCANCE: \${{ needs.alcance.result }}" "ALCANCE_INTEGRACION: \${{ needs.alcance.outputs.integracion }}" "ALCANCE_E2E: \${{ needs.alcance.outputs.e2e }}" "EVENTO: \${{ github.event_name }}"; do
  contrato "el paso recibe $par" 1 "$([[ "$(cuenta "          $par")" -ge 1 ]] && echo 1 || echo 0)"
done
for salida in integracion e2e carga; do
  contrato "un job pesado solo se salta con $salida == 'false' y nunca en merge_group" 1 "$(cuenta "    if: \${{ !cancelled() && (github.event_name == 'merge_group' || needs.alcance.outputs.$salida != 'false') }}")"
done
contrato "los tres jobs pesados dependen de alcance" 3 "$(grep -cE '^    needs: alcance$' <<< "$yml" || true)"

# Agregador de los bloques de E2E: conserva el nombre del check obligatorio.
e2e_agregador="$(sed -n '/^  e2e-tests:$/,/^    steps:$/p' <<< "$yml")"
contrato "un único job se llama «Tests E2E (Playwright)»" 1 "$(grep -cE '^    name: Tests E2E \(Playwright\)$' <<< "$yml" || true)"
contrato "ese nombre es el del job e2e-tests" 1 "$(grep -cE '^    name: Tests E2E \(Playwright\)$' <<< "$e2e_agregador" || true)"
contrato "el agregador de E2E corre siempre" 1 "$(grep -cE '^    if: always\(\)$' <<< "$e2e_agregador" || true)"
contrato "el agregador de E2E depende de alcance y de los bloques" 1 "$(grep -cF -- '    needs: [alcance, e2e-bloques]' <<< "$e2e_agregador" || true)"
contrato "los bloques de E2E son el job e2e-bloques, con matriz de tres" 1 "$(sed -n '/^  e2e-bloques:$/,/^    steps:$/p' <<< "$yml" | grep -cF -- '        bloque: [1, 2, 3]' || true)"
e2e_pasos="$(sed -n '/^  e2e-tests:$/,/^  load-test:$/p' <<< "$yml")"
contrato "el agregador de E2E usa el modo SOLO_E2E" 1 "$(grep -cF -- '          SOLO_E2E: "1"' <<< "$e2e_pasos" || true)"
contrato "el agregador de E2E recibe el resultado de los bloques" 1 "$(grep -cF -- '          R_E2E: ${{ needs.e2e-bloques.result }}' <<< "$e2e_pasos" || true)"
contrato "el agregador de E2E recibe el resultado y la salida de alcance" 2 "$(grep -cE '^          (R_ALCANCE: \$\{\{ needs\.alcance\.result \}\}|ALCANCE_E2E: \$\{\{ needs\.alcance\.outputs\.e2e \}\})$' <<< "$e2e_pasos" || true)"
contrato "cuatro pasos del agregador de E2E se saltan solo con e2e == saltada" 4 "$(grep -cF -- "        if: steps.exigir.outputs.e2e != 'saltada'" <<< "$e2e_pasos" || true)"
contrato "ningún paso exige e2e == 'ejecutada'" 0 "$(cuenta "steps.exigir.outputs.e2e == 'ejecutada'")"
contrato "el agregador general sigue recibiendo el resultado del job e2e-tests" 1 "$(cuenta '          R_E2E: ${{ needs.e2e-tests.result }}')"

echo
echo "Pruebas: $PRUEBAS · Fallos: $FALLOS"
(( FALLOS == 0 )) || exit 1
