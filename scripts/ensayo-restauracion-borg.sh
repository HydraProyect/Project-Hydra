#!/usr/bin/env bash
# Ensayo de restauración del backup Borg (equivalente post-Railway de
# scripts/ensayo-restauracion.sh, que ensayaba contra el bucket S3 del
# BackupHostedService).
#
# Extrae el último archivo `caemanager-*` del repo Borg del Storage Box y lo
# restaura de extremo a extremo contra un PostgreSQL desechable en Docker, SIN
# tocar el stack real: pg_restore del dump, filas en las tablas núcleo, al
# menos una clave XML de Data Protection y recuento de PDFs restaurados. El
# resultado (fecha + resultado) se anota en ENSAYO-RESTAURACION.md, en el
# repositorio de negocio.
#
# Requisitos: borg y docker en el host. pg_restore corre DENTRO del contenedor
# desechable (postgres:18), así que no hace falta cliente 18 en el host.
#
# Uso:
#   BORG_REPO='ssh://uXXXXXX@uXXXXXX.your-storagebox.de:23/./backups/caemanager' \
#   BORG_PASSPHRASE='...' ./scripts/ensayo-restauracion-borg.sh
set -euo pipefail

: "${BORG_REPO:?Define BORG_REPO (ssh://uXXXXXX@uXXXXXX.your-storagebox.de:23/./backups/caemanager)}"
: "${BORG_PASSPHRASE:?Define BORG_PASSPHRASE (la passphrase del repo Borg)}"
export BORG_REPO BORG_PASSPHRASE

DIR_TRABAJO="$(mktemp -d)"
trap 'rm -rf "$DIR_TRABAJO"; docker rm -f ensayo-restauracion-pg >/dev/null 2>&1 || true' EXIT

echo "==> 1/5 Localizando el archivo más reciente en $BORG_REPO ..."
ULTIMO=$(borg list --glob-archives 'caemanager-*' --short | sort | tail -1)
[ -n "$ULTIMO" ] || { echo "ERROR: no hay archivos caemanager-* en el repo Borg"; exit 1; }
echo "    Archivo elegido: $ULTIMO"

echo "==> 2/5 Extrayendo CaeManager.dump + dataprotection-keys + documentos (juntos, del MISMO archivo)..."
(cd "$DIR_TRABAJO" && borg extract "::$ULTIMO")

echo "==> 3/5 Levantando PostgreSQL 18 desechable..."
docker rm -f ensayo-restauracion-pg >/dev/null 2>&1 || true
docker run -d --name ensayo-restauracion-pg -e POSTGRES_PASSWORD=ensayo \
    -e POSTGRES_DB=caemanager postgres:18 >/dev/null
until docker exec ensayo-restauracion-pg pg_isready -U postgres >/dev/null 2>&1; do sleep 1; done

echo "==> 4/5 Restaurando el dump con pg_restore (dentro del contenedor)..."
# --no-privileges: el dump incluye GRANT hacia cae_app_runtime (rol de RLS,
# ver RUNBOOK-RLS.md) que existe en la BD real pero no en este Postgres
# desechable — pg_dump no puede incluir el CREATE ROLE en sí (es un objeto de
# cluster, no de base de datos). Sin este flag, pg_restore falla ~89 GRANT y
# devuelve código de salida no-cero (aunque los DATOS sí se restauran), lo que
# con set -e aborta el script antes de llegar a las verificaciones de la
# sección 5. El ensayo verifica recuperabilidad de datos, no ACLs — replicar
# permisos exactos es un paso aparte si algún día se activa RLS de verdad.
# Bootstrap de roles de clúster ANTES de restaurar. pg_dump no puede incluir
# el CREATE ROLE —es objeto de clúster, no de base—, y desde la reparación del
# 42704 tampoco lo crea ninguna migración. Sin este paso, un clúster restaurado
# no tendría los principales y la aplicación fallaría al migrar.
docker exec -i ensayo-restauracion-pg psql --username=postgres --dbname=postgres     -v ON_ERROR_STOP=1 < "$(dirname "$0")/../deploy/bootstrap/roles-de-cluster.sql"

docker exec -i ensayo-restauracion-pg pg_restore --clean --if-exists --no-owner --no-privileges \
    --username=postgres --dbname=caemanager < "$DIR_TRABAJO/CaeManager.dump"

echo "==> 5/5 Verificaciones..."
comprobar_tabla() {
    local tabla="$1"
    local filas
    filas=$(docker exec ensayo-restauracion-pg psql -U postgres -d caemanager \
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

# RLS sobrevivió a la restauración, no solo los datos. pg_restore incluye
# ENABLE ROW LEVEL SECURITY y CREATE POLICY (son DDL de esquema, no
# privilegios — --no-privileges de arriba no los toca), pero eso nunca se
# había comprobado aquí: un ensayo en verde probaba recuperar FILAS, no que
# el aislamiento por tenant volviera a estar activo. Los roles y su
# membresía (cae_app_runtime / cae_app_soporte) ya los verifica, con
# RAISE EXCEPTION propio, el bootstrap de roles-de-cluster.sql del paso 4/5.
comprobar_rls() {
    local tabla="$1"
    local activa
    activa=$(docker exec ensayo-restauracion-pg psql -U postgres -d caemanager \
        -tAc "SELECT relrowsecurity FROM pg_class WHERE relname = '$tabla' AND relnamespace = 'public'::regnamespace;")
    if [ "$activa" != "t" ]; then
        echo "ERROR: \"$tabla\" no tiene Row Level Security activo tras la restauración (relrowsecurity=$activa)."
        exit 1
    fi
    local politicas
    politicas=$(docker exec ensayo-restauracion-pg psql -U postgres -d caemanager \
        -tAc "SELECT COUNT(*) FROM pg_policies WHERE schemaname = 'public' AND tablename = '$tabla';")
    if [ "${politicas:-0}" -eq 0 ]; then
        echo "ERROR: \"$tabla\" tiene RLS activo pero cero políticas — bloquearía TODO acceso, no solo el cross-tenant."
        exit 1
    fi
    echo "    $tabla: RLS activo, $politicas política(s)"
}
comprobar_rls "Empresas"
comprobar_rls "Documentos"
comprobar_rls "Trabajadores"

CLAVES=$(ls "$DIR_TRABAJO/dataprotection-keys"/*.xml 2>/dev/null | wc -l)
echo "    dataprotection-keys/: $CLAVES archivo(s) de clave"
[ "$CLAVES" -ge 1 ] || { echo "ERROR: el backup no contiene ninguna clave XML — ver RUNBOOK-CLAVES.md (restaurar solo la BD deja las credenciales cifradas irrecuperables)"; exit 1; }

PDFS=$(find "$DIR_TRABAJO/documentos" -type f 2>/dev/null | wc -l)
echo "    documentos/: $PDFS archivo(s) restaurado(s)"

echo ""
echo "ENSAYO COMPLETADO. Pasos manuales restantes (no automatizables desde aquí):"
echo "  1. Arranca la app contra este Postgres con DataProtection__RutaClaves apuntando a"
echo "     $DIR_TRABAJO/dataprotection-keys (o una copia) y comprueba que una Empresa con"
echo "     credenciales guardadas las muestra legibles (RUNBOOK-CLAVES.md § Recuperación, paso 3)."
echo "  2. Anota fecha, archivo usado y resultado en ENSAYO-RESTAURACION.md (repositorio de negocio)."
