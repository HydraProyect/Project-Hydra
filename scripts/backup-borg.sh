#!/usr/bin/env bash
# Backup del stack local (deploy/local/) al Storage Box de Hetzner con Borg.
#
# Único mecanismo de backup automático: el Storage Box no habla S3, así que
# el backup corre como cron del host, no como servicio dentro de la app.
# Mantiene la invariante de Project-Hydra-Negocio/tecnico/RUNBOOK-CLAVES.md — el volcado
# de la BD y dataprotection-keys/ van SIEMPRE en el mismo archivo de backup
# (restaurar la BD con claves de otro momento deja las credenciales cifradas
# de Empresa/Subcontrata irrecuperables) — y añade lo que el servicio antiguo
# no cubría: los PDFs de /data/documentos y el .env de producción (archivo
# env-produccion, 0600, cifrado por Borg; ver el bloque correspondiente).
#
# Requisitos: borg en el host, el repo Borg ya inicializado
# (`borg init --encryption=repokey-blake2 "$BORG_REPO"`; el guion se niega a
# trabajar con un repositorio sin cifrar, paso 0/4) y los contenedores
# caemanager-db del compose levantado y al menos una ranura de la app
# (caemanager-app-azul / -verde) creada. Corre como root (el cron sale de
# /etc/caemanager-backup.env, solo-root): lee documentos/ en el punto de montaje
# del volumen, bajo /var/lib/docker, con `unshare --mount` y `mount --bind`
# (util-linux), sin copiarlos al disco del host (paso 3/4).
#
# Uso (cron diario recomendado, ver Project-Hydra-Negocio/tecnico/RUNBOOK-DESPLIEGUE-LOCAL.md § Backups):
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
# Todo lo que este guion crea en disco (volcado de la BD, claves de Data Protection,
# .env de producción) es secreto: nada legible por otros usuarios, ni siquiera un
# instante entre crear el fichero y ajustarle los permisos.
umask 077

: "${BORG_REPO:?Define BORG_REPO (ssh://uXXXXXX@uXXXXXX.your-storagebox.de:23/./backups/caemanager)}"
: "${BORG_PASSPHRASE:?Define BORG_PASSPHRASE (la passphrase del repo Borg)}"
export BORG_REPO BORG_PASSPHRASE

ahora() { date -u +%Y-%m-%dT%H:%M:%SZ; }

# Directorio de trabajo: prefijo fijo con el PID del guion dentro del nombre
# (`caemanager-backup.<pid>.<aleatorio>`, modo 0700 por mktemp). El `trap` de abajo
# lo borra al salir, pero un SIGKILL, un corte de luz o el OOM killer no dejan
# ejecutar ningún `trap`: el volcado de la BD, las claves y el .env se quedarían
# en claro en el disco del host. Por eso, al arrancar, se borran los restos de
# ejecuciones anteriores con ese prefijo. Solo los de un PID que ya no vive (una
# ejecución simultánea, p. ej. el `--check` semanal con el diario, conserva el suyo),
# solo directorios propios y sin cruzar de sistema de ficheros (`--one-file-system`:
# si algún día quedara un montaje dentro, rm no entra en él, avisa y no toca el
# volumen de documentos).
PREFIJO_TRABAJO="caemanager-backup"
BASE_TEMPORALES="${TMPDIR:-/tmp}"
for resto in "$BASE_TEMPORALES/$PREFIJO_TRABAJO".*; do
    [ -d "$resto" ] && [ ! -L "$resto" ] && [ -O "$resto" ] || continue
    pid_resto="${resto##*/$PREFIJO_TRABAJO.}"
    pid_resto="${pid_resto%%.*}"
    case "$pid_resto" in ''|*[!0-9]*) continue ;; esac
    if kill -0 "$pid_resto" 2>/dev/null; then continue; fi
    echo "AVISO: borrando el directorio de trabajo que dejó una ejecución anterior interrumpida: $resto"
    rm -rf --one-file-system -- "$resto" \
        || echo "AVISO: no se pudo borrar del todo $resto; revisarlo a mano (contiene secretos del backup)."
done
DIR_TRABAJO="$(mktemp -d "$BASE_TEMPORALES/$PREFIJO_TRABAJO.$$.XXXXXXXX")"

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
    # Sin cruzar de sistema de ficheros: aunque quedara algo montado dentro, rm no entra.
    rm -rf --one-file-system -- "$DIR_TRABAJO" \
        || echo "AVISO: no se pudo borrar $DIR_TRABAJO (contiene secretos del backup); el próximo arranque lo reintenta si el PID ya no vive."
    if [ "$codigo" -ne 0 ] && [ -n "${BETTERSTACK_HEARTBEAT_URL:-}" ]; then
        llamar_heartbeat "${BETTERSTACK_HEARTBEAT_URL%/}/fail" --retry 2 --retry-delay 3 >/dev/null 2>&1 \
            || echo "AVISO: el backup FALLÓ y tampoco se pudo avisar al heartbeat (/fail) — la ausencia del ping de éxito lo avisará igualmente."
    fi
    return "$codigo"
}
trap al_salir EXIT
# INT, TERM y HUP (Ctrl-C, `kill`, parada del servicio, cierre de la sesión) terminan
# con `exit`, y así corre el trap de EXIT y se borra el directorio de trabajo. Con un
# proceso hijo en primer plano (borg), bash ejecuta el manejador al acabar el hijo.
# SIGKILL no se puede atrapar: de esos restos se ocupa el barrido del arranque.
trap 'exit 130' INT
trap 'exit 143' TERM
trap 'exit 129' HUP

# Borg guarda aquí el volcado de la BD, las claves de Data Protection y el .env de
# producción, y los envía fuera del servidor: si el repositorio no estuviera cifrado
# (`borg init --encryption=none`, o `authenticated*`, que solo firma), irían en claro
# al Storage Box aunque BORG_PASSPHRASE esté definida (Borg la ignora en un
# repositorio sin cifrar). Se exige un modo con clave (`repokey*` / `keyfile*`) y se
# aborta —con /fail, porque el trap ya está armado— si es otro o si no se puede leer.
echo "==> 0/4 Comprobando que el repositorio Borg está cifrado (borg info --json)..."
INFO_REPOSITORIO="$(borg info --json)" \
    || { echo "ERROR: borg info --json falló (repositorio inaccesible, passphrase incorrecta u otro error de borg, ver arriba); no se hace backup"; exit 1; }
MODO_CIFRADO="$(printf '%s' "$INFO_REPOSITORIO" | tr -d '\n' \
    | sed -n 's/.*"encryption"[[:space:]]*:[[:space:]]*{[^}]*"mode"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p')"
case "$MODO_CIFRADO" in
    repokey*|keyfile*)
        echo "    repositorio cifrado (modo $MODO_CIFRADO)" ;;
    '')
        echo "ERROR: no se pudo leer la encriptación del repositorio en la salida de borg info --json; no se envía nada sin saber si va cifrado"
        exit 1 ;;
    *)
        echo "ERROR: el repositorio Borg NO está cifrado (modo '$MODO_CIFRADO'): el backup llevaría la BD, las claves y el .env en claro. Crea otro repositorio con 'borg init --encryption=repokey-blake2' y apunta BORG_REPO a él."
        exit 1 ;;
esac

# Ventana de consistencia (S-D-10): la BD (paso 1/4) y documentos/ (paso 3/4) no se
# leen en el mismo instante. Cada fase deja su hora de inicio y fin en el registro, y
# al final del paso 3/4 se resumen, para que una restauración pueda acotar la ventana.
echo "==> 1/4 Volcando PostgreSQL (pg_dump --format=custom, dentro del contenedor db)..."
T_VOLCADO_INICIO="$(ahora)"
echo "    [$T_VOLCADO_INICIO] inicio del volcado de la BD"
docker exec caemanager-db pg_dump -U postgres --format=custom caemanager \
    > "$DIR_TRABAJO/CaeManager.dump"
[ -s "$DIR_TRABAJO/CaeManager.dump" ] || { echo "ERROR: el dump salió vacío"; exit 1; }
T_VOLCADO_FIN="$(ahora)"
echo "    [$T_VOLCADO_FIN] fin del volcado de la BD"

echo "==> 2/4 Copiando dataprotection-keys/ y localizando documentos/ en el volumen..."
T_CLAVES_INICIO="$(ahora)"
echo "    [$T_CLAVES_INICIO] inicio de la copia de dataprotection-keys/"
# P1-F2: la app de producción corre en dos ranuras (caemanager-app-azul y
# -verde, deploy/relevo-app.sh) y no hay un nombre fijo. Todas montan el mismo
# volumen caemanager-data —también el contenedor único anterior a P1-F2—, y
# tanto `docker cp` como el punto de montaje del volumen sirven también con el
# contenedor parado: vale cualquiera que exista.
CONTENEDOR_DATOS=""
for candidato in caemanager-app-azul caemanager-app-verde caemanager-app; do
    if docker inspect "$candidato" > /dev/null 2>&1; then CONTENEDOR_DATOS="$candidato"; break; fi
done
[ -n "$CONTENEDOR_DATOS" ] \
    || { echo "ERROR: no existe ningún contenedor de la app de producción (caemanager-app-azul/-verde) del que copiar /data"; exit 1; }
docker cp "$CONTENEDOR_DATOS":/data/dataprotection-keys "$DIR_TRABAJO/dataprotection-keys"
# Sin claves no hay backup válido.
ls "$DIR_TRABAJO/dataprotection-keys"/*.xml >/dev/null 2>&1 \
    || { echo "ERROR: dataprotection-keys/ no contiene ninguna clave XML — ver Project-Hydra-Negocio/tecnico/RUNBOOK-CLAVES.md"; exit 1; }
echo "    [$(ahora)] fin de la copia de dataprotection-keys/"

# documentos/ NO se copia al host: hasta 2026-10 se hacía `docker cp` de todo
# /data/documentos a este directorio temporal, y el disco del servidor tenía que
# tener sitio para los documentos dos veces cada noche. Ahora Borg los lee en
# su sitio, en el punto de montaje del volumen /data de la ranura (paso 3/4).
VOLUMEN_DATOS=$(docker inspect -f '{{range .Mounts}}{{if eq .Destination "/data"}}{{if eq .Type "volume"}}{{.Name}}{{end}}{{end}}{{end}}' "$CONTENEDOR_DATOS")
[ -n "$VOLUMEN_DATOS" ] \
    || { echo "ERROR: /data de $CONTENEDOR_DATOS no es un volumen Docker con nombre; no se sabe de dónde leer documentos/"; exit 1; }
# Solo un volumen `local` sin opciones guarda sus datos en el propio punto de
# montaje. Uno con driver_opts (nfs, bind) u otro driver solo está montado ahí
# mientras un contenedor lo usa: con la ranura parada se vería vacío y daría un
# backup sin documentos y en verde.
TIPO_VOLUMEN=$(docker volume inspect -f '{{ .Driver }} {{ len .Options }}' "$VOLUMEN_DATOS")
[ "$TIPO_VOLUMEN" = "local 0" ] \
    || { echo "ERROR: el volumen $VOLUMEN_DATOS no es un volumen local sin opciones ('$TIPO_VOLUMEN'); su punto de montaje puede no tener los documentos"; exit 1; }
ORIGEN_DATOS=$(docker volume inspect -f '{{ .Mountpoint }}' "$VOLUMEN_DATOS")
# /var/lib/docker solo lo atraviesa root: si esto falla, el cron no corre como root.
[ -n "$ORIGEN_DATOS" ] && [ -d "$ORIGEN_DATOS" ] \
    || { echo "ERROR: el punto de montaje del volumen $VOLUMEN_DATOS ('$ORIGEN_DATOS') no es un directorio accesible (¿el guion corre como root?)"; exit 1; }
# documentos/ puede no existir aún (nadie subió un PDF todavía) — eso sí es
# válido: el archivo lleva entonces un documentos/ vacío, como antes.
mkdir "$DIR_TRABAJO/documentos"
ORIGEN_DOCUMENTOS=""
if [ -d "$ORIGEN_DATOS/documentos" ]; then
    ORIGEN_DOCUMENTOS="$ORIGEN_DATOS/documentos"
    echo "    documentos/ se lee en caliente de $ORIGEN_DOCUMENTOS (volumen $VOLUMEN_DATOS), sin copia temporal"
else
    echo "    el volumen $VOLUMEN_DATOS aún no tiene documentos/: el archivo lleva un documentos/ vacío"
fi

# El .env de producción (contraseña de PostgreSQL, la del rol cae_app_runtime,
# DSN de Sentry, claves de IA y demás secretos) entra en el MISMO archivo Borg
# (decisión del propietario, continuidad n.º 26): sin él, perder el disco del
# servidor obliga a regenerar todos los secretos a mano y entra en el RTO. Va
# cifrado por Borg (repokey-blake2), como el resto del archivo. El contenido
# NUNCA se imprime ni pasa por argumentos: solo se copia con permisos 0600.
# Si falta o está vacío NO se aborta: el archivo Borg se crea igualmente con el
# volcado de la BD, las claves y los documentos (la ausencia de un secreto no puede
# costar el backup de la base de datos de esa noche). La ausencia se registra y el
# guion termina con código distinto de 0 AL FINAL, sin ping de éxito, para que salte
# /fail: ruidoso, pero sin perder el backup. Omitirlo en silencio sería un falso
# verde (un backup que parece bueno y no lleva el .env).
# Salida deliberada: BACKUP_SIN_ENV=1.
ENV_FALTA=0
if [ "${BACKUP_SIN_ENV:-0}" = "1" ]; then
    echo "AVISO: BACKUP_SIN_ENV=1 — este archivo NO incluye el .env; restaurar el servidor exigirá regenerar todos los secretos a mano."
else
    ENV_ORIGEN="${ENV_PRODUCCION:-$(cd "$(dirname "$0")/.." && pwd)/deploy/local/.env}"
    if [ ! -s "$ENV_ORIGEN" ]; then
        echo "ERROR: no hay .env de producción (o está vacío) en $ENV_ORIGEN."
        echo "       Indica su ruta con ENV_PRODUCCION, o BACKUP_SIN_ENV=1 para omitirlo a sabiendas."
        ENV_FALTA=1
    elif ! install -m 600 "$ENV_ORIGEN" "$DIR_TRABAJO/env-produccion" 2>/dev/null; then
        # Existe pero no se puede copiar (p. ej. 0600 de otro usuario): tampoco
        # cuesta el backup de la BD (hallazgo de Codex). Se borra lo que haya quedado.
        rm -f "$DIR_TRABAJO/env-produccion"
        echo "ERROR: el .env de producción existe en $ENV_ORIGEN pero no se pudo copiar (¿permisos? ¿lo lee el usuario del cron?)."
        ENV_FALTA=1
    else
        echo "    .env de producción incluido como env-produccion ($(wc -c < "$DIR_TRABAJO/env-produccion") bytes; el contenido no se muestra)"
    fi
fi

ARCHIVO="caemanager-$(date -u +%Y-%m-%dT%H-%M-%S)"
echo "==> 3/4 borg create ::$ARCHIVO ..."
CONTENIDO=(CaeManager.dump dataprotection-keys documentos)
[ -f "$DIR_TRABAJO/env-produccion" ] && CONTENIDO+=(env-produccion)
# Rutas dentro del archivo, iguales que cuando documentos/ se copiaba aquí:
# borg corre con el directorio de trabajo como cwd y rutas relativas, así que el
# archivo lleva `CaeManager.dump`, `dataprotection-keys/<clave>.xml`,
# `documentos/<tenantId>/...` y `env-produccion` en la raíz. La restauración
# (scripts/ensayo-restauracion-borg.sh: `borg extract` en un directorio vacío)
# no distingue los archivos de antes de los de después.
#
# documentos/ entra por un bind de SOLO LECTURA del punto de montaje del volumen
# sobre $DIR_TRABAJO/documentos, hecho dentro de un espacio de nombres de montaje
# privado (`unshare --mount`): el montaje solo lo ve este borg y desaparece con
# él, aunque muera de un kill -9. Así no queda nunca un montaje colgado y el
# `rm -rf "$DIR_TRABAJO"` de al_salir, que corre fuera de ese espacio, solo ve un
# directorio vacío: no puede llegar a los documentos de producción.
#
# Copia «en caliente», sin parar la app, y basta: DiskFileStorageService escribe
# cada documento en un temporal `<ruta>.tmp-<guid>` de la misma carpeta y lo
# publica con un rename atómico, sin sobrescribir nunca un fichero existente
# (renovar un documento crea otro fichero). Lo que Borg ve en la ruta final está
# completo y no cambia; los temporales (escrituras a medias o huérfanas) se
# excluyen. El patrón es el nombre exacto que les da GuardarAsync,
# `documentos/<tenant>/<guid de 32 hex><extensión>.tmp-<guid de 32 hex>`, y no un
# `*.tmp-*` genérico: la extensión sale del nombre del adjunto, que puede venir de
# fuera (correo, WhatsApp), y un documento legítimo `<guid>.tmp-1` debe entrar.
# GuardarAsync reserva los sufijos `.tmp-*` a temporales en nuevas escrituras.
# Los definitivos antiguos terminados en `.tmp-<32 hex>` siguen siendo
# indistinguibles por el nombre y quedan excluidos; no se migran aquí.
# VENTANA DE CONSISTENCIA ENTRE LA BD Y documentos/ (S-D-10). No son una
# instantánea única: el volcado de la BD se hizo ANTES (paso 1/4) y Borg lee
# documentos/ DESPUÉS, en caliente. pg_dump toma su instantánea de la BD AL EMPEZAR,
# así que la ventana va desde el INICIO del volcado hasta el fin de borg create.
# Suponiendo que la app publica el fichero antes de insertar su fila (así lo hace
# GuardarAsync + SaveChangesAsync, p. ej. al firmar un documento), ninguna fila del
# volcado apunta a un fichero publicado después de su instantánea; el orden es
# deliberado: con el inverso, documentos primero y BD después, una fila podría
# apuntar a un fichero que el archivo no lleva. Lo que sí puede pasar, solo dentro
# de la ventana: (a) un documento publicado en ella puede entrar en el archivo de
# esta noche sin fila en el volcado (su fila llega en el volcado de mañana);
# (b) un fichero borrado en ella falta en el archivo aunque su fila siga en el
# volcado, igual que con el `docker cp` de antes (mismo orden). Las horas de inicio
# y fin de cada fase quedan en el registro, y el resumen del final de este paso las
# repite juntas, para acotar esa ventana al restaurar.
# Si un fichero se borra mientras Borg lo lee, Borg avisa (código 1)
# y el guion falla con /fail, como ante cualquier otro aviso de borg create: el
# archivo queda hecho, pero esa noche no hay prune ni compact (igual que hasta
# ahora), para que un archivo con avisos no saque de la retención a uno bueno.
EXCLUIR_TEMPORALES='re:^documentos/[^/]+/[0-9a-f]{32}[^/]*\.tmp-[0-9a-f]{32}$'
OPCIONES_CREATE=(--stats --compression zstd --exclude "$EXCLUIR_TEMPORALES")
T_CREATE_INICIO="$(ahora)"
echo "    [$T_CREATE_INICIO] inicio de borg create (lee documentos/ en caliente)"
if [ -n "$ORIGEN_DOCUMENTOS" ]; then
    unshare --mount --propagation private -- bash -eu -c '
        origen="$1"; trabajo="$2"; shift 2
        mount --bind "$origen" "$trabajo/documentos"
        mount -o remount,bind,ro "$trabajo/documentos"
        cd "$trabajo"
        exec borg create "$@"
    ' montar-documentos "$ORIGEN_DOCUMENTOS" "$DIR_TRABAJO" \
        "${OPCIONES_CREATE[@]}" "::$ARCHIVO" "${CONTENIDO[@]}"
else
    (cd "$DIR_TRABAJO" && borg create "${OPCIONES_CREATE[@]}" \
        "::$ARCHIVO" "${CONTENIDO[@]}")
fi
T_CREATE_FIN="$(ahora)"
echo "    [$T_CREATE_FIN] fin de borg create"
echo "    VENTANA ($ARCHIVO): BD volcada entre $T_VOLCADO_INICIO y $T_VOLCADO_FIN; claves copiadas desde $T_CLAVES_INICIO; documentos/ leídos entre $T_CREATE_INICIO y $T_CREATE_FIN."
echo "    Al restaurar: un documento publicado después de $T_VOLCADO_INICIO (la instantánea de pg_dump es de su inicio) puede estar sin fila en la BD; uno borrado después de $T_VOLCADO_INICIO puede tener fila y no fichero."

echo "==> 4/4 borg prune (7 diarios / 4 semanales / 6 mensuales) + compact..."
borg prune --glob-archives 'caemanager-*' \
    --keep-daily 7 --keep-weekly 4 --keep-monthly 6
borg compact

# --check (p. ej. en el cron semanal): verificación de integridad del repo.
if [ "${1:-}" = "--check" ]; then
    echo "==> borg check..."
    borg check
fi

# El archivo con la BD ya existe (borg create, prune y compact hechos). Si faltó
# el .env se termina AHORA con error: al_salir avisa con /fail y no se manda el
# ping de éxito. La BD no se pierde; el aviso es ruidoso a propósito.
if [ "$ENV_FALTA" -eq 1 ]; then
    echo "BACKUP DE LA BD COMPLETADO: $ARCHIVO — PERO SIN el .env de producción (ver el ERROR de arriba)."
    echo "El guion termina con error a propósito: avisa con /fail y no manda el ping de éxito."
    exit 1
fi

echo "BACKUP COMPLETADO: $ARCHIVO"
echo "Ensayo de restauración periódico: scripts/ensayo-restauracion-borg.sh (anotar en Project-Hydra-Negocio/tecnico/docs/ENSAYO-RESTAURACION.md, repositorio de negocio)."

# Dead man's switch (Horizonte 2.4 del plan macro): un cron que deja de
# ejecutarse (host caído, systemd-timer borrado sin querer, el propio script
# roto por una actualización) no puede reportar su propio fallo — por
# definición, no hay proceso vivo que lo intente. La única forma de detectar
# "el backup de hoy no corrió" es que algo EXTERNO note la AUSENCIA de una
# señal, no que este script reporte un error. Better Stack (ya en uso para
# el uptime check externo, ver Project-Hydra-Negocio/tecnico/RUNBOOK-HORIZONTE-0.md § 0.3) tiene monitores
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
