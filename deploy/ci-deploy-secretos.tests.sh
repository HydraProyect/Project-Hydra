#!/bin/bash
# Prueba de CLAVES_PERMITIDAS_SECRETOS_PRODUCCION y
# actualizar_secretos_produccion, ambas definidas y ejecutadas TAL CUAL en
# ci-deploy.sh — sin copiar su lógica (hallazgo de Codex sobre la primera
# versión de esta prueba: una copia mantenida a mano podía desincronizarse
# en silencio y quedar en verde mientras el VPS corría otra cosa).
#
# `source` funciona sin disparar un despliegue real gracias a la guarda
# `BASH_SOURCE[0] = $0` al final de ci-deploy.sh, y
# FICHERO_ENV_SECRETOS_PRODUCCION (ver ese fichero) redirige la función a un
# directorio temporal en vez de /opt/talveg/deploy/local/.env — las dos
# existen solo para que este test pueda ejercitar la función real de forma
# aislada, y ninguna de las dos cambia el comportamiento en el VPS (ahí
# nunca se define esa variable).
#
# No sustituye probar contra un VPS real: resolve-deploy-sha.sh y
# liberar-disco.sh siguen sin arnés por el mismo motivo — el propio comando
# forzado de SSH no se puede ejercitar entero fuera de una sesión real.
set -euo pipefail

DIR_GUION="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$DIR_GUION/ci-deploy.sh"

DIR="$(mktemp -d)"
trap 'rm -rf "$DIR"' EXIT
export FICHERO_ENV_SECRETOS_PRODUCCION="$DIR/.env"

cat > "$FICHERO_ENV_SECRETOS_PRODUCCION" <<'ENVEOF'
DOMINIO=app.talveg.es
POSTGRES_PASSWORD=viejo123
AdministradorInicial__Email=admin@talveg.es
AdministradorInicial__Contrasena=
DatosPrueba__Activo=true
ENVEOF

leer_env() { cat "$FICHERO_ENV_SECRETOS_PRODUCCION"; }

echo "=== Caso 1: stdin vacío (no-op) ==="
ANTES="$(leer_env)"
actualizar_secretos_produccion < /dev/null
DESPUES="$(leer_env)"
if [ "$ANTES" != "$DESPUES" ]; then
  echo "FALLO: .env cambió con stdin vacío" >&2
  exit 1
fi
echo "OK: .env sin cambios"

echo "=== Caso 2: upsert (clave existente + clave nueva + valor con '=' dentro) ==="
printf 'AdministradorInicial__Contrasena=nuevo456\nIntegraciones__Microsoft365__ClientSecret=Host=x;Password=a=b=c\nAnthropic__ApiKey=sk-test-123\n' \
  | actualizar_secretos_produccion
leer_env

grep -qx "AdministradorInicial__Contrasena='nuevo456'" "$FICHERO_ENV_SECRETOS_PRODUCCION" || { echo "FALLO: no sustituyó una clave existente" >&2; exit 1; }
grep -qx "Integraciones__Microsoft365__ClientSecret='Host=x;Password=a=b=c'" "$FICHERO_ENV_SECRETOS_PRODUCCION" || { echo "FALLO: valor con '=' truncado" >&2; exit 1; }
grep -qx "Anthropic__ApiKey='sk-test-123'" "$FICHERO_ENV_SECRETOS_PRODUCCION" || { echo "FALLO: no añadió clave nueva" >&2; exit 1; }
grep -qx 'DOMINIO=app.talveg.es' "$FICHERO_ENV_SECRETOS_PRODUCCION" || { echo "FALLO: perdió una clave no tocada" >&2; exit 1; }
grep -qx 'POSTGRES_PASSWORD=viejo123' "$FICHERO_ENV_SECRETOS_PRODUCCION" || { echo "FALLO: perdió otra clave no tocada" >&2; exit 1; }
[ "$(wc -l < "$FICHERO_ENV_SECRETOS_PRODUCCION")" -eq 7 ] || { echo "FALLO: número de líneas inesperado ($(wc -l < "$FICHERO_ENV_SECRETOS_PRODUCCION"))" >&2; exit 1; }
echo "OK: upsert correcto, entrecomillado, resto de .env intacto"

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

export FICHERO_ENV_SECRETOS_PRODUCCION="$DIR/.env-caso3"
cat > "$FICHERO_ENV_SECRETOS_PRODUCCION" <<'ENVEOF'
DOMINIO=app.talveg.es
Anthropic__ApiKey=viejo123
ENVEOF
construir_blob_como_deploy_yml | actualizar_secretos_produccion
grep -qx "Anthropic__ApiKey='otro789'" "$FICHERO_ENV_SECRETOS_PRODUCCION" || { echo "FALLO: regresión del salto de línea literal — Anthropic__ApiKey no se actualizó" >&2; exit 1; }
grep -qx "Serilog__Seq__ApiKey='seq-key-xyz'" "$FICHERO_ENV_SECRETOS_PRODUCCION" || { echo "FALLO: regresión del salto de línea literal — clave nueva no se añadió limpia" >&2; exit 1; }
! grep -q '^ ' "$FICHERO_ENV_SECRETOS_PRODUCCION" || { echo "FALLO: hay líneas con espacio inicial en .env (indentación arrastrada)" >&2; exit 1; }
echo "OK: construcción del blob de deploy.yml no arrastra indentación"

echo "=== Caso 4: clave fuera de la lista blanca — rechazo total (hallazgo de Codex) ==="
# Escenario del hallazgo: si la clave SSH se filtrara, sin esta lista blanca
# alguien podría mandar Rls__PermitirIdentidadAdministrativaInsegura=true (o
# vaciar ConnectionStrings__CaeManagerDbRuntime) y, en el siguiente redeploy
# de un commit ya legítimo, dejar a producción sirviendo tráfico sin RLS.
# Esta prueba comprueba que el intento se rechaza ENTERO, .env no se toca ni
# siquiera para las claves buenas que venían en el mismo envío.
export FICHERO_ENV_SECRETOS_PRODUCCION="$DIR/.env-caso4"
cat > "$FICHERO_ENV_SECRETOS_PRODUCCION" <<'ENVEOF'
DOMINIO=app.talveg.es
ENVEOF
ANTES4="$(leer_env)"
if printf 'Anthropic__ApiKey=bueno\nRls__PermitirIdentidadAdministrativaInsegura=true\n' | actualizar_secretos_produccion 2>/tmp/caso4-stderr.txt; then
  echo "FALLO: aceptó una clave fuera de la lista blanca" >&2
  exit 1
fi
grep -q "clave no permitida" /tmp/caso4-stderr.txt || { echo "FALLO: no avisó por qué rechazó" >&2; cat /tmp/caso4-stderr.txt >&2; exit 1; }
DESPUES4="$(leer_env)"
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

echo "=== Caso 6: un valor con '\$' no se interpola (hallazgo de Codex) ==="
# docs.docker.com/reference/compose-file/services/#env_file-format: un valor
# SIN comillas (o con comillas dobles) de un env_file sufre la misma
# interpolación \${VAR}/\$VAR que el resto del fichero Compose; con comillas
# simples "se usan literales". Esta prueba no invoca Compose (no hay Docker
# en este arnés) — fija que la línea escrita queda entrecomillada de la
# forma que el propio formato dotenv documenta como literal.
export FICHERO_ENV_SECRETOS_PRODUCCION="$DIR/.env-caso6"
: > "$FICHERO_ENV_SECRETOS_PRODUCCION"
printf 'Smtp__Contrasena=abc$HOME/raro'"'"'con-comilla\n' | actualizar_secretos_produccion
grep -qxF "Smtp__Contrasena='abc\$HOME/raro\'con-comilla'" "$FICHERO_ENV_SECRETOS_PRODUCCION" \
  || { echo "FALLO: el valor con '\$' y comilla no quedó entrecomillado/escapado como espera el formato dotenv" >&2; cat "$FICHERO_ENV_SECRETOS_PRODUCCION" >&2; exit 1; }
echo "OK: valor con '\$' y comilla queda entrecomillado y escapado"

echo "=== Caso 7: una barra invertida suelta no se duplica (hallazgo de Codex) ==="
# Regresión sobre la primera versión de esc(), que duplicaba TODA barra
# invertida "por si acaso". docs.docker.com/reference/compose-file/services/#env_file-format
# prueba con su propio ejemplo (VAR='some\tvalue' -> some\tvalue) que un
# valor con comillas simples no procesa ninguna secuencia de escape salvo la
# de la comilla — duplicarla escribía dos barras donde el secreto real solo
# tenía una, y la credencial dejaba de coincidir con la cargada en GitHub.
export FICHERO_ENV_SECRETOS_PRODUCCION="$DIR/.env-caso7"
: > "$FICHERO_ENV_SECRETOS_PRODUCCION"
printf 'Smtp__Contrasena=cla\\ve\n' | actualizar_secretos_produccion
grep -qxF "Smtp__Contrasena='cla\ve'" "$FICHERO_ENV_SECRETOS_PRODUCCION" \
  || { echo "FALLO: la barra invertida no quedó tal cual (se duplicó o se perdió)" >&2; cat "$FICHERO_ENV_SECRETOS_PRODUCCION" >&2; exit 1; }
echo "OK: la barra invertida queda literal, sin duplicar"

echo "TODAS LAS PRUEBAS PASARON"
