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
#   - Tests de integración (los 4 bloques, resultado agregado de la matriz):
#       · en `merge_group`: `success`. Nada más vale. El grupo de fusión es la
#         puerta y ahí la integración corre siempre.
#       · fuera de `merge_group`: `success`, o bien `skipped` ÚNICAMENTE si el
#         job `alcance` terminó en `success` y su salida `integracion` es
#         exactamente `false` (la saltó a propósito, ver ci-alcance.sh).
#   - Cualquier otra combinación —failure, cancelled, skipped sin ese permiso,
#     vacío, un valor que no se conoce— es rojo.
#
# Salida `integracion` en $GITHUB_OUTPUT: `ejecutada` o `saltada`. Los pasos
# del agregador que leen artefactos de integración (reparto de bloques,
# recuento de tests, cobertura del núcleo y su trinquete) se condicionan a
# `ejecutada`: sin integración no hay nada que recoger, y medir el trinquete
# del núcleo sobre dos suites en vez de tres lo haría caer por un motivo que
# no es una pérdida de cobertura.
#
# Entradas (entorno): EVENTO, R_COMPILACION, R_FORMATO, R_EXTENSION,
# R_INTEGRACION, R_ALCANCE, ALCANCE_INTEGRACION.
set -uo pipefail

EVENTO="${EVENTO:-}"
R_COMPILACION="${R_COMPILACION:-}"
R_FORMATO="${R_FORMATO:-}"
R_EXTENSION="${R_EXTENSION:-}"
R_INTEGRACION="${R_INTEGRACION:-}"
R_ALCANCE="${R_ALCANCE:-}"
ALCANCE_INTEGRACION="${ALCANCE_INTEGRACION:-}"

echo "Evento: ${EVENTO:-<vacío>}"
echo "Compilación y tests unitarios: ${R_COMPILACION:-<vacío>}"
echo "Verificar formato: ${R_FORMATO:-<vacío>}"
echo "Arneses de la extensión: ${R_EXTENSION:-<vacío>}"
echo "Tests de integración (4 bloques): ${R_INTEGRACION:-<vacío>}"
echo "Alcance del CI: ${R_ALCANCE:-<vacío>} (integracion=${ALCANCE_INTEGRACION:-<vacío>})"

rojo() {
  echo "::error::$1 Este check obligatorio falla a propósito en vez de quedarse sin ejecutar, porque un check obligatorio omitido cuenta como aprobado."
  exit 1
}

if [[ -z "$EVENTO" ]]; then
  rojo "No se sabe qué evento disparó el run."
fi

if [[ "$R_COMPILACION" != "success" || "$R_FORMATO" != "success" || "$R_EXTENSION" != "success" ]]; then
  rojo "Una dependencia no terminó en success."
fi

estado=""
if [[ "$R_INTEGRACION" == "success" ]]; then
  estado="ejecutada"
elif [[ "$EVENTO" != "merge_group" && "$R_INTEGRACION" == "skipped" \
        && "$R_ALCANCE" == "success" && "$ALCANCE_INTEGRACION" == "false" ]]; then
  estado="saltada"
elif [[ "$EVENTO" == "merge_group" ]]; then
  rojo "En el grupo de fusión los tests de integración tienen que terminar en success (terminaron en «${R_INTEGRACION:-vacío}»)."
elif [[ "$R_INTEGRACION" == "skipped" ]]; then
  rojo "Los tests de integración se saltaron sin que el alcance del CI lo decidiera (alcance: «${R_ALCANCE:-vacío}», integracion=«${ALCANCE_INTEGRACION:-vacío}»)."
else
  rojo "Los tests de integración no terminaron en success (terminaron en «${R_INTEGRACION:-vacío}»)."
fi

if [[ "$estado" == "saltada" ]]; then
  echo "Integración saltada por el alcance del CI: corre en el grupo de fusión. Los pasos que leen sus artefactos (reparto, recuento, cobertura y trinquete del núcleo) no se ejecutan en este run."
else
  echo "Todas las dependencias terminaron en success."
fi
if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
  echo "integracion=$estado" >> "$GITHUB_OUTPUT"
fi
echo "integracion=$estado"
