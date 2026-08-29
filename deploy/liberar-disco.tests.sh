#!/bin/bash
# Tests de deploy/liberar-disco.sh
#
# Se inyecta el uso del disco con USO_DISCO_FORZADO y se simula Docker con
# LIBERAR_DISCO_SIMULADO, asi que corren en CI sin Docker, sin VPS y sin
# llenar ningun disco de verdad.

set -uo pipefail

AQUI=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
GUION="$AQUI/liberar-disco.sh"

fallos=0
comprobar() {
  local caso=$1 esperado=$2 obtenido=$3
  if [ "$esperado" = "$obtenido" ]; then
    echo "  ok   $caso"
  else
    echo "  FALLO $caso"
    echo "        esperado: $esperado"
    echo "        obtenido: $obtenido"
    fallos=$((fallos + 1))
  fi
}

echo "liberar-disco.sh"

# Por debajo del umbral: no se libera nada y se sigue adelante.
salida=$(USO_DISCO_FORZADO=40 LIBERAR_DISCO_SIMULADO=1 bash "$GUION" 75 2>&1)
comprobar "40% con umbral 75 no libera" "0" "$?"
case "$salida" in
  *"liberando"*) comprobar "40% no menciona liberar" "no" "si" ;;
  *) comprobar "40% no menciona liberar" "no" "no" ;;
esac

# Por encima del umbral: libera, y si tras liberar esta bien, continua.
salida=$(USO_DISCO_FORZADO=80 LIBERAR_DISCO_SIMULADO=1 bash "$GUION" 75 2>&1)
codigo=$?
comprobar "80% con umbral 75 libera y continua" "0" "$codigo"
case "$salida" in
  *"liberando"*) comprobar "80% si libera" "si" "si" ;;
  *) comprobar "80% si libera" "si" "no" ;;
esac

# El caso del incidente: disco al 100%. Como la simulacion no libera nada,
# sigue critico y TIENE que parar — no dejar que el build falle luego con
# errores de NuGet que no hablan del disco.
USO_DISCO_FORZADO=100 LIBERAR_DISCO_SIMULADO=1 bash "$GUION" 75 >/dev/null 2>&1
comprobar "100% y sin poder liberar, se detiene" "1" "$?"

salida=$(USO_DISCO_FORZADO=100 LIBERAR_DISCO_SIMULADO=1 bash "$GUION" 75 2>&1 >/dev/null)
case "$salida" in
  *"por encima del critico"*) comprobar "y lo dice por el disco, no por otra cosa" "si" "si" ;;
  *) comprobar "y lo dice por el disco, no por otra cosa" "si" "no ($salida)" ;;
esac

# Justo en el umbral tambien libera: >= y no >.
salida=$(USO_DISCO_FORZADO=75 LIBERAR_DISCO_SIMULADO=1 bash "$GUION" 75 2>&1)
case "$salida" in
  *"liberando"*) comprobar "justo en el umbral libera" "si" "si" ;;
  *) comprobar "justo en el umbral libera" "si" "no" ;;
esac

# 94 no es critico todavia; 95 si. La frontera importa porque decide entre
# seguir desplegando y parar.
USO_DISCO_FORZADO=94 LIBERAR_DISCO_SIMULADO=1 bash "$GUION" 99 >/dev/null 2>&1
comprobar "94% no es critico" "0" "$?"
USO_DISCO_FORZADO=95 LIBERAR_DISCO_SIMULADO=1 bash "$GUION" 99 >/dev/null 2>&1
comprobar "95% si es critico" "1" "$?"

# Umbrales invalidos: error de uso, no un despliegue a ciegas.
USO_DISCO_FORZADO=40 LIBERAR_DISCO_SIMULADO=1 bash "$GUION" 0 >/dev/null 2>&1
comprobar "umbral 0 es invalido" "2" "$?"
USO_DISCO_FORZADO=40 LIBERAR_DISCO_SIMULADO=1 bash "$GUION" abc >/dev/null 2>&1
comprobar "umbral no numerico es invalido" "2" "$?"

# NUNCA debe aparecer una orden que borre volumenes: ahi vive la base de datos.
#
# Se miran solo las lineas EJECUTABLES: el guion menciona `system prune` y
# `--volumes` en un comentario, justamente para advertir de que no se usan, y
# un grep sobre el fichero entero daria un falso positivo sobre esa advertencia.
if sed 's/#.*//' "$GUION" | grep -qE "system prune|--volumes|volume (rm|prune)"; then
  comprobar "no contiene ninguna orden que borre volumenes" "si" "no"
else
  comprobar "no contiene ninguna orden que borre volumenes" "si" "si"
fi

if [ "$fallos" -gt 0 ]; then
  echo "$fallos comprobacion(es) fallaron."
  exit 1
fi
echo "Todas las comprobaciones pasaron."
