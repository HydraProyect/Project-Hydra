#!/bin/bash
# Prueba de la recepción y verificación de la imagen firmada en CI
# (recibir_imagen_firmada, verificar_firma_imagen, cargar_imagen_verificada),
# ejecutadas TAL CUAL desde ci-deploy.sh con `source` — mismo criterio que
# ci-deploy-secretos.tests.sh. `cosign` y `docker` son falsos: aquí se prueba
# qué exige el guion y en qué orden, no la criptografía de Sigstore (esa la
# ejercita de verdad el paso "Verificar la firma con la misma función que el
# VPS" del job `imagen` de deploy.yml, con la firma real del run).
set -euo pipefail

DIR_GUION="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$DIR_GUION/ci-deploy.sh"
FICHERO_FUENTE="$DIR_GUION/ci-deploy.sh"

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

SHA_OK="0123456789abcdef0123456789abcdef01234567"
SHA_OTRO="89abcdef0123456789abcdef0123456789abcdef"

fallo() { echo "FALLO: $*" >&2; exit 1; }

# Tarball de prueba con saltos de línea y bytes nulos dentro: si la lectura de
# las dos líneas de cabecera consumiera de más, el tarball llegaría recortado.
printf 'linea1\nlinea2\n\000\001\377fin' > "$TMP/tarball"
printf '{"bundle":"de prueba"}' > "$TMP/bundle"
printf 'revision=%s\nsha256=%s\n' "$SHA_OK" "$(sha256sum "$TMP/tarball" | cut -d' ' -f1)" > "$TMP/manifiesto"

flujo() {
    base64 -w0 "$TMP/bundle"; echo
    base64 -w0 "$TMP/manifiesto"; echo
    cat "$TMP/tarball"
}

nuevo_dir() { local d; d="$(mktemp -d "$TMP/recibido.XXXXXX")"; echo "$d"; }

echo "=== Caso 1: un flujo válido por tubería llega entero y separado ==="
D1="$(nuevo_dir)"
flujo | recibir_imagen_firmada "$D1" > /dev/null
cmp -s "$D1/imagen.sigstore.json" "$TMP/bundle" || fallo "el bundle no llegó idéntico"
cmp -s "$D1/imagen.manifiesto" "$TMP/manifiesto" || fallo "el manifiesto no llegó idéntico"
cmp -s "$D1/imagen.tar.gz" "$TMP/tarball" || fallo "el tarball no llegó idéntico (¿se comió bytes la lectura de cabecera?)"
echo "OK: bundle, manifiesto y tarball llegan idénticos"

echo "=== Caso 2: rechazos de forma en la recepción ==="
espera_rechazo_recepcion() {
    local descripcion="$1" patron="$2" salida d
    d="$(nuevo_dir)"
    if salida="$(cat | recibir_imagen_firmada "$d" 2>&1)"; then
        fallo "$descripcion: se aceptó"
    fi
    printf '%s' "$salida" | grep -q "$patron" || fallo "$descripcion: mensaje inesperado: $salida"
    echo "OK: $descripcion"
}
printf '' | espera_rechazo_recepcion "stdin vacío (cliente anterior a este cambio)" "no llegó el bundle"
printf 'no es base64!\n' | espera_rechazo_recepcion "bundle que no es base64" "no es base64"
{ head -c "$MAX_CARACTERES_LINEA_BASE64" /dev/zero | tr '\0' 'A'; echo; } | espera_rechazo_recepcion "bundle demasiado largo" "supera"
{ base64 -w0 "$TMP/bundle"; echo; } | espera_rechazo_recepcion "sin manifiesto" "no llegó el manifiesto"
{ base64 -w0 "$TMP/bundle"; echo; base64 -w0 "$TMP/manifiesto"; echo; } | espera_rechazo_recepcion "sin tarball" "no llegó la imagen"
( MAX_BYTES_IMAGEN=4; flujo | espera_rechazo_recepcion "tarball por encima del límite" "supera" )

echo "=== Caso 3: verificación con un cosign falso que registra sus argumentos ==="
COSIGN_FALSO="$TMP/cosign"
cat > "$COSIGN_FALSO" <<'EOF'
#!/bin/bash
printf '%s\n' "$@" > "$COSIGN_ARGS"
exit "${COSIGN_SALIDA:-0}"
EOF
chmod +x "$COSIGN_FALSO"
export COSIGN_ARGS="$TMP/cosign.args"

COSIGN="$COSIGN_FALSO" verificar_firma_imagen "$D1" "$SHA_OK" > /dev/null || fallo "rechazó un envío válido"
ARGS="$(tr '\n' ' ' < "$COSIGN_ARGS")"
for esperado in \
    "verify-blob" \
    "--bundle $D1/imagen.sigstore.json" \
    "--certificate-identity https://github.com/HydraProyect/Project-Hydra/.github/workflows/deploy.yml@refs/heads/main" \
    "--certificate-oidc-issuer https://token.actions.githubusercontent.com" \
    "--certificate-github-workflow-repository HydraProyect/Project-Hydra" \
    "--certificate-github-workflow-sha $SHA_OK" \
    "$D1/imagen.manifiesto"; do
    case " $ARGS " in
        *" $esperado "*) ;;
        *) fallo "cosign no recibió '$esperado' (recibió: $ARGS)" ;;
    esac
done
case "$ARGS" in
    *"--certificate-identity-regexp"*|*"--insecure"*) fallo "cosign recibió una opción que relaja la identidad: $ARGS" ;;
esac
echo "OK: identidad, emisor, repositorio y SHA exigidos a cosign, sobre el manifiesto"

echo "=== Caso 4: rechazos de la verificación (todos fallan cerrado) ==="
espera_rechazo_verificacion() {
    local descripcion="$1" dir="$2" sha="$3" patron="$4" salida
    if salida="$(verificar_firma_imagen "$dir" "$sha" 2>&1)"; then
        fallo "$descripcion: se aceptó"
    fi
    printf '%s' "$salida" | grep -q "$patron" || fallo "$descripcion: mensaje inesperado: $salida"
    echo "OK: $descripcion"
}
COSIGN="$TMP/no-existe" espera_rechazo_verificacion "sin cosign instalado" "$D1" "$SHA_OK" "cosign no está instalado"
COSIGN="$COSIGN_FALSO" COSIGN_SALIDA=1 espera_rechazo_verificacion "firma inválida" "$D1" "$SHA_OK" "firma de la imagen NO es válida"
COSIGN="$COSIGN_FALSO" espera_rechazo_verificacion "SHA con forma inválida" "$D1" "main" "SHA no válido"

D2="$(nuevo_dir)"; cp "$D1"/* "$D2"/
printf 'revision=%s\nsha256=%s\n' "$SHA_OTRO" "$(sha256sum "$TMP/tarball" | cut -d' ' -f1)" > "$D2/imagen.manifiesto"
COSIGN="$COSIGN_FALSO" espera_rechazo_verificacion "manifiesto firmado de otro commit" "$D2" "$SHA_OK" "es de $SHA_OTRO"

D3="$(nuevo_dir)"; cp "$D1"/* "$D3"/
printf 'otro contenido' > "$D3/imagen.tar.gz"
COSIGN="$COSIGN_FALSO" espera_rechazo_verificacion "tarball distinto del firmado" "$D3" "$SHA_OK" "no coincide con el hash firmado"

D4="$(nuevo_dir)"; cp "$D1"/* "$D4"/
printf 'revision=%s\nsha256=%s\nextra=1\n' "$SHA_OK" "$(sha256sum "$TMP/tarball" | cut -d' ' -f1)" > "$D4/imagen.manifiesto"
COSIGN="$COSIGN_FALSO" espera_rechazo_verificacion "manifiesto con una línea de más" "$D4" "$SHA_OK" "no tiene la forma esperada"

echo "=== Caso 5: cargar_imagen_verificada comprueba etiqueta y revisión ==="
docker() {
    case "$1" in
        load) return "${DOCKER_LOAD_SALIDA:-0}" ;;
        image)
            [ "${DOCKER_SIN_IMAGEN:-0}" = "1" ] && return 1
            [ "$3" = "${IMAGEN_REPOSITORIO}:${SHA_OK}" ] || return 1
            echo "${REVISION_ETIQUETA:-$SHA_OK}" ;;
        *) return 0 ;;
    esac
}
cargar_imagen_verificada "$D1" "$SHA_OK" || fallo "rechazó una imagen con la revisión correcta"
if REVISION_ETIQUETA="$SHA_OTRO" cargar_imagen_verificada "$D1" "$SHA_OK" 2>/dev/null; then fallo "aceptó una revisión distinta"; fi
if DOCKER_SIN_IMAGEN=1 cargar_imagen_verificada "$D1" "$SHA_OK" 2>/dev/null; then fallo "aceptó un tarball sin la etiqueta esperada"; fi
if DOCKER_LOAD_SALIDA=1 cargar_imagen_verificada "$D1" "$SHA_OK" 2>/dev/null; then fallo "siguió tras un docker load fallido"; fi
unset -f docker
echo "OK: etiqueta caemanager:<sha> y revisión OCI exigidas"

echo "=== Caso 6: orden en main() — recibir, resolver, verificar, liberar disco, cargar, up sin build ==="
linea() { grep -n -- "$1" "$FICHERO_FUENTE" | head -1 | cut -d: -f1; }
L_RECIBIR="$(linea '^recibir_imagen_firmada "\$DIR_IMAGEN" || exit 1$')"
L_RESOLVER="$(linea '^bash /opt/talveg/deploy/resolve-deploy-sha.sh')"
L_VERIFICAR="$(linea '^verificar_firma_imagen "\$DIR_IMAGEN" "\$SHA" || exit 1$')"
L_LIBERAR="$(linea '^bash /opt/talveg/deploy/liberar-disco.sh$')"
L_CARGAR="$(linea 'if ! cargar_imagen_verificada "\$DIR_IMAGEN" "\$SHA"')"
L_UP="$(linea 'docker compose "\${args\[@\]}" up -d --wait --wait-timeout 180 --no-build')"
for v in L_RECIBIR L_RESOLVER L_VERIFICAR L_LIBERAR L_CARGAR L_UP; do
    [ -n "${!v}" ] || fallo "no se localizó $v en ci-deploy.sh"
done
[ "$L_RECIBIR" -lt "$L_RESOLVER" ] || fallo "stdin se lee después de resolve-deploy-sha.sh ($L_RECIBIR >= $L_RESOLVER)"
[ "$L_RESOLVER" -lt "$L_VERIFICAR" ] || fallo "se verifica antes de validar el SHA ($L_VERIFICAR <= $L_RESOLVER)"
[ "$L_VERIFICAR" -lt "$L_LIBERAR" ] || fallo "se libera disco antes de verificar la firma"
[ "$L_LIBERAR" -lt "$L_CARGAR" ] || fallo "liberar-disco.sh (docker image prune -af) corre después de cargar: borraría la imagen"
[ "$L_CARGAR" -lt "$L_UP" ] || fallo "el up va antes de cargar la imagen"
if grep -nE 'docker compose .*[[:space:]]build([[:space:]]|$)' "$FICHERO_FUENTE" | grep -v '^[0-9]*:[[:space:]]*#'; then
    fallo "ci-deploy.sh vuelve a compilar en el VPS"
fi
echo "OK: recibir ($L_RECIBIR) < resolver ($L_RESOLVER) < verificar ($L_VERIFICAR) < liberar ($L_LIBERAR) < cargar ($L_CARGAR) < up --no-build ($L_UP); sin build en el VPS"

echo "=== Caso 7: los dos compose arrancan caemanager:\${IMAGEN_TAG} en app y migrador ==="
for f in docker-compose.produccion.yml docker-compose.staging.yml; do
    n="$(grep -c '^    image: caemanager:\${IMAGEN_TAG:-local}$' "$DIR_GUION/local/$f")"
    [ "$n" -eq 2 ] || fallo "$f tiene $n servicios con image: caemanager:\${IMAGEN_TAG:-local} (se esperan 2: app y migrador)"
done
echo "OK: app y migrador de ambos stacks usan la imagen cargada"

echo "TODAS LAS PRUEBAS PASARON"
