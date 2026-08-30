#!/usr/bin/env bash
# Backup del stack local (deploy/local/) al Storage Box de Hetzner con Borg.
#
# Sustituye al BackupHostedService (apagado en esta etapa con
# Backups__Activo=false): el Storage Box no habla S3, así que el backup corre
# como cron del host. Mantiene la invariante de RUNBOOK-CLAVES.md — el volcado
# de la BD y dataprotection-keys/ van SIEMPRE en el mismo archivo de backup
# (restaurar la BD con claves de otro momento deja las credenciales cifradas
# de Empresa/Subcontrata irrecuperables) — y añade lo que el servicio antiguo
# no cubría: los PDFs de /data/documentos.
#
# Requisitos: borg en el host, el repo Borg ya inicializado
# (`borg init --encryption=repokey-blake2 "$BORG_REPO"`) y los contenedores
# caemanager-app / caemanager-db del compose levantados.
#
# Uso (cron diario recomendado, ver RUNBOOK-DESPLIEGUE-LOCAL.md § Backups):
#   BORG_REPO='ssh://uXXXXXX@uXXXXXX.your-storagebox.de:23/./backups/caemanager' \
#   BORG_PASSPHRASE='...' \
#   BETTERSTACK_HEARTBEAT_URL='...' \
#   ./scripts/backup-borg.sh [--check]
#
# BETTERSTACK_HEARTBEAT_URL es opcional (Horizonte 2.4 del plan macro,
# dead man's switch): sin ella el script funciona exactamente igual que
# antes, solo que nadie externo nota si el cron deja de ejecutarse (ver
# el bloque al final del script).
set -euo pipefail

: "${BORG_REPO:?Define BORG_REPO (ssh://uXXXXXX@uXXXXXX.your-storagebox.de:23/./backups/caemanager)}"
: "${BORG_PASSPHRASE:?Define BORG_PASSPHRASE (la passphrase del repo Borg)}"
export BORG_REPO BORG_PASSPHRASE

DIR_TRABAJO="$(mktemp -d)"
trap 'rm -rf "$DIR_TRABAJO"' EXIT

echo "==> 1/4 Volcando PostgreSQL (pg_dump --format=custom, dentro del contenedor db)..."
docker exec caemanager-db pg_dump -U postgres --format=custom caemanager \
    > "$DIR_TRABAJO/CaeManager.dump"
[ -s "$DIR_TRABAJO/CaeManager.dump" ] || { echo "ERROR: el dump salió vacío"; exit 1; }

echo "==> 2/4 Copiando dataprotection-keys/ y documentos/ del volumen..."
docker cp caemanager-app:/data/dataprotection-keys "$DIR_TRABAJO/dataprotection-keys"
# Sin claves no hay backup válido — mismo criterio que el BackupHostedService.
ls "$DIR_TRABAJO/dataprotection-keys"/*.xml >/dev/null 2>&1 \
    || { echo "ERROR: dataprotection-keys/ no contiene ninguna clave XML — ver RUNBOOK-CLAVES.md"; exit 1; }
# documentos/ puede no existir aún (nadie subió un PDF todavía) — eso sí es válido.
docker cp caemanager-app:/data/documentos "$DIR_TRABAJO/documentos" 2>/dev/null \
    || mkdir "$DIR_TRABAJO/documentos"

ARCHIVO="caemanager-$(date -u +%Y-%m-%dT%H-%M-%S)"
echo "==> 3/4 borg create ::$ARCHIVO ..."
(cd "$DIR_TRABAJO" && borg create --stats --compression zstd \
    "::$ARCHIVO" CaeManager.dump dataprotection-keys documentos)

echo "==> 4/4 borg prune (7 diarios / 4 semanales / 6 mensuales) + compact..."
borg prune --glob-archives 'caemanager-*' \
    --keep-daily 7 --keep-weekly 4 --keep-monthly 6
borg compact

# --check (p. ej. en el cron semanal): verificación de integridad del repo.
if [ "${1:-}" = "--check" ]; then
    echo "==> borg check..."
    borg check
fi

echo "BACKUP COMPLETADO: $ARCHIVO"
echo "Ensayo de restauración periódico: scripts/ensayo-restauracion-borg.sh (anotar en ENSAYO-RESTAURACION.md, repositorio de negocio)."

# Dead man's switch (Horizonte 2.4 del plan macro): un cron que deja de
# ejecutarse (host caído, systemd-timer borrado sin querer, el propio script
# roto por una actualización) no puede reportar su propio fallo — por
# definición, no hay proceso vivo que lo intente. La única forma de detectar
# "el backup de hoy no corrió" es que algo EXTERNO note la AUSENCIA de una
# señal, no que este script reporte un error. Better Stack (ya en uso para
# el uptime check externo, ver RUNBOOK-HORIZONTE-0.md § 0.3) tiene monitores
# de tipo heartbeat para exactamente esto: una URL secreta que espera un GET
# dentro de una ventana (p. ej. 26 h para un cron diario, con margen); si no
# llega a tiempo, Better Stack alerta igual que ante una caída real.
#
# Se llama aquí, al final, después de "BACKUP COMPLETADO" — con
# "set -euo pipefail" activo, cualquier fallo anterior (dump vacío, sin
# claves, borg create roto) ya habría terminado el script antes de llegar a
# esta línea, así que el ping nunca llega y la ausencia es exactamente la
# señal de fallo que Better Stack tiene que detectar. No hace de esto un
# fallo del backup en sí: el backup ya terminó bien si llegamos aquí: un
# fallo de red puntual al avisar no debe hacer que el cron reintente un
# backup que ya se completó.
#
# Opcional y apagado por defecto, mismo patrón "inerte sin configurar" que
# el resto de integraciones de este despliegue (Sentry, KMS, Backups a S3
# de la etapa Railway): sin BETTERSTACK_HEARTBEAT_URL no se hace ninguna
# llamada de red.
if [ -n "${BETTERSTACK_HEARTBEAT_URL:-}" ]; then
    echo "==> Avisando al heartbeat de Better Stack..."
    curl -fsS -m 10 --retry 3 --retry-delay 5 "$BETTERSTACK_HEARTBEAT_URL" >/dev/null \
        || echo "AVISO: el backup terminó bien pero el ping a Better Stack falló (red caída, URL mal puesta) — revisar a mano; si se repite mañana, Better Stack alertará igualmente por la ausencia."
fi
