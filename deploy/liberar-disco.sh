#!/bin/bash
# Mantiene el disco del VPS por debajo de un umbral antes de construir.
#
# Nace de un incidente real y repetido: el 2026-08-26 y otra vez el 2026-08-29
# el disco llego al 100% y tumbo el servicio. La segunda vez habia 23 GB de
# cache de build de Docker con CERO en uso, acumulada porque el pipeline de
# despliegue no la retiraba nunca.
#
# El sintoma no se parece a "disco lleno": el build falla con errores de NuGet
# ("Central Directory corrupt", "Failed to download package") y, sobre todo,
# PostgreSQL deja de poder escribir y la aplicacion cae en produccion aunque
# nadie haya desplegado nada. Un mensaje claro aqui ahorra ese rodeo.
#
# QUE NO TOCA, NUNCA: los volumenes. Ahi vive caemanager-postgres-data, que es
# la base de datos de produccion. Por eso se usan `builder prune` e
# `image prune` y jamas `system prune --volumes`.
#
# Uso:  liberar-disco.sh [umbral-por-ciento]
# Para poder probarlo sin Docker ni un disco lleno de verdad, admite
# USO_DISCO_FORZADO (porcentaje) y LIBERAR_DISCO_SIMULADO=1.

set -euo pipefail

UMBRAL="${1:-75}"
CRITICO=95

if ! [[ "$UMBRAL" =~ ^[0-9]+$ ]] || [ "$UMBRAL" -lt 1 ] || [ "$UMBRAL" -gt 99 ]; then
  echo "Umbral invalido: '$UMBRAL' — se espera un entero entre 1 y 99." >&2
  exit 2
fi

uso_actual() {
  if [ -n "${USO_DISCO_FORZADO:-}" ]; then
    echo "$USO_DISCO_FORZADO"
  else
    df --output=pcent / | tail -1 | tr -dc '0-9'
  fi
}

liberar() {
  if [ "${LIBERAR_DISCO_SIMULADO:-}" = "1" ]; then
    echo "  (simulado: no se ejecuta docker)"
    return 0
  fi
  # Se conserva la cache reciente: acelera el build de hoy y no es la que
  # llena el disco. Lo que se retira es lo viejo, que no lo usa nadie.
  docker builder prune -af --filter until=24h || true
  docker image prune -af --filter until=168h || true
}

uso=$(uso_actual)
echo "Uso del disco: ${uso}% (umbral: ${UMBRAL}%)"

if [ "$uso" -ge "$UMBRAL" ]; then
  echo "Por encima del umbral: liberando cache de build e imagenes antiguas."
  echo "Los volumenes NO se tocan: ahi esta la base de datos."
  liberar
  uso=$(uso_actual)
  echo "Uso del disco tras liberar: ${uso}%"
fi

# Si despues de liberar sigue critico, se para AQUI y con un mensaje que se
# entiende, en vez de dejar que el build falle mas adelante con errores de
# NuGet que no mencionan el disco por ninguna parte.
if [ "$uso" -ge "$CRITICO" ]; then
  echo "El disco sigue al ${uso}%, por encima del critico (${CRITICO}%)." >&2
  echo "No se continua con el despliegue: un build en un disco lleno corrompe" >&2
  echo "descargas de NuGet y puede dejar a PostgreSQL sin poder escribir." >&2
  echo "Hace falta intervencion manual — mira 'docker system df' y que ocupa /." >&2
  exit 1
fi

echo "Disco en condiciones para construir."
