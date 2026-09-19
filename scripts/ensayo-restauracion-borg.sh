#!/usr/bin/env bash
# Ensayo de restauración del backup Borg, de extremo a extremo (P35/P38).
#
# Restaura el último archivo `caemanager-*` SOBRE UNA COPIA DESECHABLE (un
# PostgreSQL y una app en contenedores efímeros, en una red Docker propia) y
# comprueba que lo restaurado sirve de verdad, sin tocar nunca el stack real:
#   1. la BD restaura y el esquema es compatible con el código (migrador);
#   2. las tablas núcleo tienen filas y RLS sobrevive con sus políticas;
#   3. la app arranca contra la copia con SOLO las claves de Data Protection
#      del backup (no genera un llavero nuevo);
#   4. una cuenta inicia sesión (la cookie se cifra y se lee con las
#      claves restauradas) y un documento cifrado en reposo se descarga
#      descifrado (mismo llavero, otro propósito);
#   5. opcionalmente (--copia-claves) repite 3-4 con la copia cifrada de las
#      claves de la segunda ubicación (P38), sin usar las del archivo Borg.
# Y mide el RPO observado (antigüedad del backup y hueco máximo entre copias)
# y los tiempos por fase, de los que sale el RTO automatizable.
#
# Requisitos: docker (y borg salvo con --desde-dir; age solo con --copia-claves).
# pg_restore corre DENTRO del contenedor de PostgreSQL, sin cliente en el host.
#
# Uso con el backup real (lo ejecuta quien tenga acceso al Storage Box):
#   BORG_REPO='ssh://uXXXXXX@uXXXXXX.your-storagebox.de:23/./backups/caemanager' \
#   BORG_PASSPHRASE='...' \
#   ./scripts/ensayo-restauracion-borg.sh [opciones]
#
# Uso sobre un directorio ya extraído (pruebas, o backup traído a mano):
#   ./scripts/ensayo-restauracion-borg.sh --desde-dir DIR --marca-backup 2026-09-19T03-15-02
#   (DIR contiene CaeManager.dump, dataprotection-keys/ y documentos/)
#
# Opciones:
#   --desde-dir DIR        no usa Borg: DIR ya contiene el backup extraído.
#   --marca-backup TS      con --desde-dir: instante del backup, UTC, en el
#                          formato del nombre del archivo (AAAA-MM-DDTHH-MM-SS).
#   --sin-app              solo BD + claves + PDFs (sin migrador ni app).
#   --copia-claves F.age   segunda ubicación de las claves (P38), cifrada con age.
#   --identidad-age FICH   identidad (clave privada age) para abrir --copia-claves.
# Entorno (opcional):
#   ENSAYO_IMAGEN_APP      imagen de la app; si falta, se construye del Dockerfile
#                          de este árbol.
#   ENSAYO_IMAGEN_PG       imagen de PostgreSQL (por defecto postgres:18).
#   ENSAYO_CUENTA / ENSAYO_CLAVE   (opcionales) cuenta SIN 2FA para el login. Sin
#                          ellas el guion prepara una credencial de ensayo EN LA
#                          COPIA desechable (ver preparar_cuenta). Nunca van en el
#                          repositorio.
#   RPO_MAX_HORAS (24) · RTO_MAX_HORAS (4): objetivos con los que se compara.
set -euo pipefail

DIR_SCRIPT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RAIZ_REPO="$(cd "$DIR_SCRIPT/.." && pwd)"

DESDE_DIR=""
MARCA_BACKUP=""
SIN_APP=0
COPIA_CLAVES=""
IDENTIDAD_AGE=""
while [ $# -gt 0 ]; do
    case "$1" in
        --desde-dir)      DESDE_DIR="${2:?--desde-dir necesita un directorio}"; shift 2 ;;
        --marca-backup)   MARCA_BACKUP="${2:?--marca-backup necesita AAAA-MM-DDTHH-MM-SS}"; shift 2 ;;
        --sin-app)        SIN_APP=1; shift ;;
        --copia-claves)   COPIA_CLAVES="${2:?--copia-claves necesita un fichero .age}"; shift 2 ;;
        --identidad-age)  IDENTIDAD_AGE="${2:?--identidad-age necesita un fichero}"; shift 2 ;;
        *) echo "ERROR: opción desconocida: $1"; exit 2 ;;
    esac
done
if [ -n "$COPIA_CLAVES" ] && [ -z "$IDENTIDAD_AGE" ]; then
    echo "ERROR: --copia-claves exige --identidad-age"; exit 2
fi
if [ -n "$DESDE_DIR" ] && [ -z "$MARCA_BACKUP" ]; then
    echo "ERROR: --desde-dir exige --marca-backup (sin ella no se puede medir el RPO)"; exit 2
fi
if [ -z "$DESDE_DIR" ]; then
    : "${BORG_REPO:?Define BORG_REPO (ssh://uXXXXXX@uXXXXXX.your-storagebox.de:23/./backups/caemanager)}"
    : "${BORG_PASSPHRASE:?Define BORG_PASSPHRASE (la passphrase del repo Borg)}"
    export BORG_REPO BORG_PASSPHRASE
fi

# NO en el servidor de producción: este ensayo levanta su propio PostgreSQL y su
# propia app (la app tiene techo de 3 GB) y compite por RAM y disco con el stack
# real de una máquina pequeña; un ensayo no puede tumbar lo que ensaya. Si aquí
# ya corre el stack (contenedor caemanager-app o caemanager-db), se niega.
if docker ps -a --format '{{.Names}}' 2>/dev/null | grep -qxE 'caemanager-(app|db)'; then
    if [ "${ENSAYO_PERMITIR_EN_SERVIDOR:-}" != "1" ]; then
        echo "ERROR: en este host existe el stack real (caemanager-app / caemanager-db)."
        echo "       Ejecuta el ensayo en otra máquina (tu equipo con Docker, o un servidor temporal)."
        echo "       Solo si sabes lo que haces: ENSAYO_PERMITIR_EN_SERVIDOR=1."
        exit 3
    fi
fi

IMAGEN_PG="${ENSAYO_IMAGEN_PG:-postgres:18}"
RPO_MAX_HORAS="${RPO_MAX_HORAS:-24}"
RTO_MAX_HORAS="${RTO_MAX_HORAS:-4}"

# Nombres únicos por ejecución: dos ensayos (o el de otra sesión) no se pisan.
SUFIJO="$$"
RED="ensayo-red-$SUFIJO"
PG="ensayo-pg-$SUFIJO"
CLAVE_PG="ensayo"
CLAVE_RUNTIME="ensayo-runtime"
CLAVE_ADMIN_ENSAYO="Ea1!$(openssl rand -hex 12)"
DIR_TRABAJO="$(mktemp -d)"
VOLUMENES=()
CONTENEDORES=("$PG")

limpiar() {
    local c v
    for c in "${CONTENEDORES[@]}"; do docker rm -f "$c" >/dev/null 2>&1 || true; done
    for v in "${VOLUMENES[@]}"; do docker volume rm -f "$v" >/dev/null 2>&1 || true; done
    docker network rm "$RED" >/dev/null 2>&1 || true
    rm -rf "$DIR_TRABAJO"
}
trap limpiar EXIT

# ── Registro de resultados y tiempos ─────────────────────────────────────
RESULTADOS=()
HAY_FALLO=0
HAY_OMITIDO=0
registrar() {   # registrar OK|FALLO|OMITIDO "nombre" "detalle"
    local estado="$1" nombre="$2" detalle="${3:-}"
    RESULTADOS+=("$(printf '%-8s %-38s %s' "$estado" "$nombre" "$detalle")")
    [ "$estado" = "FALLO" ] && HAY_FALLO=1
    [ "$estado" = "OMITIDO" ] && HAY_OMITIDO=1
    echo "    [$estado] $nombre ${detalle:+— $detalle}"
    return 0
}
T_INICIO=$(date +%s)
T_FASE=$T_INICIO
TIEMPOS=()
fase_hecha() {  # fase_hecha "nombre": guarda la duración desde la fase anterior
    local ahora
    ahora=$(date +%s)
    TIEMPOS+=("$(printf '%-30s %5d s' "$1" $((ahora - T_FASE)))")
    T_FASE=$ahora
}


# ── 1. Obtener el backup y medir el RPO ──────────────────────────────────
epoch_de_marca() {   # 2026-09-19T03-15-02 (UTC) -> epoch
    local m="$1"
    date -u -d "${m:0:10} ${m:11:2}:${m:14:2}:${m:17:2} UTC" +%s
}

if [ -n "$DESDE_DIR" ]; then
    echo "==> 1/7 Usando el backup ya extraído en $DESDE_DIR (marca $MARCA_BACKUP) ..."
    ULTIMO="caemanager-$MARCA_BACKUP"
    cp -r "$DESDE_DIR"/. "$DIR_TRABAJO"/
    HUECO_MAX_TXT="n/d (un solo archivo)"
else
    echo "==> 1/7 Localizando el archivo más reciente en $BORG_REPO ..."
    ULTIMO=$(borg list --glob-archives 'caemanager-*' --short | sort | tail -1)
    [ -n "$ULTIMO" ] || { echo "ERROR: no hay archivos caemanager-* en el repo Borg"; exit 1; }
    MARCA_BACKUP="${ULTIMO#caemanager-}"
    echo "    Archivo elegido: $ULTIMO"

    # Hueco máximo entre archivos consecutivos: demuestra la cadencia, no solo
    # que el último existe. Un cron que falló un día lo deja ver aquí.
    ANTERIOR=""; HUECO_MAX=0
    while read -r nombre; do
        actual=$(epoch_de_marca "${nombre#caemanager-}")
        if [ -n "$ANTERIOR" ] && [ $((actual - ANTERIOR)) -gt "$HUECO_MAX" ]; then
            HUECO_MAX=$((actual - ANTERIOR))
        fi
        ANTERIOR=$actual
    done < <(borg list --glob-archives 'caemanager-*' --short | sort)
    HUECO_MAX_TXT="$((HUECO_MAX / 3600)) h $(((HUECO_MAX % 3600) / 60)) min entre archivos consecutivos retenidos"
    fase_hecha "localizar archivo"

    echo "==> 2/7 Extrayendo CaeManager.dump + dataprotection-keys + documentos (juntos, del MISMO archivo)..."
    (cd "$DIR_TRABAJO" && borg extract "::$ULTIMO")
fi
[ -s "$DIR_TRABAJO/CaeManager.dump" ] || { echo "ERROR: el backup no trae CaeManager.dump"; exit 1; }
fase_hecha "extraer backup"

EDAD_S=$(( $(date -u +%s) - $(epoch_de_marca "$MARCA_BACKUP") ))
EDAD_H=$((EDAD_S / 3600))
if [ "$EDAD_S" -le $((RPO_MAX_HORAS * 3600)) ]; then
    registrar OK "RPO: antigüedad del último backup" "${EDAD_H} h $(((EDAD_S % 3600) / 60)) min (objetivo ${RPO_MAX_HORAS} h)"
else
    registrar FALLO "RPO: antigüedad del último backup" "${EDAD_H} h supera el objetivo de ${RPO_MAX_HORAS} h"
fi

# ── 2. PostgreSQL desechable, roles y restauración ───────────────────────
echo "==> 3/7 Levantando $IMAGEN_PG desechable ($PG) en la red $RED ..."
docker network create "$RED" >/dev/null
docker run -d --name "$PG" --network "$RED" -e POSTGRES_PASSWORD="$CLAVE_PG" \
    -e POSTGRES_DB=caemanager "$IMAGEN_PG" >/dev/null
until docker exec "$PG" pg_isready -U postgres -d caemanager >/dev/null 2>&1; do sleep 1; done
# La imagen oficial arranca dos veces (init temporal + definitivo): esperar a
# que el segundo responda evita un pg_isready verde del servidor temporal.
sleep 2
until docker exec "$PG" pg_isready -U postgres -d caemanager >/dev/null 2>&1; do sleep 1; done
fase_hecha "levantar PostgreSQL"

echo "==> 4/7 Restaurando (bootstrap de roles de clúster + pg_restore, como en RUNBOOK-CLAVES)..."
# pg_dump no puede incluir el CREATE ROLE —es objeto de clúster, no de base—, y
# ninguna migración lo crea. Sin este paso, un clúster restaurado no tendría los
# principales y la aplicación fallaría al migrar.
docker exec -i "$PG" psql --username=postgres --dbname=postgres -v ON_ERROR_STOP=1 \
    < "$RAIZ_REPO/deploy/bootstrap/roles-de-cluster.sql" >/dev/null

# SIN --no-privileges, a propósito: es la forma del RUNBOOK-CLAVES (el
# procedimiento real de recuperación). Con los roles ya creados arriba, los
# GRANT hacia cae_app_runtime se restauran, y así el ensayo comprueba también
# que la app podrá conectar con su rol restringido, no solo que hay filas.
docker exec -i "$PG" pg_restore --clean --if-exists --no-owner \
    --username=postgres --dbname=caemanager < "$DIR_TRABAJO/CaeManager.dump"
fase_hecha "restaurar BD (pg_restore)"

consulta() { docker exec "$PG" psql -U postgres -d caemanager -tAc "$1"; }

comprobar_tabla() {
    local tabla="$1" filas
    filas=$(consulta "SELECT COUNT(*) FROM \"$tabla\";")
    echo "    $tabla: $filas filas"
}
# Tablas núcleo: si alguna no existe, pg_restore no restauró el esquema completo y el script falla aquí.
# F3c (2026-08-28) retiró "Clientes": toda contraparte es hoy una fila de "Empresas".
for t in Empresas Trabajadores Documentos AspNetUsers Tenants; do comprobar_tabla "$t"; done
registrar OK "tablas núcleo restauradas" "Empresas, Trabajadores, Documentos, AspNetUsers, Tenants"

# RLS sobrevivió a la restauración, no solo los datos (ENABLE ROW LEVEL SECURITY
# y CREATE POLICY son DDL de esquema). Un ensayo en verde probaba recuperar
# FILAS, no que el aislamiento por tenant volviera a estar activo.
comprobar_rls() {
    local tabla="$1" activa politicas
    activa=$(consulta "SELECT relrowsecurity FROM pg_class WHERE relname = '$tabla' AND relnamespace = 'public'::regnamespace;")
    if [ "$activa" != "t" ]; then
        registrar FALLO "RLS en $tabla" "relrowsecurity=$activa tras la restauración"; return 0
    fi
    politicas=$(consulta "SELECT COUNT(*) FROM pg_policies WHERE schemaname = 'public' AND tablename = '$tabla';")
    if [ "${politicas:-0}" -eq 0 ]; then
        registrar FALLO "RLS en $tabla" "activo pero cero políticas: bloquearía TODO acceso"; return 0
    fi
    registrar OK "RLS en $tabla" "$politicas política(s)"
}
for t in Empresas Documentos Trabajadores; do comprobar_rls "$t"; done

# El rol de aplicación recupera sus privilegios (los da el pg_restore sin
# --no-privileges): sin ellos la app arrancaría y fallaría en la primera consulta.
if [ "$(consulta "SELECT has_table_privilege('cae_app_runtime', 'public.\"Documentos\"', 'SELECT');")" = "t" ]; then
    registrar OK "GRANT del rol cae_app_runtime" "SELECT sobre Documentos restaurado"
else
    registrar FALLO "GRANT del rol cae_app_runtime" "sin SELECT sobre Documentos tras pg_restore"
fi
# LOGIN es configuración de despliegue (RUNBOOK-RLS): en la copia se le da una
# contraseña desechable, igual que se hace en el servidor real.
docker exec "$PG" psql -U postgres -d postgres -v ON_ERROR_STOP=1 \
    -c "ALTER ROLE cae_app_runtime WITH LOGIN PASSWORD '$CLAVE_RUNTIME';" >/dev/null

CLAVES=$(ls "$DIR_TRABAJO/dataprotection-keys"/*.xml 2>/dev/null | wc -l)
echo "    dataprotection-keys/: $CLAVES archivo(s) de clave"
[ "$CLAVES" -ge 1 ] || { echo "ERROR: el backup no contiene ninguna clave XML — ver RUNBOOK-CLAVES.md (restaurar solo la BD deja las credenciales cifradas irrecuperables)"; exit 1; }
PDFS=$(find "$DIR_TRABAJO/documentos" -type f 2>/dev/null | wc -l)
echo "    documentos/: $PDFS archivo(s) restaurado(s)"
registrar OK "claves y documentos en el mismo archivo" "$CLAVES clave(s) XML, $PDFS documento(s)"
fase_hecha "verificaciones de BD"

# ── 3. La app contra la copia ────────────────────────────────────────────
CADENA_OWNER="Host=$PG;Port=5432;Database=caemanager;Username=postgres;Password=$CLAVE_PG"
CADENA_RUNTIME="Host=$PG;Port=5432;Database=caemanager;Username=cae_app_runtime;Password=$CLAVE_RUNTIME"

if [ "$SIN_APP" -eq 1 ]; then
    registrar OMITIDO "migrador, arranque, login, documento" "--sin-app"
else
    if [ -n "${ENSAYO_IMAGEN_APP:-}" ]; then
        IMAGEN_APP="$ENSAYO_IMAGEN_APP"
    else
        IMAGEN_APP="caemanager-ensayo:$(git -C "$RAIZ_REPO" rev-parse --short HEAD 2>/dev/null || echo local)"
        echo "==> 5/7 Construyendo la imagen de la app ($IMAGEN_APP) del Dockerfile de este árbol..."
        docker build -q -t "$IMAGEN_APP" "$RAIZ_REPO" >/dev/null
        fase_hecha "construir imagen de la app"
    fi

    # Prepara un volumen /data como el del servidor: claves + documentos
    # copiados con `docker cp`, igual que el paso 1 de RUNBOOK-CLAVES.
    preparar_volumen() {   # preparar_volumen NOMBRE DIR_CLAVES
        local vol="$1" dir_claves="$2" cargador="ensayo-carga-$SUFIJO-$1"
        docker volume create "$vol" >/dev/null
        VOLUMENES+=("$vol")
        docker create --name "$cargador" -v "$vol":/data --entrypoint true "$IMAGEN_APP" >/dev/null
        docker cp "$dir_claves" "$cargador":/data/dataprotection-keys
        docker cp "$DIR_TRABAJO/documentos" "$cargador":/data/documentos
        docker rm "$cargador" >/dev/null
    }

    entorno_app=(
        -e "ConnectionStrings__CaeManagerDb=$CADENA_OWNER"
        -e "ConnectionStrings__CaeManagerDbRuntime=$CADENA_RUNTIME"
        -e "AlmacenamientoArchivos__Ruta=/data/documentos"
        -e "DataProtection__RutaClaves=/data/dataprotection-keys"
        -e "Migraciones__AlArrancar=false"
        -e "DatosPrueba__Activo=false"
        # En Producción el arranque exige un administrador inicial (IdentitySeeder):
        # un valor desechable de esta copia, nunca el de verdad.
        -e "AdministradorInicial__Email=ensayo-admin@ensayo.invalid"
        -e "AdministradorInicial__Contrasena=$CLAVE_ADMIN_ENSAYO"
    )

    # Migraciones y arranque + login + documento, una vez por origen de claves.
    verificar_app() {   # verificar_app ETIQUETA DIR_CLAVES
        local etiqueta="$1" dir_claves="$2"
        local vol="ensayo-datos-$SUFIJO-$etiqueta" app="ensayo-app-$SUFIJO-$etiqueta"
        CONTENEDORES+=("$app")
        echo "==> Verificando la app con las claves de: $etiqueta"
        preparar_volumen "$vol" "$dir_claves"

        # Migrador, como en docker-compose.produccion.yml: mismo comando, misma
        # identidad de propietario. Sale con 0 o el esquema del backup no es
        # compatible con el código de este árbol.
        local antes despues
        antes=$(consulta "SELECT COUNT(*) FROM \"__EFMigrationsHistory\";")
        if docker run --rm --network "$RED" -v "$vol":/data "${entorno_app[@]}" "$IMAGEN_APP" --migrate-only \
                >"$DIR_TRABAJO/migrador-$etiqueta.log" 2>&1; then
            despues=$(consulta "SELECT COUNT(*) FROM \"__EFMigrationsHistory\";")
            registrar OK "migrador [$etiqueta]" "exit 0; migraciones aplicadas en la BD: $antes -> $despues"
        else
            registrar FALLO "migrador [$etiqueta]" "exit != 0 (ver: tail $DIR_TRABAJO/migrador-$etiqueta.log)"
            tail -20 "$DIR_TRABAJO/migrador-$etiqueta.log" | sed 's/^/        /'
            return 0
        fi
        fase_hecha "migrador [$etiqueta]"

        docker run -d --name "$app" --network "$RED" -v "$vol":/data "${entorno_app[@]}" "$IMAGEN_APP" >/dev/null
        local listo=0 i
        for i in $(seq 1 90); do
            if docker exec "$app" curl -fsS http://localhost:8080/salud >/dev/null 2>&1; then listo=1; break; fi
            # Un contenedor que ya murió no va a responder: no esperar los 180 s.
            [ "$(docker inspect -f '{{.State.Running}}' "$app")" = "true" ] || break
            sleep 2
        done
        if [ "$listo" -ne 1 ]; then
            registrar FALLO "arranque + /salud [$etiqueta]" "sin 200 (contenedor caído o más de 180 s)"
            docker logs --tail 25 "$app" 2>&1 | sed 's/^/        /'
            return 0
        fi
        registrar OK "arranque + /salud [$etiqueta]" "200"
        fase_hecha "arranque de la app [$etiqueta]"

        # El llavero restaurado sigue siendo el que usa la app: ninguna clave
        # restaurada ha cambiado (una clave nueva por caducidad es legítima; una
        # restaurada que cambia o desaparece, no).
        local cambiadas=0 f
        for f in "$dir_claves"/*.xml; do
            local n; n=$(basename "$f")
            local antes_h despues_h
            antes_h=$(sha256sum "$f" | cut -d' ' -f1)
            despues_h=$(docker exec "$app" sha256sum "/data/dataprotection-keys/$n" 2>/dev/null | cut -d' ' -f1 || true)
            [ "$antes_h" = "$despues_h" ] || cambiadas=$((cambiadas + 1))
        done
        local total_ahora
        total_ahora=$(docker exec "$app" sh -c 'ls /data/dataprotection-keys/*.xml | wc -l')
        if [ "$cambiadas" -eq 0 ]; then
            registrar OK "llavero intacto tras el arranque [$etiqueta]" "${total_ahora} clave(s) en la app; ninguna restaurada modificada"
        else
            registrar FALLO "llavero intacto tras el arranque [$etiqueta]" "$cambiadas clave(s) restaurada(s) distinta(s) o ausente(s)"
        fi

        # Login. La cookie de sesión la cifra Data Protection: que el segundo GET
        # (autenticado) responda bien prueba que se cifró y se leyó con el
        # llavero de este origen. OJO: el login solo NO prueba que el llavero sea
        # el correcto (cualquier llavero emite y lee sus propias cookies); lo que
        # lo prueba es el documento de más abajo, cifrado en su día con el
        # llavero verdadero.
        if [ -z "${CUENTA_LOGIN:-}" ]; then
            registrar OMITIDO "login [$etiqueta]" "sin cuenta utilizable: ${MOTIVO_SIN_CUENTA:-?}"
            registrar OMITIDO "documento descifrado [$etiqueta]" "requiere el login"
            return 0
        fi
        local html token handler
        html=$(docker exec "$app" curl -sS -c /tmp/jar -H 'X-Forwarded-Proto: https' \
            http://localhost:8080/cuenta/iniciar-sesion | tr '\n' ' ')
        token=$(printf '%s' "$html" | grep -o '<input[^>]*__RequestVerificationToken[^>]*>' | head -1 \
            | sed -n 's/.*value="\([^"]*\)".*/\1/p')
        handler=$(printf '%s' "$html" | grep -o '<input[^>]*name="_handler"[^>]*>' | head -1 \
            | sed -n 's/.*value="\([^"]*\)".*/\1/p')
        if [ -z "$token" ]; then
            registrar FALLO "login [$etiqueta]" "la página de login no trae token antiforgery"
            return 0
        fi
        local codigo
        # Cuenta y contraseña entran por variables del contenedor, no por argumentos.
        codigo=$(docker exec -e ENSAYO_CUENTA_LOGIN="$CUENTA_LOGIN" -e ENSAYO_CLAVE_LOGIN="$CLAVE_LOGIN" "$app" sh -c '
            curl -sS -o /dev/null -w "%{http_code}" -b /tmp/jar -c /tmp/jar -H "X-Forwarded-Proto: https" \
                --data-urlencode "_handler='"$handler"'" \
                --data-urlencode "__RequestVerificationToken='"$token"'" \
                --data-urlencode "Entrada.Email=$ENSAYO_CUENTA_LOGIN" \
                --data-urlencode "Entrada.Password=$ENSAYO_CLAVE_LOGIN" \
                http://localhost:8080/cuenta/iniciar-sesion')
        if docker exec "$app" grep -q '\.AspNetCore\.Identity\.Application' /tmp/jar; then
            registrar OK "login [$etiqueta]" "HTTP $codigo, cookie de sesión emitida (${ORIGEN_CUENTA})"
        else
            registrar FALLO "login [$etiqueta]" "HTTP $codigo sin cookie de sesión (¿contraseña, 2FA o cuenta inexistente en esta copia?)"
            return 0
        fi

        # Documento cifrado en reposo con el llavero (propósito Archivos.v2, por
        # tenant). Candidatos: documentos del tenant de la cuenta CUYO ARCHIVO
        # está de verdad en el documentos/ restaurado (los de siembra apuntan a
        # plantillas que no viven ahí y darían un falso negativo). 200 = descifra;
        # 500 = el servidor no pudo leerlo/descifrarlo (fallo real); 404/403 = la
        # cuenta no lo ve o no tiene permiso (rol, alcance de cartera) y se prueba
        # el siguiente.
        local candidatos doc_id doc_url probados=0 visto=0
        candidatos=$(consulta "SELECT \"Id\" || '|' || \"ArchivoUrl\" FROM \"Documentos\" WHERE \"TenantId\" = '$TENANT_LOGIN' AND \"ArchivoUrl\" IS NOT NULL LIMIT 500;")
        while IFS='|' read -r doc_id doc_url; do
            [ -n "$doc_id" ] || continue
            [ -f "$DIR_TRABAJO/documentos/$doc_url" ] || continue
            probados=$((probados + 1))
            local respuesta http_doc bytes_doc
            respuesta=$(docker exec "$app" sh -c \
                "curl -sS -b /tmp/jar -H 'X-Forwarded-Proto: https' -o /tmp/doc.bin -w '%{http_code} %{size_download}' http://localhost:8080/documentos/$doc_id/archivo")
            http_doc=${respuesta%% *}; bytes_doc=${respuesta##* }
            if [ "$http_doc" = "200" ] && [ "${bytes_doc:-0}" -gt 0 ]; then
                registrar OK "documento descifrado [$etiqueta]" "HTTP 200, $bytes_doc bytes ($probados candidato(s) probado(s))"
                visto=1; break
            elif [ "$http_doc" != "404" ] && [ "$http_doc" != "403" ]; then
                registrar FALLO "documento descifrado [$etiqueta]" "HTTP $http_doc, $bytes_doc bytes (el archivo existe en el backup y el servidor no lo entrega descifrado)"
                visto=1; break
            fi
            [ "$probados" -lt 10 ] || break
        done <<< "$candidatos"
        if [ "$visto" -eq 0 ]; then
            registrar OMITIDO "documento descifrado [$etiqueta]" "ningún documento con archivo restaurado visible para la cuenta ($probados probado(s)): usar una cuenta de un tenant con archivos"
        fi
        fase_hecha "login y documento [$etiqueta]"
    }

    # Cuenta para el login. Si el propietario da una (ENSAYO_CUENTA/ENSAYO_CLAVE,
    # SIN 2FA) se usa tal cual. Si no, se PREPARA UNA CREDENCIAL DE ENSAYO en la
    # copia desechable —nunca en el servidor—: un usuario del primer tenant que
    # tenga archivos restaurados recibe una contraseña aleatoria, sin 2FA y sin
    # bloqueo. Así el ensayo no depende de ninguna contraseña real ni de que las
    # cuentas de demo existan en producción, y el documento a descifrar es de un
    # tenant con archivos de verdad. El hash es el formato v3 de ASP.NET Identity
    # (PBKDF2-HMAC-SHA512, 100 000 iteraciones); si el login falla, falla el
    # ensayo, no se disimula.
    CUENTA_LOGIN=""; CLAVE_LOGIN=""; TENANT_LOGIN=""; ORIGEN_CUENTA=""; MOTIVO_SIN_CUENTA=""
    preparar_cuenta() {
        local tenant_n="" d cuenta
        for d in "$DIR_TRABAJO"/documentos/*/; do
            [ -d "$d" ] || continue
            if [ -n "$(find "$d" -type f 2>/dev/null | head -1)" ]; then tenant_n=$(basename "$d"); break; fi
        done
        if [ -n "${ENSAYO_CUENTA:-}" ] && [ -n "${ENSAYO_CLAVE:-}" ]; then
            CUENTA_LOGIN="$ENSAYO_CUENTA"; CLAVE_LOGIN="$ENSAYO_CLAVE"; ORIGEN_CUENTA="cuenta aportada"
            TENANT_LOGIN=$(consulta "SELECT \"TenantId\" FROM \"AspNetUsers\" WHERE lower(\"Email\") = lower('$CUENTA_LOGIN');")
            [ -n "$TENANT_LOGIN" ] || { MOTIVO_SIN_CUENTA="la cuenta aportada no existe en la copia"; CUENTA_LOGIN=""; }
            return 0
        fi
        if ! command -v python3 >/dev/null 2>&1; then MOTIVO_SIN_CUENTA="falta python3 para preparar la credencial de ensayo"; return 0; fi
        if [ -z "$tenant_n" ] || [ ${#tenant_n} -ne 32 ]; then MOTIVO_SIN_CUENTA="documentos/ no trae carpetas de tenant con archivos"; return 0; fi
        TENANT_LOGIN="${tenant_n:0:8}-${tenant_n:8:4}-${tenant_n:12:4}-${tenant_n:16:4}-${tenant_n:20:12}"
        cuenta=$(consulta "SELECT u.\"Email\" FROM \"AspNetUsers\" u JOIN \"AspNetUserRoles\" ur ON ur.\"UserId\" = u.\"Id\" JOIN \"AspNetRoles\" r ON r.\"Id\" = ur.\"RoleId\" WHERE u.\"TenantId\" = '$TENANT_LOGIN' ORDER BY CASE r.\"Name\" WHEN 'DireccionCae' THEN 0 WHEN 'CoordinadorCae' THEN 1 WHEN 'GestorCae' THEN 2 ELSE 3 END, u.\"Email\" LIMIT 1;")
        if [ -z "$cuenta" ]; then MOTIVO_SIN_CUENTA="el tenant con archivos no tiene usuarios"; return 0; fi
        local clave hash
        clave="Ea1!$(openssl rand -hex 12)"
        hash=$(python3 - "$clave" <<'PY'
import base64, hashlib, os, struct, sys
sal = os.urandom(16)
subclave = hashlib.pbkdf2_hmac('sha512', sys.argv[1].encode(), sal, 100000, 32)
print(base64.b64encode(bytes([1]) + struct.pack('>III', 2, 100000, 16) + sal + subclave).decode())
PY
)
        consulta "UPDATE \"AspNetUsers\" SET \"PasswordHash\" = '$hash', \"TwoFactorEnabled\" = false, \"EmailConfirmed\" = true, \"DebeCambiarContrasena\" = false, \"LockoutEnd\" = NULL, \"AccessFailedCount\" = 0 WHERE \"Email\" = '$cuenta';" >/dev/null
        CUENTA_LOGIN="$cuenta"; CLAVE_LOGIN="$clave"
        ORIGEN_CUENTA="credencial de ensayo preparada en la copia para un usuario del tenant ${TENANT_LOGIN:0:8}…"
    }
    preparar_cuenta

    echo "==> 6/7 Migrador, arranque, login y documento con las claves del archivo Borg..."
    verificar_app archivo "$DIR_TRABAJO/dataprotection-keys"

    # ── P38: la segunda ubicación de las claves debe bastar por sí sola ──
    if [ -n "$COPIA_CLAVES" ]; then
        echo "==> 7/7 Claves desde la copia cifrada (segunda ubicación)..."
        if ! command -v age >/dev/null 2>&1; then
            registrar FALLO "copia cifrada de las claves" "falta el binario age"
        else
            mkdir -p "$DIR_TRABAJO/claves-copia"
            if age -d -i "$IDENTIDAD_AGE" "$COPIA_CLAVES" | tar -x -C "$DIR_TRABAJO/claves-copia"; then
                if ls "$DIR_TRABAJO"/claves-copia/dataprotection-keys/*.xml >/dev/null 2>&1; then
                    solo_archivo=$(comm -23 <(cd "$DIR_TRABAJO/dataprotection-keys" && ls *.xml | sort) \
                                            <(cd "$DIR_TRABAJO/claves-copia/dataprotection-keys" && ls *.xml | sort) | wc -l)
                    solo_copia=$(comm -13 <(cd "$DIR_TRABAJO/dataprotection-keys" && ls *.xml | sort) \
                                          <(cd "$DIR_TRABAJO/claves-copia/dataprotection-keys" && ls *.xml | sort) | wc -l)
                    # Una clave del archivo que la copia no tiene = copia obsoleta: lo
                    # cifrado con esa clave no se podría leer si solo quedara la copia.
                    if [ "$solo_archivo" -gt 0 ]; then
                        registrar FALLO "copia cifrada de las claves al día" "le faltan $solo_archivo clave(s) que sí están en el archivo Borg: exportar de nuevo"
                    else
                        registrar OK "copia cifrada de las claves al día" "abre y no le falta ninguna clave del archivo ($solo_copia más reciente(s) que el archivo)"
                    fi
                    verificar_app copia "$DIR_TRABAJO/claves-copia/dataprotection-keys"
                else
                    registrar FALLO "copia cifrada de las claves" "descifra pero no contiene dataprotection-keys/*.xml"
                fi
            else
                registrar FALLO "copia cifrada de las claves" "age no pudo descifrarla con la identidad dada"
            fi
        fi
    else
        registrar OMITIDO "copia cifrada de las claves (P38)" "sin --copia-claves"
    fi
fi

# ── Resumen ──────────────────────────────────────────────────────────────
T_FIN=$(date +%s)
TOTAL=$((T_FIN - T_INICIO))
echo ""
echo "════════ RESUMEN DEL ENSAYO ════════"
echo "Archivo restaurado : $ULTIMO"
echo "RPO observado      : ${EDAD_H} h $(((EDAD_S % 3600) / 60)) min de antigüedad; cadencia: $HUECO_MAX_TXT"
echo ""
printf '%s\n' "${RESULTADOS[@]}"
echo ""
echo "Tiempos por fase:"
printf '  %s\n' "${TIEMPOS[@]}"
echo "  TOTAL automatizado: ${TOTAL} s ($((TOTAL / 60)) min $((TOTAL % 60)) s)"
echo ""
echo "El RTO NO es este total: este es el tramo automatizable (extraer, restaurar, migrar, arrancar,"
echo "verificar) sobre una máquina que ya existe. Al RTO real (objetivo ${RTO_MAX_HORAS} h laborables) hay que"
echo "sumarle localizar credenciales, provisionar el servidor si se perdió, DNS y la persona que lo hace."
echo ""
echo "Fila para docs/ENSAYO-RESTAURACION.md (repositorio de negocio):"
echo "| $(date -u +%F) | \`$ULTIMO\` | <quién> | <dump: ver resumen> | <claves/login/documento: ver resumen> | ${TOTAL} s automatizado; RPO ${EDAD_H} h | <notas> |"
echo ""
if [ "$HAY_FALLO" -ne 0 ]; then
    echo "ENSAYO: FALLIDO — hoy NO hay recuperación demostrada. Tratar como incidente de severidad alta."
    exit 1
elif [ "$HAY_OMITIDO" -ne 0 ]; then
    echo "ENSAYO: PARCIAL — todo lo ejecutado pasó, pero hay comprobaciones omitidas (ver arriba)."
    exit 0
fi
echo "ENSAYO: COMPLETO"
