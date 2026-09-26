#!/bin/bash
# Historial de despliegues por entorno y retención de las N últimas imágenes
# caemanager:<sha> en el VPS (P1-F1).
#
# Uso:
#   imagenes-retenidas.sh registrar <staging|produccion> <sha>
#   imagenes-retenidas.sh retener
#   imagenes-retenidas.sh anterior <staging|produccion> <sha actual>
#
# `registrar` y `retener` los llama ci-deploy.sh tras un despliegue que llegó a
# sano, y `retener` también ANTES de liberar-disco.sh y de recibir la imagen
# nueva: si no, las imágenes de despliegues que fallan tras cargarse (que nunca
# llegan al `retener` final) se acumularían hasta que el disco crítico
# impidiera desplegar (hallazgo de Codex). `anterior` lo usa deploy/volver-atras.sh (que además hace `source` de
# este fichero). Staging y producción comparten el daemon Docker del VPS, así
# que la retención trabaja sobre los dos historiales a la vez: se conservan
# las N últimas imágenes DISTINTAS de cada entorno (la unión de ambas) y
# cualquier imagen que use un contenedor; el resto de etiquetas caemanager:<sha>
# se retira con `docker image rm` (sin -f: una imagen en uso nunca se fuerza).
#
# N sale, por este orden, de la variable IMAGENES_RETENIDAS, de la línea
# `IMAGENES_RETENIDAS=<n>` de $CONFIG_DESPLIEGUE (/etc/talveg/despliegue.conf;
# se lee esa línea, el fichero nunca se ejecuta) o del valor por defecto, 5.
# Admite de 2 a 50: con 1 no quedaría ninguna imagen a la que volver. Un valor
# fuera de rango no borra nada: se avisa y se sale con error.
#
# El historial vive fuera del checkout de /opt/talveg (el `git checkout
# --detach` de cada despliegue no debe poder tocarlo): un fichero por entorno
# en $DIR_HISTORIAL_DESPLIEGUES, una línea `<fecha ISO> <sha>` por despliegue.
#
# liberar-disco.sh ya no poda estas imágenes (las marca la etiqueta
# es.talveg.despliegue que les pone deploy.yml): esta retención es lo único que
# las retira, y por eso corre en cada despliegue sano.

set -euo pipefail

DIR_HISTORIAL_DESPLIEGUES="${DIR_HISTORIAL_DESPLIEGUES:-/var/lib/talveg/despliegues}"
CONFIG_DESPLIEGUE="${CONFIG_DESPLIEGUE:-/etc/talveg/despliegue.conf}"
IMAGENES_RETENIDAS_POR_DEFECTO=5
REPOSITORIO_IMAGEN_DESPLIEGUE="caemanager"

es_entorno() { [ "${1:-}" = "staging" ] || [ "${1:-}" = "produccion" ]; }
es_sha() { [[ "${1:-}" =~ ^[0-9a-f]{40}$ ]]; }

fichero_historial() { echo "$DIR_HISTORIAL_DESPLIEGUES/$1"; }

# Imprime N o falla si el valor configurado no es un entero entre 2 y 50.
imagenes_retenidas_n() {
    local n="${IMAGENES_RETENIDAS:-}"
    if [ -z "$n" ] && [ -r "$CONFIG_DESPLIEGUE" ]; then
        n="$(sed -n 's/^IMAGENES_RETENIDAS=\([^[:space:]]*\)[[:space:]]*$/\1/p' "$CONFIG_DESPLIEGUE" | tail -1)"
    fi
    n="${n:-$IMAGENES_RETENIDAS_POR_DEFECTO}"
    if ! [[ "$n" =~ ^[0-9]+$ ]] || [ "$n" -lt 2 ] || [ "$n" -gt 50 ]; then
        echo "::error::IMAGENES_RETENIDAS='$n' no es un entero entre 2 y 50 — no se retira ninguna imagen." >&2
        return 1
    fi
    echo "$n"
}

registrar_despliegue() {
    local entorno="$1" sha="$2"
    es_entorno "$entorno" || { echo "::error::entorno no válido: '$entorno'" >&2; return 1; }
    es_sha "$sha" || { echo "::error::SHA no válido: '$sha'" >&2; return 1; }
    mkdir -p "$DIR_HISTORIAL_DESPLIEGUES"
    printf '%s %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$sha" >> "$(fichero_historial "$entorno")"
    echo "Historial de $entorno: registrado $sha."
}

# Los <límite> SHAs más recientes del historial de un entorno (todos si no se
# da), del más reciente al más antiguo, sin repetir (cuenta la aparición más
# reciente de cada uno). El límite lo aplica awk leyendo la entrada ENTERA: con
# `| head -n N` detrás, un historial largo cerraba la tubería antes de tiempo y
# SIGPIPE + pipefail abortaban `retener` para siempre (hallazgo de Codex).
shas_recientes() {
    local fichero limite="${2:-0}"
    fichero="$(fichero_historial "$1")"
    [ -r "$fichero" ] || return 0
    awk '$2 ~ /^[0-9a-f]{40}$/ { print $2 }' "$fichero" | tac \
        | awk -v limite="$limite" '!visto[$0]++ && (limite == 0 || n < limite) { print; n++ }'
}

# La imagen desplegada justo antes de la ÚLTIMA aparición de <sha actual> en
# el historial, distinta de ella. Tras volver de B a A, «anterior» vuelve a
# retroceder desde A, no ofrece otra vez B. Falla si el SHA actual no está en
# el historial o si no hay nada antes.
imagen_anterior() {
    local entorno="$1" actual="$2" fichero
    fichero="$(fichero_historial "$entorno")"
    if [ ! -r "$fichero" ]; then
        echo "::error::no hay historial de despliegues de $entorno en $fichero." >&2
        return 1
    fi
    awk -v actual="$actual" '
        $2 ~ /^[0-9a-f]{40}$/ { n++; sha[n] = $2; if ($2 == actual) ultima = n }
        END {
            if (!ultima) { exit 2 }
            for (i = ultima - 1; i >= 1; i--) if (sha[i] != actual) { print sha[i]; exit 0 }
            exit 3
        }' "$fichero" || {
        case $? in
            2) echo "::error::la imagen en marcha ($actual) no está en el historial de $entorno: indica el SHA de destino explícitamente." >&2 ;;
            *) echo "::error::no hay ningún despliegue de $entorno anterior a $actual en el historial." >&2 ;;
        esac
        return 1
    }
}

retener_imagenes() {
    local n entorno sha etiqueta
    n="$(imagenes_retenidas_n)" || return 1

    local conservar=""
    for entorno in staging produccion; do
        conservar+="$(shas_recientes "$entorno" "$n")"$'\n'
    done
    # Las que usa cualquier contenedor, aunque esté parado.
    conservar+="$(docker ps -a --format '{{.Image}}' | sed -n "s/^${REPOSITORIO_IMAGEN_DESPLIEGUE}://p")"$'\n'

    echo "Retención de imágenes: se conservan las $n últimas de cada entorno y las que usa un contenedor."
    while read -r etiqueta; do
        es_sha "$etiqueta" || continue
        if printf '%s' "$conservar" | grep -qx -- "$etiqueta"; then
            continue
        fi
        echo "  retirando ${REPOSITORIO_IMAGEN_DESPLIEGUE}:${etiqueta}"
        docker image rm "${REPOSITORIO_IMAGEN_DESPLIEGUE}:${etiqueta}" > /dev/null \
            || echo "::warning::no se pudo retirar ${REPOSITORIO_IMAGEN_DESPLIEGUE}:${etiqueta}." >&2
    done < <(docker image ls "$REPOSITORIO_IMAGEN_DESPLIEGUE" --format '{{.Tag}}')

    # Redesplegar un SHA con otra imagen (otro ID) deja la anterior sin
    # etiqueta: el bucle de arriba ya no la ve y liberar-disco.sh la excluye por
    # su es.talveg.despliegue (hallazgo de Codex). Sin -a, `image prune` solo
    # quita las colgantes, y nunca una que use un contenedor.
    docker image prune -f --filter "label=es.talveg.despliegue" > /dev/null \
        || echo "::warning::no se pudieron retirar las imágenes de despliegue sin etiqueta." >&2
}

main_imagenes_retenidas() {
    case "${1:-}" in
        registrar) registrar_despliegue "${2:-}" "${3:-}" ;;
        retener) retener_imagenes ;;
        anterior) imagen_anterior "${2:-}" "${3:-}" ;;
        *)
            echo "uso: imagenes-retenidas.sh registrar <staging|produccion> <sha> | retener | anterior <staging|produccion> <sha>" >&2
            return 2
            ;;
    esac
}

if [ "${BASH_SOURCE[0]}" = "$0" ]; then
    main_imagenes_retenidas "$@"
fi
