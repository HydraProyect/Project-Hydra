#!/bin/bash
# Prueba del latido del backup (P36) sin Docker, sin Borg y sin red: `docker`,
# `borg` y `curl` falsos en el PATH registran cada llamada y fallan según el
# escenario.
#
# La propiedad que importa no es «el guion corre», es la que hace útil al dead
# man's switch: el ping de ÉXITO solo llega si el backup terminó bien; un fallo
# de cualquier etapa avisa a `<url>/fail` y NUNCA envía el ping de éxito; y un
# fallo al avisar no convierte un backup bueno en uno malo.
#
# Ejecutable a mano: bash scripts/backup-borg.tests.sh
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPT="$SCRIPT_DIR/backup-borg.sh"
URL="https://uptime.example.invalid/api/v1/heartbeat/TOKEN-DE-PRUEBA"

TMP_ROOT="$(mktemp -d)"
trap 'rm -rf "$TMP_ROOT"' EXIT
MOCK_BIN="$TMP_ROOT/bin"
mkdir -p "$MOCK_BIN"

cat > "$MOCK_BIN/docker" <<'EOF'
#!/bin/bash
# docker exec caemanager-db pg_dump ...  /  docker cp caemanager-app:RUTA DEST
echo "docker $*" >> "${LOG_DOCKER:-/dev/null}"
if [ "$1" = "exec" ]; then
  [ "${ESC_DUMP_VACIO:-0}" = "1" ] || echo "contenido-del-dump"
  exit 0
fi
if [ "$1" = "cp" ]; then
  origen="$2"; destino="$3"
  case "$origen" in
    *dataprotection-keys)
      mkdir -p "$destino"
      [ "${ESC_SIN_CLAVES:-0}" = "1" ] || echo "<key id='x'/>" > "$destino/key-x.xml"
      ;;
    *documentos)
      mkdir -p "$destino"; echo pdf > "$destino/a.pdf"
      ;;
  esac
  exit 0
fi
exit 0
EOF
cat > "$MOCK_BIN/borg" <<'EOF'
#!/bin/bash
echo "borg $*" >> "$LOG_BORG"
# En `create` el directorio de trabajo es lo que entra en el archivo: se copia para
# poder comprobar QUÉ entra (y con qué permisos), no solo que borg se llamó.
if [ "$1" = "create" ]; then rm -rf "$LOG_BORG.archivo"; mkdir -p "$LOG_BORG.archivo"; for a in "${@:2}"; do case "$a" in -*|::*|zstd) ;; *) [ -e "$a" ] && cp -a "$a" "$LOG_BORG.archivo/" ;; esac; done; fi
if [ "$1" = "create" ] && [ "${ESC_BORG_CREATE_FALLA:-0}" = "1" ]; then exit 2; fi
exit 0
EOF
cat > "$MOCK_BIN/curl" <<'EOF'
#!/bin/bash
echo "curl $*" >> "$LOG_CURL"
# `-K -`: la configuración (la URL) llega por stdin; se registra aparte para poder
# comprobar que NO viaja en los argumentos.
case " $* " in *" -K - "*) echo "cfg $(cat)" >> "$LOG_CURL" ;; esac
[ "${ESC_CURL_FALLA:-0}" = "1" ] && exit 22
exit 0
EOF
chmod +x "$MOCK_BIN"/*

FALLOS=0
PRUEBAS=0

# .env de producción de mentira, con un centinela reconocible: si aparece en un log o en
# argv, se filtró. El centinela se arma en ejecución para que este fichero no contenga
# nada que parezca una credencial (gitleaks).
SENTINELA="CENTINELA-ENV-$(printf '%s%s' abc123 xyz789)"
ENV_FALSO="$TMP_ROOT/env-falso"
printf 'POSTGRES_PASSWORD=%s\nSENTRY_DSN=https://%s@sentry.example.invalid/1\n' "$SENTINELA" "$SENTINELA" > "$ENV_FALSO"

# ejecutar NOMBRE [VAR=valor ...] -> deja $SALIDA, $CODIGO, $LOG_CURL, $LOG_BORG
ejecutar() {
  local nombre="$1"; shift
  export LOG_CURL="$TMP_ROOT/$nombre.curl" LOG_BORG="$TMP_ROOT/$nombre.borg" LOG_DOCKER="$TMP_ROOT/$nombre.docker"
  : > "$LOG_CURL"; : > "$LOG_BORG"; : > "$LOG_DOCKER"
  SALIDA=$(env ENV_PRODUCCION="$ENV_FALSO" "$@" PATH="$MOCK_BIN:$PATH" BORG_REPO=repo-falso BORG_PASSPHRASE=x \
      bash "$SCRIPT" 2>&1)
  CODIGO=$?
}

comprobar() {   # comprobar "descripción" condición-en-bash
  PRUEBAS=$((PRUEBAS + 1))
  if eval "$2"; then
    echo "  ok   $1"
  else
    echo "  FALLO $1"
    FALLOS=$((FALLOS + 1))
  fi
}

llamadas_curl() { grep -c '^curl' "$LOG_CURL"; }

echo "== éxito con heartbeat: un solo ping, a la URL sin /fail"
ejecutar exito BETTERSTACK_HEARTBEAT_URL="$URL"
comprobar "termina con 0" '[ "$CODIGO" -eq 0 ]'
comprobar "un único curl" '[ "$(llamadas_curl)" -eq 1 ]'
comprobar "el curl va a la URL exacta (por stdin)" 'grep -q "^cfg url = \"$URL\"\$" "$LOG_CURL"'
comprobar "la URL NO viaja en los argumentos de curl (saldría en ps)" '! grep "^curl" "$LOG_CURL" | grep -q "$URL"'
comprobar "no se avisó de fallo" '! grep -q "/fail" "$LOG_CURL"'

echo "== sin URL de heartbeat: cero llamadas de red"
ejecutar sinurl
comprobar "termina con 0" '[ "$CODIGO" -eq 0 ]'
comprobar "curl no se llamó" '[ "$(llamadas_curl)" -eq 0 ]'

echo "== dump vacío: falla, avisa /fail y NO manda el ping de éxito"
ejecutar dumpvacio BETTERSTACK_HEARTBEAT_URL="$URL" ESC_DUMP_VACIO=1
comprobar "termina distinto de 0" '[ "$CODIGO" -ne 0 ]'
comprobar "un único curl, el de /fail" '[ "$(llamadas_curl)" -eq 1 ] && grep -q "$URL/fail" "$LOG_CURL"'
comprobar "no llegó a borg create" '! grep -q "^borg create" "$LOG_BORG"'

echo "== sin claves de Data Protection: falla y avisa /fail"
ejecutar sinclaves BETTERSTACK_HEARTBEAT_URL="$URL" ESC_SIN_CLAVES=1
comprobar "termina distinto de 0" '[ "$CODIGO" -ne 0 ]'
comprobar "solo el aviso /fail" '[ "$(llamadas_curl)" -eq 1 ] && grep -q "$URL/fail" "$LOG_CURL"'

echo "== borg create falla: falla y avisa /fail"
ejecutar borgroto BETTERSTACK_HEARTBEAT_URL="$URL" ESC_BORG_CREATE_FALLA=1
comprobar "termina distinto de 0" '[ "$CODIGO" -ne 0 ]'
comprobar "solo el aviso /fail" '[ "$(llamadas_curl)" -eq 1 ] && grep -q "$URL/fail" "$LOG_CURL"'
comprobar "no hubo prune tras el fallo" '! grep -q "^borg prune" "$LOG_BORG"'

echo "== fallo sin URL: no llama a la red aunque falle"
ejecutar fallosinurl ESC_DUMP_VACIO=1
comprobar "termina distinto de 0" '[ "$CODIGO" -ne 0 ]'
comprobar "curl no se llamó" '[ "$(llamadas_curl)" -eq 0 ]'

echo "== el ping falla tras un backup bueno: el backup sigue siendo bueno"
ejecutar pingroto BETTERSTACK_HEARTBEAT_URL="$URL" ESC_CURL_FALLA=1
comprobar "termina con 0" '[ "$CODIGO" -eq 0 ]'
comprobar "lo dice en la salida" 'printf "%s" "$SALIDA" | grep -q "el ping a Better Stack falló"'

echo "== URL con barra final: /fail sin barra doble"
ejecutar barra BETTERSTACK_HEARTBEAT_URL="$URL/" ESC_DUMP_VACIO=1
comprobar "el aviso va a .../TOKEN-DE-PRUEBA/fail" 'grep -q "TOKEN-DE-PRUEBA/fail" "$LOG_CURL" && ! grep -q "//fail" "$LOG_CURL"'


echo "== el .env de producción entra en el archivo, con 0600, y su contenido no sale por ningún lado"
ejecutar envok BETTERSTACK_HEARTBEAT_URL="$URL"
ARCH="$LOG_BORG.archivo"
comprobar "termina con 0" '[ "$CODIGO" -eq 0 ]'
comprobar "el archivo Borg lleva env-produccion, byte a byte igual al .env" '[ -f "$ARCH/env-produccion" ] && cmp -s "$ARCH/env-produccion" "$ENV_FALSO"'
comprobar "y sigue llevando dump, claves y documentos" '[ -s "$ARCH/CaeManager.dump" ] && [ -f "$ARCH/dataprotection-keys/key-x.xml" ] && [ -f "$ARCH/documentos/a.pdf" ]'
comprobar "permisos 0600" '[ "$(stat -c %a "$ARCH/env-produccion" 2>/dev/null || stat -f %Lp "$ARCH/env-produccion")" = "600" ]'
comprobar "el contenido NO sale en la salida del guion" '! printf "%s" "$SALIDA" | grep -q "$SENTINELA"'
comprobar "el contenido NO va en argv de borg" '! grep -q "$SENTINELA" "$LOG_BORG"'
comprobar "el contenido NO va en argv de curl ni en su configuración" '! grep -q "$SENTINELA" "$LOG_CURL"'
comprobar "el contenido NO va en argv de docker" '! grep -q "$SENTINELA" "$LOG_DOCKER"'
comprobar "la salida dice que se incluyó, sin mostrarlo" 'printf "%s" "$SALIDA" | grep -q "env-produccion"'

echo "== fallo tras copiar el .env: tampoco se filtra"
ejecutar envfalla BETTERSTACK_HEARTBEAT_URL="$URL" ESC_BORG_CREATE_FALLA=1
comprobar "termina distinto de 0" '[ "$CODIGO" -ne 0 ]'
comprobar "el contenido NO sale en la salida" '! printf "%s" "$SALIDA" | grep -q "$SENTINELA"'
comprobar "ni en los logs de curl / borg / docker" '! cat "$LOG_CURL" "$LOG_BORG" "$LOG_DOCKER" | grep -q "$SENTINELA"'

echo "== sin .env (ruta inexistente): la BD SE RESPALDA igualmente; el guion sale con error y avisa /fail"
ejecutar sinenv BETTERSTACK_HEARTBEAT_URL="$URL" ENV_PRODUCCION="$TMP_ROOT/no-existe"
comprobar "termina distinto de 0" '[ "$CODIGO" -ne 0 ]'
comprobar "dice qué falta y cómo salir" 'printf "%s" "$SALIDA" | grep -q "no hay .env de producción" && printf "%s" "$SALIDA" | grep -q "BACKUP_SIN_ENV=1"'
comprobar "se creó el archivo Borg" 'grep -q "^borg create" "$LOG_BORG"'
comprobar "el archivo lleva la BD, las claves y los documentos" '[ -s "$LOG_BORG.archivo/CaeManager.dump" ] && [ -f "$LOG_BORG.archivo/dataprotection-keys/key-x.xml" ] && [ -f "$LOG_BORG.archivo/documentos/a.pdf" ]'
comprobar "y no lleva env-produccion" '[ ! -e "$LOG_BORG.archivo/env-produccion" ]'
comprobar "prune y compact también corrieron (la retención no se salta)" 'grep -q "^borg prune" "$LOG_BORG" && grep -q "^borg compact" "$LOG_BORG"'
comprobar "avisó /fail y no mandó el ping de éxito (un único curl)" '[ "$(llamadas_curl)" -eq 1 ] && grep -q "$URL/fail" "$LOG_CURL"'
comprobar "la salida dice que la BD quedó respaldada sin el .env" 'printf "%s" "$SALIDA" | grep -q "BACKUP DE LA BD COMPLETADO" && ! printf "%s" "$SALIDA" | grep -q "^BACKUP COMPLETADO"'

echo "== .env vacío: igual (archivo con la BD, exit != 0, /fail)"
: > "$TMP_ROOT/env-vacio"
ejecutar envvacio BETTERSTACK_HEARTBEAT_URL="$URL" ENV_PRODUCCION="$TMP_ROOT/env-vacio"
comprobar "termina distinto de 0" '[ "$CODIGO" -ne 0 ]'
comprobar "hay archivo con la BD" '[ -s "$LOG_BORG.archivo/CaeManager.dump" ]'
comprobar "solo el aviso /fail" '[ "$(llamadas_curl)" -eq 1 ] && grep -q "$URL/fail" "$LOG_CURL"'

echo "== sin .env y sin heartbeat configurado: sigue saliendo con error y con el archivo hecho"
ejecutar sinenv-sinurl ENV_PRODUCCION="$TMP_ROOT/no-existe"
comprobar "termina distinto de 0, con la BD respaldada, sin llamadas de red" '[ "$CODIGO" -ne 0 ] && [ -s "$LOG_BORG.archivo/CaeManager.dump" ] && [ "$(llamadas_curl)" -eq 0 ]'

echo "== .env que existe pero no se puede copiar (hallazgo de Codex): la BD se respalda igual"
# Un directorio pasa `-s` (tiene tamaño) y hace fallar `install` con cualquier usuario,
# root incluido: reproduce «existe pero no se puede leer» sin depender de permisos.
mkdir -p "$TMP_ROOT/env-directorio"
ejecutar envilegible BETTERSTACK_HEARTBEAT_URL="$URL" ENV_PRODUCCION="$TMP_ROOT/env-directorio"
comprobar "termina distinto de 0" '[ "$CODIGO" -ne 0 ]'
comprobar "dice que existe pero no se pudo copiar" 'printf "%s" "$SALIDA" | grep -q "no se pudo copiar"'
comprobar "se creó el archivo Borg con la BD" '[ -s "$LOG_BORG.archivo/CaeManager.dump" ] && [ ! -e "$LOG_BORG.archivo/env-produccion" ]'
comprobar "avisó /fail y no mandó el ping de éxito" '[ "$(llamadas_curl)" -eq 1 ] && grep -q "$URL/fail" "$LOG_CURL"'

echo "== BACKUP_SIN_ENV=1: se omite a sabiendas, con aviso"
ejecutar sinenv-explicito ENV_PRODUCCION="$TMP_ROOT/no-existe" BACKUP_SIN_ENV=1
comprobar "termina con 0" '[ "$CODIGO" -eq 0 ]'
comprobar "avisa de que el archivo NO lleva el .env" 'printf "%s" "$SALIDA" | grep -q "NO incluye el .env"'
comprobar "el archivo Borg no lleva env-produccion" '[ ! -e "$LOG_BORG.archivo/env-produccion" ]'
echo ""
echo "$PRUEBAS comprobaciones, $FALLOS fallo(s)"
[ "$FALLOS" -eq 0 ]
