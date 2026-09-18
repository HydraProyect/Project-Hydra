#!/bin/bash
# Prueba manual, aislada, de la función actualizar_secretos_produccion y de
# la lista blanca CLAVES_PERMITIDAS_SECRETOS_PRODUCCION de ci-deploy.sh. No
# es un arnés automatizado que ejecute el propio ci-deploy.sh (ese guion solo
# corre de verdad como comando forzado de SSH contra el VPS, ver el
# comentario en su cabecera) — es una COPIA de las mismas dos piezas,
# mantenida a mano en sincronía con el original. Si tocas cualquiera de las
# dos en ci-deploy.sh, copia el cambio aquí también, o esta prueba deja de
# medir lo que de verdad corre en producción.
set -euo pipefail

CLAVES_PERMITIDAS_SECRETOS_PRODUCCION="
AdministradorInicial__Contrasena
Anthropic__ApiKey
Smtp__Contrasena
AzureAd__ClientSecret
Integraciones__Microsoft365__ClientSecret
Integraciones__WhatsApp__AppSecret
Integraciones__WhatsApp__VerifyToken
Serilog__Seq__ApiKey
"

actualizar_secretos_produccion() {
    local recibido
    recibido="$(cat)"
    if [ -z "$recibido" ]; then
        echo "Sin secretos que actualizar — .env sin tocar." >&2
        return 0
    fi

    local linea clave
    while IFS= read -r linea; do
        [ -n "$linea" ] || continue
        clave="${linea%%=*}"
        case "$CLAVES_PERMITIDAS_SECRETOS_PRODUCCION" in
            *$'\n'"$clave"$'\n'*) ;;
            *)
                echo "::error::clave no permitida en 'secretos': '$clave' — .env sin tocar." >&2
                return 1
                ;;
        esac
    done <<< "$recibido"

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

DIR="$(mktemp -d)"
trap 'rm -rf "$DIR"' EXIT
cd "$DIR"

cat > .env <<'ENVEOF'
DOMINIO=app.talveg.es
POSTGRES_PASSWORD=viejo123
AdministradorInicial__Email=admin@talveg.es
AdministradorInicial__Contrasena=
DatosPrueba__Activo=true
ENVEOF

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
printf 'AdministradorInicial__Contrasena=nuevo456\nIntegraciones__Microsoft365__ClientSecret=Host=x;Password=a=b=c\nAnthropic__ApiKey=sk-test-123\n' \
  | actualizar_secretos_produccion
cat .env

grep -qx 'AdministradorInicial__Contrasena=nuevo456' .env || { echo "FALLO: no sustituyó una clave existente" >&2; exit 1; }
grep -qx 'Integraciones__Microsoft365__ClientSecret=Host=x;Password=a=b=c' .env || { echo "FALLO: valor con '=' truncado" >&2; exit 1; }
grep -qx 'Anthropic__ApiKey=sk-test-123' .env || { echo "FALLO: no añadió clave nueva" >&2; exit 1; }
grep -qx 'DOMINIO=app.talveg.es' .env || { echo "FALLO: perdió una clave no tocada" >&2; exit 1; }
grep -qx 'POSTGRES_PASSWORD=viejo123' .env || { echo "FALLO: perdió otra clave no tocada" >&2; exit 1; }
[ "$(wc -l < .env)" -eq 7 ] || { echo "FALLO: número de líneas inesperado ($(wc -l < .env))" >&2; exit 1; }
echo "OK: upsert correcto, resto de .env intacto"

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
    agregar "Serilog__Seq__ApiKey" "seq-key-xyz"
    agregar "Anthropic__ApiKey" "otro789"
    agregar "Smtp__Contrasena" ""
    printf '%s' "$blob"
}

DIR2="$(mktemp -d)"
trap 'rm -rf "$DIR" "$DIR2"' EXIT
(
  cd "$DIR2"
  cat > .env <<'ENVEOF'
DOMINIO=app.talveg.es
Anthropic__ApiKey=viejo123
ENVEOF
  construir_blob_como_deploy_yml | actualizar_secretos_produccion
  grep -qx 'Anthropic__ApiKey=otro789' .env || { echo "FALLO: regresión del salto de línea literal — Anthropic__ApiKey no se actualizó" >&2; exit 1; }
  grep -qx 'Serilog__Seq__ApiKey=seq-key-xyz' .env || { echo "FALLO: regresión del salto de línea literal — clave nueva no se añadió limpia" >&2; exit 1; }
  ! grep -q '^ ' .env || { echo "FALLO: hay líneas con espacio inicial en .env (indentación arrastrada)" >&2; exit 1; }
)
echo "OK: construcción del blob de deploy.yml no arrastra indentación"

echo "=== Caso 4: clave fuera de la lista blanca — rechazo total (hallazgo de Codex) ==="
# Escenario del hallazgo: si la clave SSH se filtrara, sin esta lista blanca
# alguien podría mandar Rls__PermitirIdentidadAdministrativaInsegura=true (o
# vaciar ConnectionStrings__CaeManagerDbRuntime) y, en el siguiente redeploy
# de un commit ya legítimo, dejar producción sirviendo sin RLS. Esta prueba
# comprueba que el intento se rechaza ENTERO, .env no se toca ni siquiera
# para las claves buenas que venían en el mismo envío.
ANTES4="$(cat .env)"
if printf 'Anthropic__ApiKey=bueno\nRls__PermitirIdentidadAdministrativaInsegura=true\n' | actualizar_secretos_produccion 2>/tmp/caso4-stderr.txt; then
  echo "FALLO: aceptó una clave fuera de la lista blanca" >&2
  exit 1
fi
grep -q "clave no permitida" /tmp/caso4-stderr.txt || { echo "FALLO: no avisó por qué rechazó" >&2; cat /tmp/caso4-stderr.txt >&2; exit 1; }
DESPUES4="$(cat .env)"
if [ "$ANTES4" != "$DESPUES4" ]; then
  echo "FALLO: .env cambió pese al rechazo (incluso la clave 'buena' del mismo envío se aplicó)" >&2
  exit 1
fi
rm -f /tmp/caso4-stderr.txt
echo "OK: rechazo total, .env intacto, aviso claro"

echo "=== Caso 5: POSTGRES_PASSWORD y CaeManagerDbRuntime siguen fuera de la lista (hallazgo de Codex) ==="
for clave_prohibida in POSTGRES_PASSWORD ConnectionStrings__CaeManagerDbRuntime; do
  if printf '%s=loquesea\n' "$clave_prohibida" | actualizar_secretos_produccion 2>/dev/null; then
    echo "FALLO: $clave_prohibida ya no está excluida — revisa si la exclusión sigue siendo intencional" >&2
    exit 1
  fi
done
echo "OK: las dos claves de PostgreSQL siguen fuera de la lista blanca"

echo "TODAS LAS PRUEBAS PASARON"
