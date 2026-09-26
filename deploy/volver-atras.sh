#!/bin/bash
# Vuelta atrás de un entorno a una imagen caemanager:<sha> ya presente en el
# VPS, sin reconstruir nada (P1-F1).
#
# Uso (en el VPS, como root):
#   bash /opt/talveg/deploy/volver-atras.sh <staging|produccion> <sha|anterior>
#
# `anterior` es la imagen desplegada en ese entorno justo antes de la que está
# en marcha, según el historial que ci-deploy.sh registra en cada despliegue
# sano (deploy/imagenes-retenidas.sh). Con un SHA explícito se vuelve a esa
# imagen, que tiene que estar cargada en Docker: este guion nunca construye,
# nunca descarga y nunca hace checkout de /opt/talveg (el checkout traería con
# él un ci-deploy.sh viejo, que es el comando forzado de la clave SSH).
#
# Orden, y en cada paso se detiene con un mensaje y código distinto de 0:
#   1. Cerrojo de despliegue (el mismo que ci-deploy.sh): nunca a la vez que un
#      despliegue ni que otra vuelta atrás.
#   2. La imagen existe y su etiqueta OCI de revisión es ese SHA.
#   3. ESQUEMA: la lista de migraciones de EF de la imagen (etiqueta
#      es.talveg.migraciones-ef, ver listar-migraciones-ef.sh) tiene que ser
#      exactamente la de `__EFMigrationsHistory`. Si la base tiene migraciones
#      que la imagen no conoce, volver exigiría deshacer esquema, y este guion
#      NUNCA hace un downgrade: se detiene. Si la imagen trae migraciones que la
#      base no tiene, arrancarla aplicaría esquema nuevo: eso es un despliegue,
#      no una vuelta atrás, y también se detiene. Una imagen sin la etiqueta
#      (construida antes de P1-F1) no se puede comprobar: se detiene.
#   4. `docker compose up -d --wait --no-build` con IMAGEN_TAG=<sha>, igual que
#      ci-deploy.sh.
#   5. SALUD: el contenedor app corre caemanager:<sha> y su /salud responde. Solo
#      entonces se da por buena. Si no, sale con 1 y lo dice: el entorno queda en
#      la imagen de destino sin sanar, y hay que decidir a mano.
#
# La vuelta atrás usa los docker-compose.*.yml del checkout ACTUAL (el de la
# versión de la que se vuelve), no los del commit de destino.
#
# Variables para los tests (deploy/volver-atras.tests.sh), con sus valores
# reales por defecto: RAIZ_DESPLIEGUE, VOLVER_ATRAS_INTENTOS_SALUD,
# VOLVER_ATRAS_ESPERA_SALUD, y las de imagenes-retenidas.sh.

set -euo pipefail

DIR_VOLVER_ATRAS="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=deploy/imagenes-retenidas.sh
source "$DIR_VOLVER_ATRAS/imagenes-retenidas.sh"

RAIZ_DESPLIEGUE="${RAIZ_DESPLIEGUE:-/opt/talveg}"
VOLVER_ATRAS_INTENTOS_SALUD="${VOLVER_ATRAS_INTENTOS_SALUD:-12}"
VOLVER_ATRAS_ESPERA_SALUD="${VOLVER_ATRAS_ESPERA_SALUD:-5}"
ETIQUETA_MIGRACIONES="es.talveg.migraciones-ef"
ETIQUETA_REVISION="org.opencontainers.image.revision"

detener() { echo "VUELTA ATRÁS DETENIDA: $*" >&2; exit 1; }

contenedor_app() { [ "$1" = "staging" ] && echo caemanager-staging-app || echo caemanager-app; }
contenedor_db() { [ "$1" = "staging" ] && echo caemanager-staging-db || echo caemanager-db; }

# SHA de la imagen que corre ahora el contenedor app del entorno (vacío si no
# hay contenedor o no es una caemanager:<sha>).
sha_en_marcha() {
    local imagen
    imagen="$(docker inspect "$(contenedor_app "$1")" --format '{{.Config.Image}}' 2>/dev/null || true)"
    case "$imagen" in
        "${REPOSITORIO_IMAGEN_DESPLIEGUE}":*) echo "${imagen#"${REPOSITORIO_IMAGEN_DESPLIEGUE}":}" ;;
    esac
}

# Compara el inventario de migraciones de la imagen con la base. Devuelve 0
# solo si coinciden exactamente; si no, explica la diferencia y devuelve 1.
comprobar_esquema() {
    local entorno="$1" sha="$2" en_imagen en_base sobran faltan
    en_imagen="$(docker image inspect "${REPOSITORIO_IMAGEN_DESPLIEGUE}:${sha}" \
        --format "{{index .Config.Labels \"$ETIQUETA_MIGRACIONES\"}}" 2>/dev/null || true)"
    # Una etiqueta ausente no da vacío: la plantilla de Go imprime «<no value>».
    [ "$en_imagen" = "<no value>" ] && en_imagen=""
    en_imagen="$(printf '%s' "$en_imagen" | tr ',' '\n' | sed '/^$/d' | LC_ALL=C sort -u)"
    if [ -z "$en_imagen" ]; then
        echo "La imagen ${REPOSITORIO_IMAGEN_DESPLIEGUE}:${sha} no lleva la etiqueta $ETIQUETA_MIGRACIONES (se construyó antes de P1-F1): no se puede comprobar su esquema." >&2
        return 1
    fi
    if ! en_base="$(docker exec "$(contenedor_db "$entorno")" psql -U postgres -d caemanager -v ON_ERROR_STOP=1 -tAc \
            'SELECT "MigrationId" FROM "__EFMigrationsHistory"')"; then
        echo "No se pudo leer __EFMigrationsHistory de $(contenedor_db "$entorno")." >&2
        return 1
    fi
    en_base="$(printf '%s\n' "$en_base" | tr -d '\r' | sed '/^$/d' | LC_ALL=C sort -u)"
    if [ -z "$en_base" ]; then
        echo "__EFMigrationsHistory de $(contenedor_db "$entorno") está vacía: no hay esquema con que comparar." >&2
        return 1
    fi

    sobran="$(LC_ALL=C comm -23 <(printf '%s\n' "$en_base") <(printf '%s\n' "$en_imagen"))"
    faltan="$(LC_ALL=C comm -13 <(printf '%s\n' "$en_base") <(printf '%s\n' "$en_imagen"))"
    if [ -n "$sobran" ]; then
        echo "La base de $entorno tiene migraciones que ${sha} no conoce:" >&2
        printf '  %s\n' $sobran >&2
        echo "Volver a esa imagen exigiría deshacer esquema, y este guion nunca hace un downgrade." >&2
        echo "Opciones: desplegar hacia delante un arreglo, o restaurar la base desde Borg (runbook de despliegue)." >&2
        return 1
    fi
    if [ -n "$faltan" ]; then
        echo "${sha} trae migraciones que la base de $entorno no tiene:" >&2
        printf '  %s\n' $faltan >&2
        echo "Arrancarla aplicaría esquema nuevo: eso es un despliegue, no una vuelta atrás." >&2
        return 1
    fi
    echo "Esquema compatible: la base de $entorno y ${sha} tienen las mismas $(printf '%s\n' "$en_base" | wc -l) migraciones."
}

# El contenedor app corre caemanager:<sha> y /salud responde. Reintenta
# VOLVER_ATRAS_INTENTOS_SALUD veces, VOLVER_ATRAS_ESPERA_SALUD s entre intentos.
comprobar_salud() {
    local entorno="$1" sha="$2" contenedor imagen intento
    contenedor="$(contenedor_app "$entorno")"
    imagen="$(docker inspect "$contenedor" --format '{{.Config.Image}}' 2>/dev/null || true)"
    if [ "$imagen" != "${REPOSITORIO_IMAGEN_DESPLIEGUE}:${sha}" ]; then
        echo "$contenedor corre '${imagen:-<ninguna>}', no ${REPOSITORIO_IMAGEN_DESPLIEGUE}:${sha}." >&2
        return 1
    fi
    for (( intento = 1; intento <= VOLVER_ATRAS_INTENTOS_SALUD; intento++ )); do
        if docker exec "$contenedor" curl -fsS --max-time 5 http://localhost:8080/salud > /dev/null; then
            echo "/salud de $contenedor responde (intento $intento)."
            return 0
        fi
        [ "$intento" -lt "$VOLVER_ATRAS_INTENTOS_SALUD" ] && sleep "$VOLVER_ATRAS_ESPERA_SALUD"
    done
    echo "/salud de $contenedor no respondió tras $VOLVER_ATRAS_INTENTOS_SALUD intentos." >&2
    return 1
}

main_volver_atras() {
    local entorno="${1:-}" destino="${2:-}" actual sha revision
    if ! es_entorno "$entorno" || [ -z "$destino" ] || [ $# -ne 2 ]; then
        echo "uso: volver-atras.sh <staging|produccion> <sha|anterior>" >&2
        exit 2
    fi

    exec 9>"$RAIZ_DESPLIEGUE/deploy/.ci-deploy.lock"
    flock -w 600 9 || detener "no se obtuvo el cerrojo de despliegue en 10 min: hay un despliegue o una vuelta atrás en marcha."

    actual="$(sha_en_marcha "$entorno")"
    echo "$entorno corre ahora: ${actual:-<desconocido>}"

    if [ "$destino" = "anterior" ]; then
        [ -n "$actual" ] || detener "no se sabe qué imagen corre $entorno: indica el SHA de destino explícitamente."
        sha="$(imagen_anterior "$entorno" "$actual")" || detener "no hay imagen anterior en el historial."
    else
        sha="$destino"
    fi
    es_sha "$sha" || detener "SHA no válido: '$sha' (se esperan 40 caracteres hexadecimales)."
    if [ "$sha" = "$actual" ]; then
        # `.Config.Image` también existe en un contenedor parado o insano (p. ej.
        # tras una vuelta atrás anterior que no llegó a sano): coincidir con el
        # destino no basta, tiene que responder /salud (hallazgo de Codex).
        comprobar_salud "$entorno" "$sha" \
            || detener "$entorno está configurado con ${sha} pero no está sano: no hay imagen distinta a la que volver con esta orden. Revisa los logs y decide a mano."
        echo "$entorno ya corre ${REPOSITORIO_IMAGEN_DESPLIEGUE}:${sha} y /salud responde: nada que hacer."
        exit 0
    fi
    echo "Destino: ${REPOSITORIO_IMAGEN_DESPLIEGUE}:${sha}"

    revision="$(docker image inspect "${REPOSITORIO_IMAGEN_DESPLIEGUE}:${sha}" \
        --format "{{index .Config.Labels \"$ETIQUETA_REVISION\"}}" 2>/dev/null)" \
        || detener "${REPOSITORIO_IMAGEN_DESPLIEGUE}:${sha} no está en el VPS (¿retirada por la retención?). Este guion no reconstruye."
    [ "$revision" = "$sha" ] || detener "${REPOSITORIO_IMAGEN_DESPLIEGUE}:${sha} declara la revisión '$revision'."

    comprobar_esquema "$entorno" "$sha" || detener "el esquema no admite volver a ${sha}."

    cd "$RAIZ_DESPLIEGUE/deploy/local"
    local args=(-f "docker-compose.$entorno.yml")
    [ "$entorno" = "staging" ] && args+=(--env-file .env.staging)
    export IMAGEN_TAG="$sha"
    if ! docker compose "${args[@]}" up -d --wait --wait-timeout 180 --no-build; then
        docker compose "${args[@]}" ps >&2 || true
        detener "$entorno no llegó a sano con ${sha}: queda en esa imagen sin sanar. Revisa los logs y decide a mano."
    fi

    comprobar_salud "$entorno" "$sha" || detener "$entorno arrancó ${sha} pero no está sano: no se da por buena."

    echo "VUELTA ATRÁS COMPLETADA: $entorno corre ${REPOSITORIO_IMAGEN_DESPLIEGUE}:${sha} y /salud responde."
}

if [ "${BASH_SOURCE[0]}" = "$0" ]; then
    main_volver_atras "$@"
fi
