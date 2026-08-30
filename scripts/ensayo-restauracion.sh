#!/usr/bin/env bash
# Ensayo de restauración de backup (P0-6 de docs/business/MATURITY_REVIEW.md)
#
# Ejecuta de extremo a extremo la restauración descrita en RUNBOOK-CLAVES.md
# contra un PostgreSQL local desechable, SIN tocar producción: descarga el
# último backup real de S3, restaura el dump con pg_restore, comprueba que
# las tablas núcleo tienen filas y que el zip de claves de Data Protection
# es íntegro y contiene claves. El resultado se anota en
# ENSAYO-RESTAURACION.md, en el repositorio de negocio (fecha + resultado,
# como exige el P0-6).
#
# DEUDA: este guion ensaya la restauración vía S3/aws-cli del antiguo
# BackupHostedService, apagado desde el paso a Hetzner (Backups__Activo=false
# en docker-compose.produccion.yml — ver ese archivo). El mecanismo de
# backup real hoy es Borg (scripts/backup-borg.sh +
# scripts/ensayo-restauracion-borg.sh); este script queda como referencia
# para si S3 se reactivase, no como parte del ensayo periódico actual.
#
# Requisitos: aws cli con credenciales del bucket de backups, docker (para el
# Postgres desechable) y pg_restore >= versión del servidor.
#
# Uso:
#   BUCKET=project-hydra-backups SERVICIO=Project-Hydra ./scripts/ensayo-restauracion.sh
set -euo pipefail

BUCKET="${BUCKET:?Define BUCKET (nombre del bucket S3 de backups)}"
SERVICIO="${SERVICIO:?Define SERVICIO (prefijo RAILWAY_SERVICE_NAME dentro del bucket)}"
PUERTO_LOCAL="${PUERTO_LOCAL:-55432}"
DIR_TRABAJO="$(mktemp -d)"
trap 'rm -rf "$DIR_TRABAJO"; docker rm -f ensayo-restauracion-pg >/dev/null 2>&1 || true' EXIT

echo "==> 1/5 Localizando el backup más reciente en s3://$BUCKET/$SERVICIO/ ..."
ULTIMO_PREFIJO=$(aws s3 ls "s3://$BUCKET/$SERVICIO/" | awk '{print $2}' | sort | tail -1)
[ -n "$ULTIMO_PREFIJO" ] || { echo "ERROR: no hay backups bajo s3://$BUCKET/$SERVICIO/"; exit 1; }
echo "    Backup elegido: $ULTIMO_PREFIJO"

echo "==> 2/5 Descargando CaeManager.dump + dataprotection-keys.zip (juntos, del MISMO backup)..."
aws s3 cp "s3://$BUCKET/$SERVICIO/${ULTIMO_PREFIJO}CaeManager.dump" "$DIR_TRABAJO/CaeManager.dump"
aws s3 cp "s3://$BUCKET/$SERVICIO/${ULTIMO_PREFIJO}dataprotection-keys.zip" "$DIR_TRABAJO/dataprotection-keys.zip"

echo "==> 3/5 Levantando PostgreSQL desechable en el puerto $PUERTO_LOCAL..."
docker rm -f ensayo-restauracion-pg >/dev/null 2>&1 || true
docker run -d --name ensayo-restauracion-pg -e POSTGRES_PASSWORD=ensayo \
    -e POSTGRES_DB=caemanager -p "$PUERTO_LOCAL:5432" postgres:17 >/dev/null
until docker exec ensayo-restauracion-pg pg_isready -U postgres >/dev/null 2>&1; do sleep 1; done

# Bootstrap de roles de clúster ANTES de restaurar. pg_dump no puede incluir el
# CREATE ROLE —es objeto de clúster, no de base— y desde la reparación del 42704
# tampoco lo crea ninguna migración. Sin este paso, un clúster restaurado no
# tendría los principales y la aplicación fallaría al migrar.
PGPASSWORD=ensayo psql -h localhost -p "$PUERTO_LOCAL" -U postgres -d postgres     -v ON_ERROR_STOP=1 -f "$(dirname "$0")/../deploy/bootstrap/roles-de-cluster.sql"

echo "==> 4/5 Restaurando el dump con pg_restore..."
# --no-privileges: mismo motivo que en ensayo-restauracion-borg.sh — el dump
# referencia el rol cae_app_runtime (RLS, RUNBOOK-RLS.md), que no existe en
# este Postgres desechable y no puede incluirse en un dump de una sola BD.
PGPASSWORD=ensayo pg_restore --clean --if-exists --no-owner --no-privileges \
    --host=localhost --port="$PUERTO_LOCAL" --username=postgres --dbname=caemanager \
    "$DIR_TRABAJO/CaeManager.dump"

echo "==> 5/5 Verificaciones..."
comprobar_tabla() {
    local tabla="$1"
    local filas
    filas=$(PGPASSWORD=ensayo psql -h localhost -p "$PUERTO_LOCAL" -U postgres -d caemanager \
        -tAc "SELECT COUNT(*) FROM \"$tabla\";")
    echo "    $tabla: $filas filas"
}
# Tablas núcleo: si alguna no existe, pg_restore no restauró el esquema completo y el script falla aquí.
# F3c (2026-08-28) retiró "Clientes": toda contraparte es hoy una fila de
# "Empresas", que ya está en esta lista. Comprobarla seguiría abortando el
# ensayo con set -e sobre una restauración correcta — un falso negativo de
# backup, que es justo lo contrario de lo que este guion existe para detectar.
comprobar_tabla "Empresas"
comprobar_tabla "Trabajadores"
comprobar_tabla "Documentos"
comprobar_tabla "AspNetUsers"
comprobar_tabla "Tenants"

# Integridad del zip de claves + que contenga al menos una clave XML.
unzip -t "$DIR_TRABAJO/dataprotection-keys.zip" >/dev/null
CLAVES=$(unzip -l "$DIR_TRABAJO/dataprotection-keys.zip" | grep -c '\.xml' || true)
echo "    dataprotection-keys.zip: íntegro, $CLAVES archivo(s) de clave"
[ "$CLAVES" -ge 1 ] || { echo "ERROR: el zip de claves no contiene ninguna clave XML — ver RUNBOOK-CLAVES.md (restaurar solo la BD deja las credenciales cifradas irrecuperables)"; exit 1; }

echo ""
echo "ENSAYO COMPLETADO. Pasos manuales restantes (no automatizables desde aquí):"
echo "  1. Arranca la app contra este Postgres (ConnectionStrings__CaeManagerDb=Host=localhost;Port=$PUERTO_LOCAL;Database=caemanager;Username=postgres;Password=ensayo)"
echo "     con DataProtection__RutaClaves apuntando al contenido descomprimido del zip."
echo "  2. Inicia sesión como Administrador y abre una Empresa con credenciales guardadas:"
echo "     deben verse legibles (RUNBOOK-CLAVES.md § Recuperación, paso 3)."
echo "  3. Anota fecha, backup usado y resultado en ENSAYO-RESTAURACION.md (repositorio de negocio)."
