#!/usr/bin/env bash
# Segunda ubicación de las claves de Data Protection (P38): una copia CIFRADA
# con age, distinta del archivo Borg, para que perder el Storage Box, su
# passphrase o el repo Borg no signifique perder las claves.
#
# Por qué existe: dataprotection-keys/ no es «solo cookies». Cifra en reposo las
# credenciales de portales externos, los documentos subidos (por tenant) y los
# tokens de sesión (RUNBOOK-CLAVES.md). Sin ellas todo eso es basura
# permanente, aunque la BD y los PDFs se recuperen enteros. Hoy KMS no está
# configurado, así que el llavero es un XML en claro dentro del volumen del VPS
# y dentro del archivo Borg: la única protección de la copia 1 es la passphrase
# de Borg. Esta copia 2 añade una protección y una ubicación independientes.
#
# Cifrado con la clave PÚBLICA de age (`age -r`): el VPS nunca guarda un
# secreto capaz de abrir la copia. La identidad (clave privada) la custodia el
# propietario fuera del VPS y fuera del Storage Box.
#
# Uso:
#   exportar   (en el VPS, como el usuario que ejecuta el backup)
#       AGE_RECIPIENT='age1...'  ./scripts/dataprotection-copia-cifrada.sh exportar [DIR_DESTINO]
#       Deja DIR_DESTINO/dataprotection-keys-AAAA-MM-DDTHH-MM-SSZ.tar.age (0600) y
#       muestra huella SHA-256, nº de claves y las claves activas con su
#       caducidad. NUNCA imprime el contenido de una clave.
#   desde-directorio DIR [DIR_DESTINO]   (sin Docker ni tocar el VPS)
#       Igual que exportar, partiendo de una carpeta que contiene
#       dataprotection-keys/ (p. ej. la extraída de un archivo Borg).
#   verificar  (donde esté la identidad, NO en el VPS)
#       ./scripts/dataprotection-copia-cifrada.sh verificar FICHERO.age IDENTIDAD
#       Descifra, comprueba que hay al menos una clave y muestra las ids.
#
# Variables: CONTENEDOR_APP (por defecto caemanager-app).
set -euo pipefail
umask 077

CONTENEDOR_APP="${CONTENEDOR_APP:-caemanager-app}"

resumen_claves() {   # resumen_claves DIR_CON_XML
    local f n=0 id caduca
    for f in "$1"/key-*.xml; do
        [ -e "$f" ] || continue
        n=$((n + 1))
        id=$(sed -n 's/.*<key id="\([^"]*\)".*/\1/p' "$f" | head -1)
        caduca=$(sed -n 's/.*<expirationDate>\([^<]*\)<.*/\1/p' "$f" | head -1)
        echo "    clave $id — caduca ${caduca:-?}"
    done
    echo "    total: $n clave(s)"
}

cifrar_directorio() {   # cifrar_directorio DIR_QUE_CONTIENE_dataprotection-keys DIR_DESTINO
    local origen="$1" destino="$2" salida
    ls "$origen"/dataprotection-keys/key-*.xml >/dev/null 2>&1 \
        || { echo "ERROR: no hay ninguna clave key-*.xml en $origen/dataprotection-keys"; return 1; }
    mkdir -p "$destino"
    salida="$destino/dataprotection-keys-$(date -u +%Y-%m-%dT%H-%M-%SZ).tar.age"
    # Redirección del shell, no `age -o`: el fichero nace con el umask 077 de
    # este guion (0600) sea cual sea la versión de age, en vez de depender de
    # cómo cree ella el fichero de salida.
    tar -C "$origen" -c dataprotection-keys | age -r "$AGE_RECIPIENT" > "$salida" \
        || { echo "ERROR: age no pudo cifrar la copia"; rm -f "$salida"; return 1; }
    [ -s "$salida" ] || { echo "ERROR: la copia cifrada salió vacía"; rm -f "$salida"; return 1; }
    echo "Copia cifrada: $salida"
    echo "    SHA-256: $(sha256sum "$salida" | cut -d' ' -f1)"
    resumen_claves "$origen/dataprotection-keys"
    echo "Siguiente paso: llevar este fichero FUERA de Hetzner (RUNBOOK-CONTINUIDAD, P38) y comprobarlo con 'verificar'."
}

exigir_destinatario() {
    : "${AGE_RECIPIENT:?Define AGE_RECIPIENT con la clave PÚBLICA age (age1...) del propietario}"
    case "$AGE_RECIPIENT" in age1*) ;; *) echo "ERROR: AGE_RECIPIENT debe empezar por age1 (clave pública, no la identidad AGE-SECRET-KEY-)"; exit 2 ;; esac
}

comando="${1:-}"
case "$comando" in
    exportar)
        exigir_destinatario
        trabajo="$(mktemp -d)"
        trap 'rm -rf "$trabajo"' EXIT
        docker cp "$CONTENEDOR_APP":/data/dataprotection-keys "$trabajo/dataprotection-keys"
        cifrar_directorio "$trabajo" "${2:-.}" || exit 1
        ;;
    desde-directorio)
        exigir_destinatario
        origen="${2:?uso: desde-directorio DIR_QUE_CONTIENE_dataprotection-keys [DIR_DESTINO]}"
        cifrar_directorio "$origen" "${3:-.}" || exit 1
        ;;
    verificar)
        fichero="${2:?uso: verificar FICHERO.age IDENTIDAD}"
        identidad="${3:?uso: verificar FICHERO.age IDENTIDAD}"
        trabajo="$(mktemp -d)"
        trap 'rm -rf "$trabajo"' EXIT
        age -d -i "$identidad" "$fichero" | tar -x -C "$trabajo"
        ls "$trabajo"/dataprotection-keys/key-*.xml >/dev/null 2>&1 \
            || { echo "ERROR: la copia descifra pero no contiene claves key-*.xml"; exit 1; }
        echo "Copia legible:"
        resumen_claves "$trabajo/dataprotection-keys"
        ;;
    *)
        echo "Uso: $0 exportar [DIR_DESTINO] | desde-directorio DIR [DIR_DESTINO] | verificar FICHERO.age IDENTIDAD" >&2
        exit 2
        ;;
esac
