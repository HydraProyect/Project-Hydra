#!/usr/bin/env bash
# Exportador de datos crudos para el "análisis de incidentes" de §6.5 del
# MACRO_PLAN (IA operando la plataforma, IA operando la plataforma — interno,
# sin exposición a cliente): descarga los issues de Sentry y los eventos de
# error de Seq de los últimos N días y los deja en disco como JSON crudo.
#
# Este script SOLO fetchea y guarda — no analiza nada, no escribe ningún
# informe, no abre ningún issue ni PR. El análisis (hipótesis de causa raíz +
# enlace al código, formato auditoría-adversarial) lo hace un agente después,
# siguiendo tecnico/RUNBOOK-ANALISIS-INCIDENTES-IA.md del repo de negocio
# (Project-Hydra-Negocio) contra la salida de este script.
#
# Deliberadamente NO está enganchado a ningún cron/scheduler — es una
# herramienta que alguien invoca a mano cuando quiere la pasada semanal. Ver
# el runbook de negocio para el interruptor de automatización real (sin
# activar todavía).
#
# Requisitos de credenciales (ninguna se inventa aquí, hay que provisionarlas
# — mismo patrón que Stripe/Anthropic en el resto del plan):
#   SENTRY_AUTH_TOKEN   Token de "Internal Integration" en sentry.io con scope
#                        de lectura (project:read, event:read). Distinto de
#                        `Sentry:Dsn` (ese es solo de escritura, para que la
#                        app reporte errores — no sirve para leer).
#   SENTRY_ORG           Slug de la organización en Sentry.
#   SENTRY_PROJECT        Slug del proyecto en Sentry.
#   SEQ_SERVER_URL / SEQ_API_KEY
#                        Si no están definidas, se reutilizan
#                        Serilog__Seq__ServerUrl / Serilog__Seq__ApiKey (las
#                        mismas variables que ya usa Program.cs en
#                        producción) para no duplicar configuración.
#
# Estado de partida honesto (2026-08-15): Sentry y Seq siguen "inertes" en el
# sentido de RUNBOOK-HORIZONTE-0.md — 0.3 ("Encender el monitoreo ya
# escrito") todavía no se ha ejecutado, así que hoy no hay DSN de Sentry ni
# URL de Seq reales contra las que probar este script de punta a punta. La
# llamada a Seq en concreto usa la API HTTP clásica de eventos
# (`/api/events`, ver https://docs.datalust.co/docs/the-http-api) con el
# filtro construido a mano más abajo — NO se ha podido verificar contra una
# instancia real por lo anterior; la primera vez que se ejecute con una URL
# real, revisar que el nombre de los parámetros sigue siendo el mismo en la
# versión de Seq desplegada y ajustar si hace falta.
#
# Uso:
#   SENTRY_AUTH_TOKEN=... SENTRY_ORG=... SENTRY_PROJECT=... \
#   SEQ_SERVER_URL=... SEQ_API_KEY=... \
#   ./scripts/exportar-incidentes-semanales.sh [--dias 7] [--salida DIR]
#
# Salida: DIR/sentry-issues.json, DIR/sentry-issues-detalle/<id>.json
#         (evento más reciente de cada issue, con stacktrace),
#         DIR/seq-errores.json, DIR/resumen.txt
# Por defecto DIR es un directorio temporal nuevo (se imprime la ruta al
# terminar) — deliberadamente NO se comitea nada de esto en ningún repo: son
# datos crudos de error que pueden contener fragmentos de payloads reales.

set -euo pipefail

DIAS=7
SALIDA=""

while [ "$#" -gt 0 ]; do
  case "$1" in
    --dias) DIAS="$2"; shift 2 ;;
    --salida) SALIDA="$2"; shift 2 ;;
    *) echo "Argumento desconocido: $1" >&2; exit 1 ;;
  esac
done

: "${SENTRY_AUTH_TOKEN:?Define SENTRY_AUTH_TOKEN (Internal Integration token de sentry.io con scope project:read+event:read)}"
: "${SENTRY_ORG:?Define SENTRY_ORG (slug de la organización en Sentry)}"
: "${SENTRY_PROJECT:?Define SENTRY_PROJECT (slug del proyecto en Sentry)}"

SEQ_SERVER_URL="${SEQ_SERVER_URL:-${Serilog__Seq__ServerUrl:-}}"
SEQ_API_KEY="${SEQ_API_KEY:-${Serilog__Seq__ApiKey:-}}"

if [ -z "$SALIDA" ]; then
  SALIDA="$(mktemp -d -t incidentes-semanales-XXXXXX)"
else
  mkdir -p "$SALIDA"
fi
mkdir -p "$SALIDA/sentry-issues-detalle"

echo "==> Descargando issues de Sentry (últimos ${DIAS}d, proyecto ${SENTRY_ORG}/${SENTRY_PROJECT})..."
curl -sS -f \
  -H "Authorization: Bearer ${SENTRY_AUTH_TOKEN}" \
  "https://sentry.io/api/0/projects/${SENTRY_ORG}/${SENTRY_PROJECT}/issues/?statsPeriod=${DIAS}d&query=is:unresolved&sort=freq&limit=25" \
  -o "$SALIDA/sentry-issues.json" \
  || { echo "ERROR: fallo al listar issues de Sentry — revisa SENTRY_AUTH_TOKEN/SENTRY_ORG/SENTRY_PROJECT" >&2; exit 1; }

TOTAL_ISSUES=$(grep -o '"id"' "$SALIDA/sentry-issues.json" | wc -l | tr -d ' ')
echo "    ${TOTAL_ISSUES} issues encontrados (top 25 por frecuencia)."

echo "==> Descargando el evento más reciente de cada issue (con stacktrace)..."
# Extracción de IDs sin depender de jq (no está garantizado en el entorno):
# los IDs de issue de Sentry son numéricos y aparecen como "id":"1234567890".
IDS=$(grep -oP '"id":"\d+"' "$SALIDA/sentry-issues.json" | grep -oP '\d+' | sort -u)
for id in $IDS; do
  curl -sS -f \
    -H "Authorization: Bearer ${SENTRY_AUTH_TOKEN}" \
    "https://sentry.io/api/0/issues/${id}/events/latest/" \
    -o "$SALIDA/sentry-issues-detalle/${id}.json" \
    || echo "    AVISO: no se pudo descargar el detalle del issue ${id} (se continúa)" >&2
done

if [ -n "$SEQ_SERVER_URL" ]; then
  echo "==> Descargando eventos de error de Seq (últimos ${DIAS}d)..."
  DESDE_UTC=$(date -u -d "-${DIAS} days" +%Y-%m-%dT%H:%M:%SZ 2>/dev/null || date -u -v-${DIAS}d +%Y-%m-%dT%H:%M:%SZ)
  # Filtro en Seq Query Language: nivel Error o Fatal, desde DESDE_UTC.
  # Ver el aviso de cabecera: parámetros no verificados contra una instancia real.
  FILTRO="(@Level = 'Error' or @Level = 'Fatal') and @Timestamp >= DateTime.Parse(\"${DESDE_UTC}\")"
  FILTRO_CODIFICADO=$(python3 -c "import urllib.parse,sys; print(urllib.parse.quote(sys.argv[1]))" "$FILTRO" 2>/dev/null \
    || node -e "process.stdout.write(encodeURIComponent(process.argv[1]))" "$FILTRO")
  SEQ_HEADERS=(-H "Accept: application/json")
  if [ -n "$SEQ_API_KEY" ]; then
    SEQ_HEADERS+=(-H "X-Seq-ApiKey: ${SEQ_API_KEY}")
  fi
  curl -sS -f "${SEQ_HEADERS[@]}" \
    "${SEQ_SERVER_URL%/}/api/events?filter=${FILTRO_CODIFICADO}&count=500&render=true" \
    -o "$SALIDA/seq-errores.json" \
    || echo "    AVISO: fallo al consultar Seq — revisa SEQ_SERVER_URL/SEQ_API_KEY y la sintaxis del filtro para tu versión de Seq (se continúa solo con Sentry)" >&2
else
  echo "==> SEQ_SERVER_URL no definida (ni Serilog__Seq__ServerUrl) — se omite Seq. Esto es exactamente lo esperado mientras Horizonte 0.3 siga sin ejecutarse." | tee "$SALIDA/seq-errores.json.SKIPPED"
fi

{
  echo "Exportación de incidentes — $(date -u +%Y-%m-%dT%H:%M:%SZ)"
  echo "Ventana: últimos ${DIAS} días"
  echo "Proyecto Sentry: ${SENTRY_ORG}/${SENTRY_PROJECT}"
  echo "Issues descargados: ${TOTAL_ISSUES}"
  echo "Seq consultado: $([ -n "$SEQ_SERVER_URL" ] && echo sí || echo "no (SEQ_SERVER_URL vacía)")"
  echo
  echo "Siguiente paso: pega la ruta de este directorio en una sesión de"
  echo "Claude Code junto con el prompt de"
  echo "tecnico/RUNBOOK-ANALISIS-INCIDENTES-IA.md (repo de negocio)."
} > "$SALIDA/resumen.txt"

cat "$SALIDA/resumen.txt"
echo
echo "==> Datos crudos en: $SALIDA"
