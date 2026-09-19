#!/usr/bin/env bash
# Backup del stack local (deploy/local/) al Storage Box de Hetzner con Borg.
#
# Único mecanismo de backup automático: el Storage Box no habla S3, así que
# el backup corre como cron del host, no como servicio dentro de la app.
# Mantiene la invariante de RUNBOOK-CLAVES.md — el volcado
# de la BD y dataprotection-keys/ van SIEMPRE en el mismo archivo de backup
# (restaurar la BD con claves de otro momento deja las credenciales cifradas
# de Empresa/Subcontrata irrecuperables) — y añade lo que el servicio antiguo
# no cubría: los PDFs de /data/documentos y el .env de producción (archivo
# env-produccion, 0600, cifrado por Borg; ver el bloque correspondiente).
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
# el bloque al final del script). Con ella, además del ping de éxito, un
# fallo del propio script avisa al momento con `<url>/fail` en vez de esperar a
# que venza la ventana del monitor (P36).
set -euo pipefail

: "${BORG_REPO:?Define BORG_REPO (ssh://uXXXXXX@uXXXXXX.your-storagebox.de:23/./backups/caemanager)}"
: "${BORG_PASSPHRASE:?Define BORG_PASSPHRASE (la passphrase del repo Borg)}"
export BORG_REPO BORG_PASSPHRASE

DIR_TRABAJO="$(mktemp -d)"

# La URL del heartbeat es un secreto (quien la conozca puede fingir que el backup
# corrió): va por stdin con `curl -K -`, no como argumento, para que no salga en
# `ps` ni en /proc/<pid>/cmdline.
llamar_heartbeat() {   # llamar_heartbeat URL [opciones de curl]
    local url="$1"; shift
    printf 'url = "%s"\n' "$url" | curl -fsS -m 10 "$@" -K -
}

# Un único manejador de salida: limpia el directorio de trabajo y, si el
# script termina con error (dump vacío, sin claves, borg roto, `exit 1` de
# cualquiera de las guardas), avisa al heartbeat con `/fail` para que el
# incidente se abra ya y no cuando venza la ventana de espera. Es un AVISO
# ADICIONAL: la señal que no se puede perder sigue siendo la AUSENCIA del ping
# de éxito (un host caído no puede llamar a nadie), que cubre lo que este
# manejador no puede — que el propio script ni llegue a arrancar.
al_salir() {
    local codigo=$?
    rm -rf "$DIR_TRABAJO"
    if [ "$codigo" -ne 0 ] && [ -n "${BETTERSTACK_HEARTBEAT_URL:-}" ]; then
        llamar_heartbeat "${BETTERSTACK_HEARTBEAT_URL%/}/fail" --retry 2 --retry-delay 3 >/dev/null 2>&1 \
            || echo "AVISO: el backup FALLÓ y tampoco se pudo avisar al heartbeat (/fail) — la ausencia del ping de éxito lo avisará igualmente."
    fi
    return "$codigo"
}
trap al_salir EXIT

echo "==> 1/4 Volcando PostgreSQL (pg_dump --format=custom, dentro del contenedor db)..."
docker exec caemanager-db pg_dump -U postgres --format=custom caemanager \
    > "$DIR_TRABAJO/CaeManager.dump"
[ -s "$DIR_TRABAJO/CaeManager.dump" ] || { echo "ERROR: el dump salió vacío"; exit 1; }

echo "==> 2/4 Copiando dataprotection-keys/ y documentos/ del volumen..."
docker cp caemanager-app:/data/dataprotection-keys "$DIR_TRABAJO/dataprotection-keys"
# Sin claves no hay backup válido.
ls "$DIR_TRABAJO/dataprotection-keys"/*.xml >/dev/null 2>&1 \
    || { echo "ERROR: dataprotection-keys/ no contiene ninguna clave XML — ver RUNBOOK-CLAVES.md"; exit 1; }
# documentos/ puede no existir aún (nadie subió un PDF todavía) — eso sí es válido.
docker cp caemanager-app:/data/documentos "$DIR_TRABAJO/documentos" 2>/dev/null \
    || mkdir "$DIR_TRABAJO/documentos"

# El .env de producción (contraseña de PostgreSQL, la del rol cae_app_runtime,
# DSN de Sentry, claves de IA y demás secretos) entra en el MISMO archivo Borg
# (decisión del propietario, continuidad n.º 26): sin él, perder el disco del
# servidor obliga a regenerar todos los secretos a mano y entra en el RTO. Va
# cifrado por Borg (repokey-blake2), como el resto del archivo. El contenido
# NUNCA se imprime ni pasa por argumentos: solo se copia con permisos 0600.
# Si falta o está vacío el backup FALLA (avisa con /fail) en vez de omitirlo en
# silencio: un backup que parece bueno y no lleva el .env es un falso verde.
# Salida deliberada: BACKUP_SIN_ENV=1.
if [ "${BACKUP_SIN_ENV:-0}" = "1" ]; then
    echo "AVISO: BACKUP_SIN_ENV=1 — este archivo NO incluye el .env; restaurar el servidor exigirá regenerar todos los secretos a mano."
else
    ENV_ORIGEN="${ENV_PRODUCCION:-$(cd "$(dirname "$0")/.." && pwd)/deploy/local/.env}"
    if [ ! -s "$ENV_ORIGEN" ]; then
        echo "ERROR: no hay .env de producción (o está vacío) en $ENV_ORIGEN."
        echo "       Indica su ruta con ENV_PRODUCCION, o BACKUP_SIN_ENV=1 para omitirlo a sabiendas."
        exit 1
    fi
    install -m 600 "$ENV_ORIGEN" "$DIR_TRABAJO/env-produccion"
    echo "    .env de producción incluido como env-produccion ($(wc -c < "$DIR_TRABAJO/env-produccion") bytes; el contenido no se muestra)"
fi

ARCHIVO="caemanager-$(date -u +%Y-%m-%dT%H-%M-%S)"
echo "==> 3/4 borg create ::$ARCHIVO ..."
CONTENIDO=(CaeManager.dump dataprotection-keys documentos)
[ -f "$DIR_TRABAJO/env-produccion" ] && CONTENIDO+=(env-produccion)
(cd "$DIR_TRABAJO" && borg create --stats --compression zstd \
    "::$ARCHIVO" "${CONTENIDO[@]}")

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
# el resto de integraciones de este despliegue (Sentry, KMS): sin
# BETTERSTACK_HEARTBEAT_URL no se hace ninguna llamada de red.
if [ -n "${BETTERSTACK_HEARTBEAT_URL:-}" ]; then
    echo "==> Avisando al heartbeat de Better Stack..."
    llamar_heartbeat "$BETTERSTACK_HEARTBEAT_URL" --retry 3 --retry-delay 5 >/dev/null \
        || echo "AVISO: el backup terminó bien pero el ping a Better Stack falló (red caída, URL mal puesta) — revisar a mano; si se repite mañana, Better Stack alertará igualmente por la ausencia."
fi
