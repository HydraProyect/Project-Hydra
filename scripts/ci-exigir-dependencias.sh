#!/bin/bash
# Primer paso del job agregador «Build, format y tests» de ci.yml: exige que
# sus dependencias hayan terminado como deben. Vivía en línea en el workflow;
# se sacó a un guion al añadir el alcance del CI (scripts/ci-alcance.sh) para
# poder recorrer su tabla de verdad entera en scripts/ci-exigir-dependencias.tests.sh.
#
# POR QUÉ ES LA PIEZA CRÍTICA. «Build, format y tests» es un check obligatorio
# de main. Un job que se salta reporta su check como «skipped», y GitHub trata
# un obligatorio omitido como aprobado: por eso el job corre con `if: always()`
# y este paso falla a propósito si una dependencia no salió bien, en vez de
# dejar que el job no se ejecute.
#
# REGLA.
#   - Compilación y unitarios, formato y arneses de la extensión: `success`,
#     en todo evento. Nada más vale.
#   - Tests de integración (los 4 bloques, resultado agregado de la matriz) y
#     tests E2E, cada uno por separado:
#       · en `merge_group`: `success`. Nada más vale. El grupo de fusión es la
#         puerta y ahí los dos corren siempre.
#       · fuera de `merge_group`: `success`, o bien `skipped` ÚNICAMENTE si el
#         job `alcance` terminó en `success` y su salida para ese job
#         (`integracion` o `e2e`) es exactamente `false`: lo saltó a propósito
#         (ver ci-alcance.sh).
#   - Cualquier otra combinación —failure, cancelled, skipped sin ese permiso,
#     vacío, un valor que no se conoce— es rojo.
#
# Los E2E ya son un check obligatorio por sí mismos; se exigen también aquí
# como segunda barrera, porque un job obligatorio que se salta cuenta como
# aprobado y este agregador es el único sitio que puede decir «en el grupo de
# fusión, saltado es rojo».
#
# Salida `integracion` en $GITHUB_OUTPUT: `ejecutada` o `saltada` (y `e2e`,
# igual). Los pasos del agregador que leen artefactos de integración (reparto
# de bloques, recuento de tests, cobertura del núcleo y su trinquete) se
# saltan solo con `saltada`: sin integración no hay nada que recoger, y medir
# el trinquete del núcleo sobre dos suites en vez de tres lo haría caer por un
# motivo que no es una pérdida de cobertura. La condición de esos pasos es
# `!= 'saltada'` y no `== 'ejecutada'` a propósito: si esta salida faltara
# (paso renombrado, escritura fallida), corren.
#
# Entradas (entorno): EVENTO, R_COMPILACION, R_FORMATO, R_EXTENSION,
# R_INTEGRACION, R_E2E, R_ALCANCE, ALCANCE_INTEGRACION, ALCANCE_E2E.
#
# MODO SOLO_E2E=1. Lo usa el job «Tests E2E (Playwright)», check obligatorio
# de main, que agrega los tres bloques de E2E: aplica la regla de los pesados
# a R_E2E (resultado de la matriz de bloques) y nada más. Entradas: EVENTO,
# R_E2E, R_ALCANCE, ALCANCE_E2E. Salida: solo `e2e`. Desde ese cambio el job
# «Build, format y tests» recibe en R_E2E el resultado del agregador, que ya
# es `success` cuando los bloques se saltaron con permiso.
set -uo pipefail

EVENTO="${EVENTO:-}"
R_COMPILACION="${R_COMPILACION:-}"
R_FORMATO="${R_FORMATO:-}"
R_EXTENSION="${R_EXTENSION:-}"
R_INTEGRACION="${R_INTEGRACION:-}"
R_E2E="${R_E2E:-}"
R_ALCANCE="${R_ALCANCE:-}"
ALCANCE_INTEGRACION="${ALCANCE_INTEGRACION:-}"
ALCANCE_E2E="${ALCANCE_E2E:-}"

echo "Evento: ${EVENTO:-<vacío>}"
echo "Compilación y tests unitarios: ${R_COMPILACION:-<vacío>}"
echo "Verificar formato: ${R_FORMATO:-<vacío>}"
echo "Arneses de la extensión: ${R_EXTENSION:-<vacío>}"
echo "Tests de integración (4 bloques): ${R_INTEGRACION:-<vacío>}"
echo "Tests E2E: ${R_E2E:-<vacío>}"
echo "Alcance del CI: ${R_ALCANCE:-<vacío>} (integracion=${ALCANCE_INTEGRACION:-<vacío>}, e2e=${ALCANCE_E2E:-<vacío>})"

rojo() {
  echo "::error::$1 Este check obligatorio falla a propósito en vez de quedarse sin ejecutar, porque un check obligatorio omitido cuenta como aprobado."
  exit 1
}

if [[ -z "$EVENTO" ]]; then
  rojo "No se sabe qué evento disparó el run."
fi

if [[ "${SOLO_E2E:-}" != "1" && ( "$R_COMPILACION" != "success" || "$R_FORMATO" != "success" || "$R_EXTENSION" != "success" ) ]]; then
  rojo "Una dependencia no terminó en success."
fi

# pesado NOMBRE RESULTADO SALIDA_DE_ALCANCE -> deja ESTADO en ejecutada|saltada, o rojo
pesado() {
  local nombre="$1" resultado="$2" salida="$3"
  if [[ "$resultado" == "success" ]]; then
    ESTADO="ejecutada"
  elif [[ "$EVENTO" != "merge_group" && "$resultado" == "skipped" \
          && "$R_ALCANCE" == "success" && "$salida" == "false" ]]; then
    ESTADO="saltada"
  elif [[ "$EVENTO" == "merge_group" ]]; then
    rojo "En el grupo de fusión los $nombre tienen que terminar en success (terminaron en «${resultado:-vacío}»)."
  elif [[ "$resultado" == "skipped" ]]; then
    rojo "Los $nombre se saltaron sin que el alcance del CI lo decidiera (alcance: «${R_ALCANCE:-vacío}», salida «${salida:-vacío}»)."
  else
    rojo "Los $nombre no terminaron en success (terminaron en «${resultado:-vacío}»)."
  fi
}

# SOLO_E2E=1: el mismo guion, usado por el job «Tests E2E (Playwright)», que
# desde que los E2E corren en tres bloques es un agregador de esos bloques.
# R_E2E es entonces el resultado agregado de la matriz de bloques y solo se
# aplica la regla de los pesados a los E2E; no hay nada más que exigir.
if [[ "${SOLO_E2E:-}" == "1" ]]; then
  pesado "bloques de tests E2E" "$R_E2E" "$ALCANCE_E2E"
  if [[ "$ESTADO" == "saltada" ]]; then
    echo "E2E saltados por el alcance del CI: corren en el grupo de fusión."
  else
    echo "Los bloques de E2E terminaron en success."
  fi
  if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
    if ! echo "e2e=$ESTADO" >> "$GITHUB_OUTPUT"; then
      rojo "No se pudo escribir la salida del paso."
    fi
  fi
  echo "e2e=$ESTADO"
  exit 0
fi

pesado "tests de integración" "$R_INTEGRACION" "$ALCANCE_INTEGRACION"
estado_integracion="$ESTADO"
pesado "tests E2E" "$R_E2E" "$ALCANCE_E2E"
estado_e2e="$ESTADO"

if [[ "$estado_integracion" == "saltada" ]]; then
  echo "Integración saltada por el alcance del CI: corre en el grupo de fusión. Los pasos que leen sus artefactos (reparto, recuento, cobertura y trinquete del núcleo) no se ejecutan en este run."
fi
if [[ "$estado_e2e" == "saltada" ]]; then
  echo "E2E saltados por el alcance del CI: corren en el grupo de fusión."
fi
if [[ "$estado_integracion" == "ejecutada" && "$estado_e2e" == "ejecutada" ]]; then
  echo "Todas las dependencias terminaron en success."
fi
if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
  if ! { echo "integracion=$estado_integracion"; echo "e2e=$estado_e2e"; } >> "$GITHUB_OUTPUT"; then
    rojo "No se pudo escribir la salida del paso."
  fi
fi
echo "integracion=$estado_integracion"
echo "e2e=$estado_e2e"
