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
  # Cache de build: se conserva la reciente (acelera el build de hoy), se
  # retira la de mas de 24h.
  docker builder prune -af --filter until=24h || true

  # Imagenes: NUNCA por antiguedad. El incidente del 2026-09-13 fue esto
  # exacto: 88 imagenes/26GB acumuladas porque `--filter until=168h` solo
  # libera lo que tiene mas de 7 dias, y con despliegues mas frecuentes que
  # eso una imagen sin uso nunca llega a envejecer lo bastante para calificar
  # — se acumulan indefinidamente sin que este guion las vea nunca.
  #
  # `-a` sin filtro de edad ya implica "sin usar": Docker nunca deja que
  # `image prune` borre una imagen referenciada por un contenedor (parado o
  # corriendo), y este guion corre SIEMPRE antes de `docker compose build`
  # (ver ci-deploy.sh) — el contenedor que sigue sirviendo trafico ahora
  # mismo sigue apuntando a la imagen actual, que por tanto esta en uso y
  # queda protegida sin que este guion tenga que llevar la cuenta de cual es.
  #
  # No hay ninguna imagen vieja que el rollback necesite conservar: F3
  # (tecnico/f3-analisis-pipeline-y-rollback-2026-08-25.md, repositorio de
  # negocio) deja escrito que no existe rollback automatico de aplicacion —
  # "volver a una version anterior" es volver a desplegar un SHA anterior,
  # que reconstruye la imagen desde el Dockerfile de ese commit, no reutiliza
  # una imagen local ya construida. Ademas Compose no genera un tag por
  # version (build sin `image:` propio): cada build sustituye el mismo tag,
  # asi que no hay forma de identificar "las N anteriores" por nombre — solo
  # existirian como IDs sueltos, sin ninguna referencia que las use.
  #
  # Tradeoff aceptado, revisado por Codex: el Dockerfile es multi-stage
  # (`AS build` / `AS final`) y el build corre con DOCKER_BUILDKIT=0 (ver
  # ci-deploy.sh) — con el builder clasico, la imagen intermedia de la etapa
  # `build` queda sin tag (dangling) igual que las imagenes viejas de verdad,
  # asi que esta poda tambien se la lleva por delante cuando corre. Es
  # exactamente la misma cache que `docker builder prune` no toca (esa orden
  # gestiona la cache de BuildKit, vacia aqui porque el build no lo usa). El
  # siguiente build pierde ese calentamiento y tarda mas — no es un problema
  # de correccion ni de rollback, y solo ocurre cuando el disco ya cruzo el
  # umbral: en ese momento, un build mas lento es preferible a repetir el
  # incidente de disco.
  docker image prune -af || true
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
