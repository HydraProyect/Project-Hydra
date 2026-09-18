#!/bin/bash
# Prueba manual, aislada, de la función actualizar_secretos_produccion de
# ci-deploy.sh. No es un arnés automatizado (ci-deploy.sh no tiene ninguno,
# ver el comentario en su cabecera) — se ejecuta a mano en un directorio
# temporal para verificar el upsert antes de confiar en él contra el VPS
# real. Borra su propio directorio de trabajo al terminar.
set -euo pipefail

DIR="$(mktemp -d)"
trap 'rm -rf "$DIR"' EXIT
cd "$DIR"

cat > .env <<'ENVEOF'
DOMINIO=app.talveg.es
POSTGRES_PASSWORD=viejo123
AdministradorInicial__Email=admin@talveg.es
ConnectionStrings__CaeManagerDbRuntime=
DatosPrueba__Activo=true
ENVEOF

actualizar_secretos_produccion() {
    local recibido
    recibido="$(cat)"
    if [ -z "$recibido" ]; then
        echo "Sin secretos que actualizar — .env sin tocar." >&2
        return 0
    fi
    local fichero_env="./.env"
    [ -f "$fichero_env" ] || : > "$fichero_env"
    local fichero_nuevo
    fichero_nuevo="$(mktemp ./.env.nuevo.XXXXXX)"
    awk -F= '
        NR==FNR {
            if ($1 != "") {
                clave=$1
                valor[clave]=substr($0, index($0,"=")+1)
                vista[clave]=0
            }
            next
        }
        {
            clave=$1
            if (($0 ~ /^[A-Za-z_][A-Za-z0-9_]*=/) && (clave in valor)) {
                print clave "=" valor[clave]
                vista[clave]=1
            } else {
                print
            }
        }
        END {
            for (k in valor) if (!vista[k]) print k "=" valor[k]
        }
    ' <(printf '%s\n' "$recibido") "$fichero_env" > "$fichero_nuevo"
    chmod 600 "$fichero_nuevo"
    mv "$fichero_nuevo" "$fichero_env"
    echo "Secretos actualizados." >&2
}

echo "=== Caso 1: stdin vacío (no-op) ==="
ANTES="$(cat .env)"
actualizar_secretos_produccion < /dev/null
DESPUES="$(cat .env)"
if [ "$ANTES" != "$DESPUES" ]; then
  echo "FALLO: .env cambió con stdin vacío" >&2
  exit 1
fi
echo "OK: .env sin cambios"

echo "=== Caso 2: upsert (clave existente + clave nueva + valor con '=' dentro) ==="
printf 'POSTGRES_PASSWORD=nuevo456\nConnectionStrings__CaeManagerDbRuntime=Host=db;Port=5432;Username=cae_app_runtime;Password=a=b=c\nAnthropic__ApiKey=sk-test-123\n' \
  | actualizar_secretos_produccion
cat .env

grep -qx 'POSTGRES_PASSWORD=nuevo456' .env || { echo "FALLO: no sustituyó POSTGRES_PASSWORD" >&2; exit 1; }
grep -qx 'ConnectionStrings__CaeManagerDbRuntime=Host=db;Port=5432;Username=cae_app_runtime;Password=a=b=c' .env || { echo "FALLO: valor con '=' truncado" >&2; exit 1; }
grep -qx 'Anthropic__ApiKey=sk-test-123' .env || { echo "FALLO: no añadió clave nueva" >&2; exit 1; }
grep -qx 'DOMINIO=app.talveg.es' .env || { echo "FALLO: perdió una clave no tocada" >&2; exit 1; }
grep -qx 'AdministradorInicial__Email=admin@talveg.es' .env || { echo "FALLO: perdió otra clave no tocada" >&2; exit 1; }
[ "$(wc -l < .env)" -eq 6 ] || { echo "FALLO: número de líneas inesperado ($(wc -l < .env))" >&2; exit 1; }

echo "=== Caso 3: construcción del blob tal y como lo hace deploy.yml (regresión) ==="
# Pin del bug real que se detectó al escribir esto: un salto de línea LITERAL
# dentro de una cadena bash escrita en un bloque YAML `run: |` arrastra la
# indentación de la línea siguiente como espacios delante de la próxima
# clave, y esos espacios le rompen a awk el patrón
# `^[A-Za-z_][A-Za-z0-9_]*=` — la clave deja de reconocerse. El fix es
# `$'\n'` explícito (ver .github/workflows/deploy.yml, paso "Actualizar
# secretos de producción"); esta prueba fija ESE formato exacto de
# construcción, no solo el resultado final.
construir_blob_como_deploy_yml() {
    local blob=""
    agregar() {
        local nombre="$1" valor="$2"
        if [ -n "$valor" ]; then
            blob="${blob}${nombre}=${valor}"$'\n'
        fi
    }
    agregar "POSTGRES_PASSWORD" "otro789"
    agregar "Serilog__Seq__ApiKey" "seq-key-xyz"
    agregar "Smtp__Contrasena" ""
    printf '%s' "$blob"
}

DIR2="$(mktemp -d)"
trap 'rm -rf "$DIR" "$DIR2"' EXIT
(
  cd "$DIR2"
  cat > .env <<'ENVEOF'
DOMINIO=app.talveg.es
POSTGRES_PASSWORD=viejo123
ENVEOF
  construir_blob_como_deploy_yml | actualizar_secretos_produccion
  grep -qx 'POSTGRES_PASSWORD=otro789' .env || { echo "FALLO: regresión del salto de línea literal — POSTGRES_PASSWORD no se actualizó" >&2; exit 1; }
  grep -qx 'Serilog__Seq__ApiKey=seq-key-xyz' .env || { echo "FALLO: regresión del salto de línea literal — clave nueva no se añadió limpia" >&2; exit 1; }
  ! grep -q '^ ' .env || { echo "FALLO: hay líneas con espacio inicial en .env (indentación arrastrada)" >&2; exit 1; }
)
echo "OK: construcción del blob de deploy.yml no arrastra indentación"

echo "TODAS LAS PRUEBAS PASARON"
