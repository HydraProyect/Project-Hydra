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
#   BORG_PASSPHRASE='...' ./scripts/backup-borg.sh [--check]
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
echo "Ensayo de restauración periódico: scripts/ensayo-restauracion-borg.sh (anotar en docs/ENSAYO-RESTAURACION.md)."
