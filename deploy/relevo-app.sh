#!/bin/bash
# Relevo sin corte de la aplicación (P1-F2): arranca la versión nueva junto a
# la vieja, conmuta Caddy cuando la nueva está sana y deja drenar la vieja.
#
# Por qué: recrear el contenedor de la app en cada despliegue mataba el proceso
# que guarda en memoria los circuitos Blazor Server de todos los usuarios; su
# reconexión llegaba a un proceso que no los conocía y la interfaz mostraba
# «sesión perdida» (ReconnectModal), con los cambios sin guardar perdidos, y
# durante el arranque de la nueva Caddy respondía 502.
#
# Modelo:
#   - Dos ranuras por entorno en el compose, app-azul y app-verde (perfil
#     "ranura"), con container_name <prefijo>-azul / <prefijo>-verde.
#   - Un fichero por entorno en $DIR_RANURAS, importado por el Caddyfile dentro
#     de su reverse_proxy, con UNA línea: `to <activa>:8080 [<saliente>:8080]`.
#     Es el único estado del relevo: la primera dirección es la ranura activa.
#   - El Caddyfile fija afinidad por cookie (quien tenía la página abierta sigue
#     en la saliente) y stream_close_delay (una recarga no corta los WebSocket
#     abiertos). Ver su cabecera, con lo medido.
#
# Órdenes:
#   relevo-app.sh desplegar <staging|produccion> <sha>
#       Con la imagen caemanager:<sha> ya cargada y el cerrojo de despliegue
#       YA TOMADO por quien llama (ci-deploy.sh, volver-atras.sh):
#         0. Exige EnvioCorreo__Modo con valor en el entorno efectivo de la
#            ranura nueva, y en staging que ese valor sea Cerrado y que la imagen
#            declare la guarda; en producción, una imagen que no la declara solo
#            arranca con Abierto (exigir_guarda_de_correo). Si no, sale con 1
#            antes de tocar Caddy, el fichero de ranuras, el migrador o los
#            contenedores.
#         1. Saca de Caddy y para las salientes de un relevo anterior que sigan
#            en el fichero (la libre si aún drenaba, o una cuyo drenaje murió).
#         2. `docker compose up -d --wait --no-build` de los servicios sin
#            perfil (db, migrador, seq y, en producción, caddy) y de la ranura
#            libre, con IMAGEN_TAG=<sha>. El migrador corre aquí, con la
#            ranura activa sirviendo: expand/contract (runbook de despliegue).
#         3. Sana la nueva, reescribe el fichero de ranuras con la nueva
#            primero y la activa detrás, y recarga Caddy. Si la recarga falla,
#            restaura el fichero, para la nueva y sale con 1: sigue sirviendo
#            la de antes.
#         4. Lanza el drenaje de la anterior (systemd-run si existe), salvo que
#            la anterior no declare la guarda y el modo no sea Abierto: esa nunca
#            drena. Se saca de Caddy y se para con reintentos acotados, stop y
#            kill, solo si la nueva está sana. Lo mismo vale para las salientes
#            del paso 1 y para cualquier contenedor del entorno que ignore la
#            guarda aunque el fichero ya no lo anote (sin_guarda_sin_anotar).
#       Sale con 0 solo si la nueva ranura quedó sirviendo y no queda en marcha
#       ninguna imagen que ignore la guarda con modo distinto de Abierto.
#   relevo-app.sh comprobar-guarda <staging|produccion> <sha>
#       Solo el paso 0 de `desplegar`, sobre la misma ranura, sin tocar nada más.
#       volver-atras.sh la llama antes de restaurar el historial de migraciones.
#   relevo-app.sh retirar-sin-guarda <staging|produccion> <sha>
#       Con el cerrojo de despliegue tomado por quien llama, retira las imágenes
#       que ignoran la guarda y que un relevo fallido dejó vivas, junto a la ranura
#       que sirve. Sale con 1 si alguna sigue viva o si la que sirve la ignora, y
#       sin parar nada si no puede leer el modo o confirmar que Caddy sirve la
#       sana. volver-atras.sh la llama cuando el destino ya sirve y no pasa por
#       `desplegar`.
#   relevo-app.sh activa <staging|produccion>
#       Imprime el container_name de la ranura activa (o del contenedor
#       anterior a P1-F2, si aún no ha habido relevo). Sale con 1 si no hay.
#   relevo-app.sh recargar <staging|produccion>
#       Recarga Caddy con los ficheros de ranuras tal como están y el
#       Caddyfile aprobado (volver-atras.sh, antes de dar por buena una ranura
#       que ya figura como activa).
#   relevo-app.sh drenar <staging|produccion> <contenedor> <id> <max_s>
#       Lo lanza `desplegar`; no se llama a mano. Cada $DRENAJE_INTERVALO s
#       cuenta las conexiones TCP establecidas al 8080 del contenedor (las de
#       Caddy: circuitos abiertos y keep-alive), y cuando lleva dos lecturas
#       seguidas en 0 —o vence <max_s>— toma el cerrojo de despliegue, lo quita
#       del fichero de ranuras, recarga Caddy y lo para. Nunca toca un
#       contenedor que ya no sea el mismo (<id>) ni la ranura activa.
#
# Transición desde el despliegue de un solo contenedor (anterior a P1-F2): el
# contenedor <prefijo> (caemanager-app, caemanager-staging-app) se trata como
# activa mientras no haya fichero de ranuras, drena como cualquier saliente y
# al final se borra (ya no pertenece a ningún servicio del compose). Si el
# contenedor de Caddy no tiene aún el montaje de $DIR_RANURAS, se recrea desde
# docker-compose.produccion.yml. Ese primer relevo aún corta a quien tuviera un
# circuito abierto: sus navegadores no llevan la cookie de afinidad.
#
# Variables (con sus valores reales por defecto; los tests las sustituyen):
# RAIZ_DESPLIEGUE, DIR_RANURAS, CONTENEDOR_CADDY, DRENAJE_MAX_PRODUCCION,
# DRENAJE_MAX_STAGING, DRENAJE_INTERVALO, RELEVO_ESPERA_SALUD,
# RELEVO_INTENTOS_PARADA, RELEVO_ESPERA_PARADA, RELEVO_TOPE_STOP, RELEVO_TOPE_KILL.

set -euo pipefail

RAIZ_DESPLIEGUE="${RAIZ_DESPLIEGUE:-/opt/talveg}"
DIR_RANURAS="${DIR_RANURAS:-/var/lib/talveg/caddy-ranuras}"
CONTENEDOR_CADDY="${CONTENEDOR_CADDY:-caemanager-caddy}"
# Máximo de drenaje. Producción: lo bastante para terminar lo que se tenía
# entre manos; staging: poco, comparte la máquina (4 GB) con producción y
# durante el drenaje hay dos contenedores de la app. El Caddyfile fija
# stream_close_delay por encima del máximo de producción: si se sube este, hay
# que subir aquel.
DRENAJE_MAX_PRODUCCION="${DRENAJE_MAX_PRODUCCION:-1800}"
DRENAJE_MAX_STAGING="${DRENAJE_MAX_STAGING:-300}"
DRENAJE_INTERVALO="${DRENAJE_INTERVALO:-30}"
RELEVO_ESPERA_SALUD="${RELEVO_ESPERA_SALUD:-180}"
# Parada de una saliente que ignora la guarda de correo. El tope de stop supera
# los 30 s de gracia de `docker stop -t 30`.
RELEVO_INTENTOS_PARADA="${RELEVO_INTENTOS_PARADA:-3}"
RELEVO_ESPERA_PARADA="${RELEVO_ESPERA_PARADA:-5}"
RELEVO_TOPE_STOP="${RELEVO_TOPE_STOP:-45}"
RELEVO_TOPE_KILL="${RELEVO_TOPE_KILL:-15}"
CLAVE_GUARDA_CORREO="EnvioCorreo__Modo"
ETIQUETA_GUARDA_CORREO="es.talveg.guarda-correo"
MODO_CORREO_STAGING="Cerrado"
MODO_CORREO_SIN_GUARDA="Abierto"
GUION_RELEVO="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/$(basename "${BASH_SOURCE[0]}")"

es_entorno() { [ "${1:-}" = staging ] || [ "${1:-}" = produccion ]; }
prefijo() { [ "$1" = staging ] && echo caemanager-staging-app || echo caemanager-app; }
fichero_ranuras() { echo "$DIR_RANURAS/$1.caddy"; }
max_drenaje() { [ "$1" = staging ] && echo "$DRENAJE_MAX_STAGING" || echo "$DRENAJE_MAX_PRODUCCION"; }
unidad_drenaje() { echo "talveg-drenaje-$1"; }

en_marcha() { [ "$(docker inspect -f '{{.State.Running}}' "$1" 2>/dev/null || true)" = "true" ]; }
# Falso solo si consta que no corre: parado o inexistente. A diferencia de en_marcha,
# un estado ilegible o un fallo de Docker no cuentan como parado. Lo inexistente solo
# se distingue por el mensaje de Docker: si cambiara, el error iría del lado de cerrar.
puede_estar_en_marcha() {
    local salida
    if salida="$(docker inspect -f '{{.State.Running}}' "$1" 2>/dev/null)"; then
        [ "$salida" != false ]
        return
    fi
    case "$(docker inspect -f '{{.State.Running}}' "$1" 2>&1 > /dev/null || true)" in
        *[Nn]"o such object"*|*[Nn]"o such container"*) return 1 ;;
    esac
}
# En marcha y, si tiene healthcheck, healthy. Es lo que se exige a la ranura
# que se queda sirviendo antes de retirar otra. Una salud vacía es que no tiene
# healthcheck; si la lectura falla, no está sana.
sana() {
    local salud
    en_marcha "$1" || return 1
    salud="$(docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{end}}' "$1" 2>/dev/null)" || return 1
    [ -z "$salud" ] || [ "$salud" = healthy ]
}
id_de() { docker inspect -f '{{.Id}}' "$1" 2>/dev/null || true; }

# Direcciones del fichero de ranuras, sin el puerto, una por línea (la activa
# primero). Vacío si no hay fichero.
ranuras_en_fichero() {
    local f
    f="$(fichero_ranuras "$1")"
    [ -f "$f" ] || return 0
    awk '$1 == "to" { for (i = 2; i <= NF; i++) { sub(/:8080$/, "", $i); print $i }; exit }' "$f"
}

activa() {
    local primera
    primera="$(ranuras_en_fichero "$1" | awk 'NR == 1')"
    if [ -n "$primera" ]; then
        echo "$primera"
        return 0
    fi
    # Sin fichero: despliegue anterior a P1-F2, un solo contenedor.
    if en_marcha "$(prefijo "$1")"; then
        prefijo "$1"
        return 0
    fi
    return 1
}

# escribir_ranuras ENTORNO ACTIVA [SALIENTE] — escritura atómica (mismo
# directorio + mv), porque Caddy puede leerlo en cualquier momento.
escribir_ranuras() {
    local entorno="$1" activa_="$2" saliente="${3:-}" f tmp linea
    f="$(fichero_ranuras "$entorno")"
    mkdir -p "$DIR_RANURAS"
    linea="to ${activa_}:8080"
    [ -n "$saliente" ] && linea="$linea ${saliente}:8080"
    tmp="$(mktemp "$DIR_RANURAS/.$entorno.XXXXXX")"
    printf '# Generado por deploy/relevo-app.sh (P1-F2): la primera dirección es la ranura activa.\n%s\n' "$linea" > "$tmp"
    chmod 644 "$tmp"
    mv -f "$tmp" "$f"
}

# El Caddyfile importa los ficheros de los DOS entornos: si falta uno, Caddy no
# carga la configuración. Se crea el que falte con lo que sirva ese entorno
# ahora (o el nombre anterior a P1-F2, que da 502 como hoy si no corre).
asegurar_ficheros_ranuras() {
    local e a
    for e in produccion staging; do
        [ -f "$(fichero_ranuras "$e")" ] && continue
        a="$(activa "$e" || prefijo "$e")"
        escribir_ranuras "$e" "$a"
        echo "Fichero de ranuras de $e creado: $a"
    done
}

# Sin `| grep -q`: con pipefail, grep -q sale en la primera coincidencia y el
# SIGPIPE del productor puede convertir un «sí» en fallo (141).
caddy_tiene_montaje() {
    local destinos
    destinos=" $(docker inspect -f '{{range .Mounts}}{{.Destination}} {{end}}' "$CONTENEDOR_CADDY" 2>/dev/null || true) "
    [[ "$destinos" == *" /etc/caddy/ranuras "* ]]
}

# Transición: el Caddy anterior a P1-F2 no monta $DIR_RANURAS. Recrearlo corta
# un instante los WebSocket (1001); los circuitos siguen vivos en su contenedor
# y la reconexión de Blazor los recupera.
#
# Antes de recrearlo se valida el Caddyfile del checkout con un Caddy
# desechable (misma imagen, mismos ficheros de ranuras, mismo DOMINIO): si no
# valida, no se recrea, porque un Caddy que no arranca deja sin proxy a los dos
# entornos. El Caddy nuevo arranca con el Caddyfile aprobado si existe, y si
# no, con el del checkout (compose). Es el que se acaba de validar.
validar_caddyfile() {
    local imagen dominio acme
    imagen="$(docker inspect -f '{{.Config.Image}}' "$CONTENEDOR_CADDY" 2>/dev/null)" || return 1
    dominio="$(docker exec "$CONTENEDOR_CADDY" printenv DOMINIO 2>/dev/null || true)"
    acme="$(docker exec "$CONTENEDOR_CADDY" printenv ACME_EMAIL 2>/dev/null || true)"
    docker run --rm -i --network none -e DOMINIO="$dominio" -e ACME_EMAIL="$acme" \
        -v "$DIR_RANURAS:/etc/caddy/ranuras:ro" --entrypoint caddy "$imagen" \
        validate --config /dev/stdin --adapter caddyfile < "$1"
}

asegurar_montaje_caddy() {
    docker inspect "$CONTENEDOR_CADDY" > /dev/null 2>&1 || return 0
    caddy_tiene_montaje && return 0
    echo "Caddy sin el montaje de ranuras (anterior a P1-F2): se valida el Caddyfile y se recrea desde docker-compose.produccion.yml."
    local fuente="$RAIZ_DESPLIEGUE/deploy/local/Caddyfile"
    [ -f "$(caddyfile_aprobado)" ] && fuente="$(caddyfile_aprobado)"
    if ! validar_caddyfile "$fuente"; then
        echo "El Caddyfile con el que arrancaría Caddy ($fuente) no valida: no se recrea Caddy (sigue el anterior)." >&2
        return 1
    fi
    ( cd "$RAIZ_DESPLIEGUE/deploy/local" && docker compose -f docker-compose.produccion.yml up -d --no-deps --no-build caddy )
}

# Recarga por stdin: el Caddyfile montado es un bind de fichero suelto, y tras
# un `git checkout` el contenedor sigue viendo el inodo viejo. `docker exec`
# hereda el entorno del contenedor (DOMINIO, ACME_EMAIL).
#
# QUÉ Caddyfile: Caddy es uno solo para los dos entornos, y cualquier recarga
# aplica el fichero entero, bloque de producción incluido. Solo `desplegar
# produccion` —el único paso que ha pasado la aprobación de producción— usa el
# del checkout, y si Caddy lo acepta lo guarda como aprobado. Todo lo demás
# (desplegar staging, cualquier drenaje) recarga con el aprobado: si no, un
# despliegue de staging, o el checkout que dejó staging, metería en
# producción un Caddyfile que producción no ha aprobado.
#
# ARRANQUE: la primera vez no hay aprobado. Se usa el del checkout y se guarda
# como aprobado, con aviso. Eso pasa una sola vez, en el primer relevo tras
# P1-F2 —el de staging, que el procedimiento de transición hace ir primero y
# que la coordinación revisa antes de aprobar producción— y su checkout es el
# Caddyfile de P1-F2. Si alguien borra el aprobado, vuelve a pasar: por eso el
# aviso.
#
# La copia que se va a guardar se prepara ANTES de recargar: si no se puede
# preparar, no se recarga; y si se recarga, guardarla es un `mv` en el mismo
# directorio. Así nunca queda aplicada una configuración que el aprobado no
# refleje y que una recarga posterior de staging desharía.
caddyfile_aprobado() { echo "$DIR_RANURAS/Caddyfile.aprobado"; }
recargar_caddy() {
    local entorno_checkout="${1:-}" fuente aprobado tmp=""
    aprobado="$(caddyfile_aprobado)"
    # Sin aprobado y sin entorno (recarga que solo cambia ranuras: la previa
    # al up, el fin de un drenaje, `recargar`): se recarga con el Caddyfile
    # con el que corre Caddy, su /etc/caddy/Caddyfile montado (un bind de
    # fichero suelto conserva el inodo con el que arrancó). Así no entra el
    # checkout ni se crea el aprobado antes de un up que puede fallar
    # (revisión de Codex, pasada 4). El arranque del aprobado queda para la
    # conmutación.
    if [ -z "$entorno_checkout" ] && [ ! -f "$aprobado" ]; then
        docker exec "$CONTENEDOR_CADDY" caddy reload --config /etc/caddy/Caddyfile --adapter caddyfile
        return
    fi
    if [ "$entorno_checkout" = produccion ] || [ ! -f "$aprobado" ]; then
        fuente="$RAIZ_DESPLIEGUE/deploy/local/Caddyfile"
        [ -f "$aprobado" ] || echo "::warning::no hay Caddyfile aprobado: se usa el del checkout y queda como aprobado (arranque de P1-F2)." >&2
        tmp="$(mktemp "$DIR_RANURAS/.Caddyfile.XXXXXX")" && cp "$fuente" "$tmp" && chmod 644 "$tmp" \
            || { rm -f "$tmp"; echo "No se pudo preparar la copia del Caddyfile aprobado: no se recarga." >&2; return 1; }
    else
        fuente="$aprobado"
    fi
    if ! docker exec -i "$CONTENEDOR_CADDY" caddy reload --config /dev/stdin --adapter caddyfile < "$fuente"; then
        [ -n "$tmp" ] && rm -f "$tmp"
        return 1
    fi
    # Un `mv` fallido tiene que ser un fallo: si no, el aprobado se queda en
    # la versión anterior y la siguiente recarga, o el siguiente arranque de
    # Caddy, desharía esta (revisión de Codex). Quien llama restaura.
    if [ -n "$tmp" ] && ! mv -f "$tmp" "$aprobado"; then
        rm -f "$tmp"
        echo "Caddy aceptó la recarga, pero no se pudo guardar el Caddyfile aprobado." >&2
        return 1
    fi
    return 0
}

detener_drenaje_previo() {
    command -v systemctl > /dev/null 2>&1 || return 0
    systemctl stop "$(unidad_drenaje "$1")" > /dev/null 2>&1 || true
}

lanzar_drenaje() {
    local entorno="$1" contenedor="$2" id="$3" max
    max="$(max_drenaje "$entorno")"
    if command -v systemd-run > /dev/null 2>&1; then
        # Unidad transitoria: sobrevive al fin de la sesión SSH del despliegue,
        # deja su salida en journald y su nombre fijo impide dos drenajes del
        # mismo entorno a la vez.
        # `|| return 1` explícito: se llama a la izquierda de `||`, donde
        # errexit no actúa, y sin él un fallo acababa en «lanzado».
        systemd-run --unit "$(unidad_drenaje "$entorno")" --collect --quiet \
            --setenv=RAIZ_DESPLIEGUE="$RAIZ_DESPLIEGUE" --setenv=DIR_RANURAS="$DIR_RANURAS" \
            --setenv=CONTENEDOR_CADDY="$CONTENEDOR_CADDY" --setenv=DRENAJE_INTERVALO="$DRENAJE_INTERVALO" \
            bash "$GUION_RELEVO" drenar "$entorno" "$contenedor" "$id" "$max" || return 1
        echo "Drenaje de $contenedor lanzado (máx. ${max} s): journalctl -u $(unidad_drenaje "$entorno")"
    else
        # 9>&-: el hijo no debe heredar el cerrojo de despliegue que tiene quien
        # llama, o lo retendría durante todo el drenaje.
        setsid nohup bash "$GUION_RELEVO" drenar "$entorno" "$contenedor" "$id" "$max" \
            >> "/var/log/$(unidad_drenaje "$entorno").log" 2>&1 < /dev/null 9>&- &
        echo "Drenaje de $contenedor lanzado (máx. ${max} s): /var/log/$(unidad_drenaje "$entorno").log"
    fi
}

volcar_ranura_fallida() {
    local contenedor="$1"
    echo "=== docker logs --tail 300 ${contenedor} ===" >&2
    docker logs --tail 300 "$contenedor" >&2 || true
    docker stop "$contenedor" > /dev/null 2>&1 || true
}

# Lee `docker compose config` por stdin y mira si <servicio> lleva <clave> con
# valor en su `environment:`. Sale con 0 si lo lleva, 1 si no y 2 si el servicio
# no aparece (config falló, o el instrumento no observa lo que debe). Lee la
# salida entera y no imprime nada: esa salida lleva todas las credenciales del
# entorno, y el valor no sale de awk. Vacío, solo espacios y null cuentan como
# ausente, igual que los trata la aplicación.
#
# Con un tercer argumento, además sale con 3 si la clave no vale exactamente eso,
# sin distinguir mayúsculas en el nombre de la clave: la aplicación lee cualquier
# grafía, así que otra línea con el mismo nombre podría decidir el modo efectivo.
clave_con_valor_en_servicio() {
    awk -v servicio="$1" -v clave="$2" -v exigido="${3:-}" '
        function valor_de(linea) {
            linea = substr(linea, length("      " clave ":") + 1)
            gsub(/[ \t\r"]/, "", linea)
            gsub("\047", "", linea)
            return linea
        }
        /^[^ ]/ { en_servicios = ($0 == "services:"); actual = 0; en_entorno = 0; next }
        en_servicios && /^  [^ ]/ { actual = ($0 == "  " servicio ":"); if (actual) visto = 1; en_entorno = 0; next }
        actual && /^    [^ ]/ { en_entorno = ($0 == "    environment:"); next }
        en_entorno && exigido != "" && index(tolower($0), "      " tolower(clave) ":") == 1 {
            valor = valor_de($0)
            if (valor != exigido) discrepa = 1
            valor = ""
        }
        en_entorno && index($0, "      " clave ":") == 1 {
            valor = valor_de($0)
            presente = (valor != "" && valor != "null" && valor != "~")
            valor = ""
        }
        END { if (!visto) exit 2; if (!presente) exit 1; exit discrepa ? 3 : 0 }'
}

# Guarda de envío de correo (C-04). Sin EnvioCorreo__Modo la aplicación arranca
# cerrada, y un `.env` que no la lleve dejaría sin activación, recuperación de
# contraseña ni avisos de seguridad con solo un aviso en el log, después de
# conmutar. El cierre por defecto de la aplicación no cambia y aquí no se da
# valor a la clave: se exige que alguien la haya decidido en el `.env` del
# entorno.
#
# En staging se exige además que el valor sea Cerrado: staging arranca como
# Production, nada más lo distingue, y una copia del `.env` de producción dejaría
# salir correo real a destinatarios reales. El valor no se imprime. En producción
# no se valida: uno ilegible impide arrancar (ValidateOnStart) y lo resuelve el
# `up`.
#
# Mide la configuración efectiva de la ranura que va a arrancar, la misma que
# usará el `up`: entra lo que aporten env_file y environment y queda fuera un
# comentario. Va antes de tocar nada, así que abortar deja sirviendo lo que ya
# servía y sin migrar. stderr se descarta porque un error de análisis del `.env`
# puede citar la línea.
#
# También cubre volver-atras.sh, que la pide con `comprobar-guarda` antes de
# restaurar nada y vuelve a pasar por ella al entrar en `desplegar`. Los mensajes
# hablan solo del relevo: quien llama responde de lo que haya hecho antes.
#
# El modo solo protege en una imagen que lo interprete. Una anterior a la guarda
# registra SMTP y Microsoft 365 sin envolver, así que se comporta como Abierto
# aunque el `.env` diga otra cosa. Por eso se mira la etiqueta del Dockerfile
# (ETIQUETA_GUARDA_CORREO), que lleva lo que declara esa imagen y no lo que el
# guion supone: sin ella, o si no se puede leer, staging no arranca y producción
# solo arranca si el modo efectivo es Abierto, que es lo que esa imagen haría.
#
# Lee imagen (caemanager:<sha>) o contenedor, que hereda las etiquetas de la suya.
# Se compara con 1: una etiqueta ausente imprime «<no value>» y un fallo de inspect
# no imprime nada, y ambos cuentan como «no declara».
declara_guarda_de_correo() {
    [ "$(docker inspect -f "{{index .Config.Labels \"$ETIQUETA_GUARDA_CORREO\"}}" "$1" 2>/dev/null || true)" = 1 ]
}

# Resuelve la configuración efectiva de <servicio> con la imagen <sha> y sale con
# el código de clave_con_valor_en_servicio.
medir_guarda_de_correo() {
    local entorno="$1" sha="$2" servicio="$3" modo_exigido="$4"
    local -a args
    args=(-f "docker-compose.$entorno.yml")
    [ "$entorno" = staging ] && args+=(--env-file .env.staging)

    ( cd "$RAIZ_DESPLIEGUE/deploy/local" \
        && IMAGEN_TAG="$sha" docker compose "${args[@]}" --profile ranura config 2>/dev/null ) \
        | clave_con_valor_en_servicio "$servicio" "$CLAVE_GUARDA_CORREO" "$modo_exigido"
}

exigir_guarda_de_correo() {
    local entorno="$1" sha="$2" servicio="$3" fichero=.env modo_exigido="" resultado=0
    local intacto="El relevo no ha tocado Caddy, el fichero de ranuras, el migrador ni los contenedores: sigue sirviendo la ranura de antes."
    [ "$entorno" = staging ] && fichero=.env.staging
    [ "$entorno" = staging ] && modo_exigido="$MODO_CORREO_STAGING"

    if ! declara_guarda_de_correo "caemanager:$sha"; then
        if [ "$entorno" = staging ]; then
            echo "::error::Relevo de staging abortado: caemanager:$sha no declara $ETIQUETA_GUARDA_CORREO (es anterior a la guarda o no se pudo leer). Ignoraría $CLAVE_GUARDA_CORREO y enviaría correo real con lo que tenga configurado, así que staging no la arranca: elige una imagen que la declare. $intacto" >&2
            return 1
        fi
        modo_exigido="$MODO_CORREO_SIN_GUARDA"
    fi

    medir_guarda_de_correo "$entorno" "$sha" "$servicio" "$modo_exigido" || resultado=$?

    case "$resultado" in
        0) return 0 ;;
        2) echo "::error::Relevo de $entorno abortado: no se pudo resolver la configuración efectiva de $servicio para comprobar $CLAVE_GUARDA_CORREO (revisa $fichero y docker-compose.$entorno.yml; el error de Compose no se imprime porque puede citar valores). $intacto" >&2 ;;
        3)
            if [ "$entorno" = staging ]; then
                echo "::error::Relevo de $entorno abortado: $CLAVE_GUARDA_CORREO no es $MODO_CORREO_STAGING en el entorno efectivo de $servicio, y en staging no se admite otro modo, para que no salga correo real. Corrígelo en $fichero en el servidor y repite la operación. $intacto" >&2
            else
                echo "::error::Relevo de $entorno abortado: caemanager:$sha no declara $ETIQUETA_GUARDA_CORREO y $CLAVE_GUARDA_CORREO no es $MODO_CORREO_SIN_GUARDA en el entorno efectivo de $servicio: esa imagen ignoraría el modo y enviaría correo igualmente. Elige una imagen que declare la guarda. $intacto" >&2
            fi ;;
        *) echo "::error::Relevo de $entorno abortado: falta $CLAVE_GUARDA_CORREO, o no tiene valor, en el entorno efectivo de $servicio. Añádela a $fichero en el servidor y repite la operación. Sin ella no sale ningún correo: ni activación, ni recuperación de contraseña, ni avisos de seguridad. $intacto" >&2 ;;
    esac
    return 1
}

# La saliente no interpreta el modo y el modo no es Abierto: mientras drena
# seguiría enviando con el `.env` con el que arrancó. La primera vez que un entorno
# releva a una imagen con la guarda, la saliente siempre es de estas en staging. Si
# el modo no se puede medir, cuenta como que no es Abierto.
saliente_ignora_la_guarda() {
    local entorno="$1" sha="$2" servicio="$3" saliente="$4"
    declara_guarda_de_correo "$saliente" && return 1
    ! medir_guarda_de_correo "$entorno" "$sha" "$servicio" "$MODO_CORREO_SIN_GUARDA"
}

parada_confirmada() { ! puede_estar_en_marcha "$1"; }

# Sigue parada tras una pausa: una política de reinicio la devolvería a la vida.
parada_estable() {
    parada_confirmada "$1" || return 1
    sleep "$RELEVO_ESPERA_PARADA"
    parada_confirmada "$1"
}

# Para el contenedor o devuelve 1: hasta RELEVO_INTENTOS_PARADA vueltas de stop y
# kill, cada orden con su tope (unos 4 min en el peor caso). Primero le quita la
# política de reinicio, porque `restart: unless-stopped` podría arrancarlo tras el
# kill. El código de salida de esas órdenes no decide: decide lo que dice el
# contenedor después.
parar_con_tope() {
    local contenedor="$1" intento
    for (( intento = 1; intento <= RELEVO_INTENTOS_PARADA; intento++ )); do
        timeout "$RELEVO_TOPE_KILL" docker update --restart=no "$contenedor" > /dev/null 2>&1 || true
        timeout "$RELEVO_TOPE_STOP" docker stop -t 30 "$contenedor" > /dev/null 2>&1 || true
        parada_estable "$contenedor" && return 0
        timeout "$RELEVO_TOPE_KILL" docker kill "$contenedor" > /dev/null 2>&1 || true
        parada_estable "$contenedor" && return 0
        [ "$intento" -lt "$RELEVO_INTENTOS_PARADA" ] && sleep "$RELEVO_ESPERA_PARADA"
    done
    return 1
}

# Retira una saliente que ignora la guarda. Como en retirar_saliente, nunca si la
# que se queda no está sana: dejaría el entorno sin servir. Devuelve 1, con
# ::error::, si la saliente sigue viva. Con un tercer argumento, el entorno, además
# la saca de Caddy: va aquí para que una sola comprobación de `sana` decida las dos
# cosas. Un fallo de Caddy no impide la parada: la cookie vieja reintenta en la nueva.
retirar_sin_guarda() {
    local contenedor="$1" nueva="$2" entorno_en_caddy="${3:-}"
    if ! sana "$nueva"; then
        echo "::error::$contenedor ignora la guarda de correo y sigue en marcha: no se para porque $nueva no está sana y dejaría el entorno sin servir." >&2
        return 1
    fi
    if [ -n "$entorno_en_caddy" ]; then
        { escribir_ranuras "$entorno_en_caddy" "$nueva" && recargar_caddy; } \
            || echo "::warning::no se pudo sacar $contenedor de Caddy; la cookie vieja reintentará en la activa." >&2
    fi
    if ! parar_con_tope "$contenedor"; then
        echo "::error::no se pudo parar $contenedor, que ignora la guarda de correo, tras $RELEVO_INTENTOS_PARADA intentos de stop y kill." >&2
        return 1
    fi
    case "$contenedor" in
        *-azul|*-verde) ;;
        *) docker rm "$contenedor" > /dev/null 2>&1 || true ;;
    esac
    echo "$contenedor retirada."
}

# Contenedores del entorno fuera de <anotados> que ignoran la guarda y pueden seguir
# vivos. El fichero de ranuras se reescribe antes de confirmar la parada, así que un
# relevo fallido pudo borrar de él una imagen viva, y fiarse de lo anotado daría este
# por bueno. Se miran los tres nombres posibles: una ranura la recrearía el up, pero
# el contenedor anterior a las ranuras no pertenece a ningún servicio del compose.
sin_guarda_sin_anotar() {
    local entorno="$1" sha="$2" servicio="$3" anotados="$4" base c
    base="$(prefijo "$entorno")"
    for c in "$base" "$base-azul" "$base-verde"; do
        [[ $'\n'"$anotados"$'\n' == *$'\n'"$c"$'\n'* ]] && continue
        if puede_estar_en_marcha "$c" && saliente_ignora_la_guarda "$entorno" "$sha" "$servicio" "$c"; then
            echo "$c"
        fi
    done
}

# Para quien da por bueno un <sha> que ya sirve sin pasar por `desplegar`: un relevo
# a ese sha pudo conmutar y acabar en error con una imagen sin guarda viva. Con el
# cerrojo de despliegue tomado por quien llama. Devuelve 1 si alguna sigue viva, y
# también si la propia activa ignora la guarda. Se queda la primera del fichero, la
# que quien llama comprobó. Nada se para sin leer el modo, sin esa activa sana y sin
# que Caddy acepte servirla: un error visible es preferible a cortar el servicio.
retirar_sin_guarda_pendientes() {
    local entorno="$1" sha="$2" activa_ servicio candidatas c modo=0 fallo=0
    activa_="$(activa "$entorno" || true)"
    if [ -z "$activa_" ]; then
        echo "::error::$entorno no tiene ninguna ranura en marcha: no se pueden retirar las imágenes que ignoran la guarda de correo." >&2
        return 1
    fi
    servicio="app-$(ranura_libre "$activa_")"
    if saliente_ignora_la_guarda "$entorno" "$sha" "$servicio" "$activa_"; then
        echo "::error::$activa_ sirve $entorno e ignora la guarda de correo con un modo distinto de $MODO_CORREO_SIN_GUARDA o que no se pudo leer: puede enviar correo." >&2
        return 1
    fi
    candidatas="$(sin_guarda_sin_anotar "$entorno" "$sha" "$servicio" "$activa_")"
    [ -n "$candidatas" ] || return 0

    medir_guarda_de_correo "$entorno" "$sha" "$servicio" "$MODO_CORREO_SIN_GUARDA" || modo=$?
    if [ "$modo" != 3 ]; then
        echo "::error::no se pudo leer $CLAVE_GUARDA_CORREO de $entorno: no se para nada, y sigue en marcha $candidatas, que ignora la guarda de correo." >&2
        return 1
    fi
    if ! sana "$activa_"; then
        echo "::error::$activa_ no está sana: no se para nada, y sigue en marcha $candidatas, que ignora la guarda de correo." >&2
        return 1
    fi
    if ! escribir_ranuras "$entorno" "$activa_" $(ranuras_en_fichero "$entorno" | grep -vx -- "$activa_" | grep -Fxv -- "$candidatas" | awk 'NR == 1' || true) || ! recargar_caddy; then
        echo "::error::no se pudo confirmar que Caddy sirve $activa_: no se para nada, y sigue en marcha $candidatas, que ignora la guarda de correo." >&2
        return 1
    fi
    for c in $candidatas; do
        retirar_sin_guarda "$c" "$activa_" || fallo=1
    done
    return "$fallo"
}

# La ranura que sirve de verdad: la primera del fichero que esté SANA (en marcha
# y healthy); si ninguna lo está, la primera en marcha. Si la activa murió o no
# responde y queda una saliente sana (Caddy ya la usa por el reintento), esa es
# la que hay que conservar, y la nueva va en la ranura de la otra, no en la de
# la sana. Imprime el contenedor, o nada si no hay ninguno.
ranura_que_sirve() {
    local entorno="$1" activa_="" candidata primera_en_marcha=""
    for candidata in $(ranuras_en_fichero "$entorno"); do
        if sana "$candidata"; then activa_="$candidata"; break; fi
        [ -z "$primera_en_marcha" ] && en_marcha "$candidata" && primera_en_marcha="$candidata"
    done
    [ -n "$activa_" ] || activa_="$primera_en_marcha"
    if [ -z "$activa_" ] && [ ! -f "$(fichero_ranuras "$entorno")" ] && en_marcha "$(prefijo "$entorno")"; then
        activa_="$(prefijo "$entorno")"   # sin fichero: el contenedor único anterior a P1-F2
    fi
    echo "$activa_"
}

# Ranura en la que arranca la versión nueva: la otra que la que sirve.
ranura_libre() {
    case "$1" in
        *-azul) echo verde ;;
        *) echo azul ;;
    esac
}

comprobar_guarda() {
    local entorno="$1" sha="$2" nueva
    nueva="$(ranura_libre "$(ranura_que_sirve "$entorno")")"
    exigir_guarda_de_correo "$entorno" "$sha" "app-$nueva"
}

desplegar() {
    local entorno="$1" sha="$2" activa_="" nueva cont_nueva id_activa servicios previas previa sin_guarda_viva=0
    local -a args
    args=(-f "docker-compose.$entorno.yml")
    [ "$entorno" = staging ] && args+=(--env-file .env.staging)

    activa_="$(ranura_que_sirve "$entorno")"
    nueva="$(ranura_libre "$activa_")"
    cont_nueva="$(prefijo "$entorno")-$nueva"
    echo "Relevo de $entorno: activa ${activa_:-<ninguna>}, nueva $cont_nueva (caemanager:$sha)."

    exigir_guarda_de_correo "$entorno" "$sha" "app-$nueva" || return 1

    asegurar_ficheros_ranuras
    asegurar_montaje_caddy

    # Salientes de un relevo anterior que siguen en el fichero (la ranura libre
    # si aún drenaba, o una cuyo drenaje murió, p. ej. tras reiniciar el VPS).
    # ANTES del up solo se saca de Caddy la ranura libre, porque el up la
    # recrea, y solo con una activa sana. Las demás se paran DESPUÉS de
    # conmutar: si la nueva no llega a sana, siguen ahí (pueden ser lo único
    # que sirve). El drenaje anterior no se para aquí: solo actúa bajo el
    # cerrojo, que tiene este despliegue, y si su contenedor se recrea, lo
    # detecta por el Id y termina.
    previas="$(ranuras_en_fichero "$entorno" | grep -vx -- "${activa_:-<ninguna>}" || true)"
    if [ -n "$activa_" ] && sana "$activa_" && [[ $'\n'"$previas"$'\n' == *$'\n'"$cont_nueva"$'\n'* ]]; then
        escribir_ranuras "$entorno" "$activa_" \
            $(printf '%s\n' "$previas" | grep -vx -- "$cont_nueva" | awk 'NR == 1')
        # Con el aprobado, no con el del checkout: esta recarga solo cambia
        # las ranuras. Si aplicara aquí el Caddyfile nuevo y luego fallara el
        # up, Caddy quedaría con un Caddyfile que el aprobado no refleja
        # (revisión de Codex, pasada 3). El nuevo entra solo en la
        # conmutación, que sí restaura si algo falla.
        recargar_caddy || echo "::warning::no se pudo recargar Caddy al sacar $cont_nueva, que se va a recrear; la cookie vieja reintentará en la activa."
    fi

    cd "$RAIZ_DESPLIEGUE/deploy/local"
    export IMAGEN_TAG="$sha"
    # Servicios sin perfil (db, migrador, seq y caddy en producción) más la
    # ranura nueva. La activa no se nombra: Compose no la toca.
    mapfile -t servicios < <(docker compose "${args[@]}" config --services)
    if ! docker compose "${args[@]}" up -d --wait --wait-timeout "$RELEVO_ESPERA_SALUD" --no-build \
            "${servicios[@]}" "app-$nueva"; then
        echo "La ranura nueva $cont_nueva no llegó a sana: Caddy sigue en ${activa_:-<ninguna>}." >&2
        volcar_ranura_fallida "$cont_nueva"
        return 1
    fi
    if [ "$(docker inspect -f '{{.Config.Image}}' "$cont_nueva" 2>/dev/null || true)" != "caemanager:$sha" ]; then
        echo "$cont_nueva no corre caemanager:$sha tras el up: no se conmuta." >&2
        return 1
    fi

    local -a antes
    mapfile -t antes < <(ranuras_en_fichero "$entorno")
    escribir_ranuras "$entorno" "$cont_nueva" "$activa_"
    if ! recargar_caddy "$entorno"; then
        echo "Caddy no aceptó la conmutación a $cont_nueva: se restaura el fichero de ranuras anterior." >&2
        if [ "${#antes[@]}" -gt 0 ]; then
            escribir_ranuras "$entorno" "${antes[0]}" "${antes[1]:-}"
            recargar_caddy || echo "::warning::tampoco se pudo recargar Caddy con las ranuras anteriores: revisa 'docker logs $CONTENEDOR_CADDY'." >&2
        fi
        docker stop "$cont_nueva" > /dev/null 2>&1 || true
        return 1
    fi
    echo "Caddy conmutado: $entorno sirve $cont_nueva (caemanager:$sha)."

    # Salientes de relevos anteriores que no son la activa ni la nueva: ya
    # fuera de Caddy, se paran ahora que la nueva sirve. Una que consta parada o
    # que ya no existe no puede enviar, y su etiqueta quizá ni se pueda leer.
    previas+=$'\n'"$(sin_guarda_sin_anotar "$entorno" "$sha" "app-$nueva" "$cont_nueva"$'\n'"$activa_"$'\n'"$previas")"
    for previa in $previas; do
        [ "$previa" = "$cont_nueva" ] && continue
        echo "Saliente de un relevo anterior: se retira $previa."
        if puede_estar_en_marcha "$previa" && saliente_ignora_la_guarda "$entorno" "$sha" "app-$nueva" "$previa"; then
            retirar_sin_guarda "$previa" "$cont_nueva" || sin_guarda_viva=1
            continue
        fi
        docker stop -t 30 "$previa" > /dev/null 2>&1 \
            || echo "::warning::no se pudo parar $previa: sigue en marcha fuera de Caddy; párala a mano."
        case "$previa" in
            *-azul|*-verde) ;;
            *) docker rm "$previa" > /dev/null 2>&1 || true ;;
        esac
    done

    if [ -n "$activa_" ]; then
        detener_drenaje_previo "$entorno"
        # El drenaje dejaría una ventana de envío real, y puede aplazarse sin
        # límite (retirar_saliente), así que esta saliente nunca pasa por él: o
        # queda parada aquí, o el relevo acaba en error.
        if saliente_ignora_la_guarda "$entorno" "$sha" "app-$nueva" "$activa_"; then
            echo "$activa_ no declara la guarda de correo y el modo no es $MODO_CORREO_SIN_GUARDA: se retira sin drenar."
            retirar_sin_guarda "$activa_" "$cont_nueva" "$entorno" || sin_guarda_viva=1
        else
            id_activa="$(id_de "$activa_")"
            lanzar_drenaje "$entorno" "$activa_" "$id_activa" \
                || echo "::warning::no se pudo lanzar el drenaje de $activa_: sigue en marcha y con sus circuitos; páralo a mano cuando se vacíe (runbook)."
        fi
    fi

    # La nueva ya sirve, pero el relevo no se da por bueno: quien llama no lo
    # registra y queda en rojo. Repetir el despliegue la vuelve a buscar aunque
    # ya no figure en el fichero de ranuras.
    if [ "$sin_guarda_viva" != 0 ]; then
        echo "::error::Relevo de $entorno incompleto: $cont_nueva ya conmutó, pero sigue en marcha una imagen que ignora $CLAVE_GUARDA_CORREO y puede enviar correo. Párala a mano, o repite el despliegue, que vuelve a intentarlo." >&2
        return 1
    fi
}

# Recarga Caddy con el fichero de ranuras tal como está y el Caddyfile
# aprobado. Para cuando el fichero y la configuración cargada pueden no
# coincidir (un relevo interrumpido entre escribir y recargar): la usa
# volver-atras.sh antes de dar por buena una ranura que ya figura como activa.
recargar() {
    recargar_caddy
}

# Conexiones TCP establecidas al 8080 del contenedor, sin las de loopback
# (el healthcheck). Imprime el número, o nada si no se pudo leer.
conexiones_establecidas() {
    local tablas
    tablas="$(docker exec "$1" cat /proc/net/tcp /proc/net/tcp6 2>/dev/null)" || return 0
    printf '%s\n' "$tablas" | awk '
        $2 ~ /:1F90$/ && $4 == "01" && $3 !~ /^0100007F:/ && $3 !~ /^0000000000000000FFFF00000100007F:/ && $3 !~ /^00000000000000000000000001000000:/ { n++ }
        END { print n + 0 }'
}

retirar_saliente() {
    local entorno="$1" contenedor="$2" id="$3" a
    exec 9>"$RAIZ_DESPLIEGUE/deploy/.ci-deploy.lock"
    if ! flock -w 600 9; then
        echo "Sin cerrojo de despliegue en 10 min: $contenedor sigue en marcha; se reintenta en la próxima vuelta." >&2
        exec 9>&-
        return 1
    fi
    if [ "$(id_de "$contenedor")" != "$id" ]; then
        echo "$contenedor ya no es el contenedor que drenaba (se recreó): nada que hacer."
        return 0
    fi
    a="$(activa "$entorno" || true)"
    if [ "$a" = "$contenedor" ]; then
        echo "$contenedor es la ranura activa de $entorno: no se retira." >&2
        return 0
    fi
    # Nunca se retira la saliente si la que se queda no está sana: si la nueva
    # murió tras pasar /salud, Caddy sirve por la saliente (reintento) y
    # pararla dejaría el entorno sin servir. Se aplaza y se reintenta en la
    # siguiente vuelta; si la activa no se recupera, lo resuelve el siguiente
    # despliegue (que conserva la viva).
    if [ -z "$a" ] || ! sana "$a"; then
        echo "::warning::la ranura activa de $entorno (${a:-<ninguna>}) no está sana: $contenedor no se retira todavía." >&2
        exec 9>&-
        return 1
    fi
    escribir_ranuras "$entorno" "$a"
    recargar_caddy || echo "::warning::no se pudo recargar Caddy al retirar $contenedor; la cookie vieja reintentará en la activa." >&2
    # `if !` explícito: esta función se llama a la izquierda de `&&`, donde
    # errexit no actúa, y un stop fallido acababa en «retirado».
    if ! docker stop -t 30 "$contenedor" > /dev/null; then
        echo "::warning::no se pudo parar $contenedor: se reintenta en la próxima vuelta." >&2
        exec 9>&-
        return 1
    fi
    case "$contenedor" in
        *-azul|*-verde) ;;
        *) docker rm "$contenedor" > /dev/null ;;   # anterior a P1-F2: sin servicio en el compose
    esac
    echo "$contenedor retirado de $entorno."
}

drenar() {
    local entorno="$1" contenedor="$2" id="$3" max="$4" inicio="$SECONDS" vacias=0 n
    echo "Drenando $contenedor ($entorno), máx. ${max} s."
    while true; do
        if [ "$(id_de "$contenedor")" != "$id" ] || ! en_marcha "$contenedor"; then
            echo "$contenedor ya no está en marcha o se recreó: fin del drenaje."
            return 0
        fi
        n="$(conexiones_establecidas "$contenedor")"
        if [ "$n" = "0" ]; then vacias=$((vacias + 1)); else vacias=0; fi
        echo "$(( SECONDS - inicio )) s: ${n:-?} conexiones."
        if [ "$vacias" -ge 2 ] || [ $(( SECONDS - inicio )) -ge "$max" ]; then
            retirar_saliente "$entorno" "$contenedor" "$id" && return 0
        fi
        sleep "$DRENAJE_INTERVALO"
    done
}

main_relevo() {
    local orden="${1:-}"
    case "$orden" in
        desplegar)
            es_entorno "${2:-}" && [[ "${3:-}" =~ ^[0-9a-f]{40}$ ]] && [ $# -eq 3 ] \
                || { echo "uso: relevo-app.sh desplegar <staging|produccion> <sha de 40>" >&2; exit 2; }
            desplegar "$2" "$3" ;;
        comprobar-guarda)
            es_entorno "${2:-}" && [[ "${3:-}" =~ ^[0-9a-f]{40}$ ]] && [ $# -eq 3 ] \
                || { echo "uso: relevo-app.sh comprobar-guarda <staging|produccion> <sha de 40>" >&2; exit 2; }
            comprobar_guarda "$2" "$3" ;;
        retirar-sin-guarda)
            es_entorno "${2:-}" && [[ "${3:-}" =~ ^[0-9a-f]{40}$ ]] && [ $# -eq 3 ] \
                || { echo "uso: relevo-app.sh retirar-sin-guarda <staging|produccion> <sha de 40>" >&2; exit 2; }
            retirar_sin_guarda_pendientes "$2" "$3" ;;
        activa)
            es_entorno "${2:-}" && [ $# -eq 2 ] || { echo "uso: relevo-app.sh activa <staging|produccion>" >&2; exit 2; }
            activa "$2" ;;
        recargar)
            es_entorno "${2:-}" && [ $# -eq 2 ] || { echo "uso: relevo-app.sh recargar <staging|produccion>" >&2; exit 2; }
            recargar ;;
        drenar)
            es_entorno "${2:-}" && [ -n "${3:-}" ] && [ -n "${4:-}" ] && [[ "${5:-}" =~ ^[0-9]+$ ]] && [ $# -eq 5 ] \
                || { echo "uso: relevo-app.sh drenar <entorno> <contenedor> <id> <max_s>" >&2; exit 2; }
            drenar "$2" "$3" "$4" "$5" ;;
        *)
            echo "uso: relevo-app.sh <desplegar|comprobar-guarda|retirar-sin-guarda|activa|recargar|drenar> ..." >&2; exit 2 ;;
    esac
}

if [ "${BASH_SOURCE[0]}" = "$0" ]; then
    main_relevo "$@"
fi
