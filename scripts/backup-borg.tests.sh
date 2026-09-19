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
if [ "$1" = "create" ] && [ "${ESC_BORG_CREATE_FALLA:-0}" = "1" ]; then exit 2; fi
exit 0
EOF
cat > "$MOCK_BIN/curl" <<'EOF'
#!/bin/bash
echo "curl $*" >> "$LOG_CURL"
[ "${ESC_CURL_FALLA:-0}" = "1" ] && exit 22
exit 0
EOF
chmod +x "$MOCK_BIN"/*

FALLOS=0
PRUEBAS=0

# ejecutar NOMBRE [VAR=valor ...] -> deja $SALIDA, $CODIGO, $LOG_CURL, $LOG_BORG
ejecutar() {
  local nombre="$1"; shift
  export LOG_CURL="$TMP_ROOT/$nombre.curl" LOG_BORG="$TMP_ROOT/$nombre.borg"
  : > "$LOG_CURL"; : > "$LOG_BORG"
  SALIDA=$(env "$@" PATH="$MOCK_BIN:$PATH" BORG_REPO=repo-falso BORG_PASSPHRASE=x \
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
comprobar "el curl va a la URL exacta" 'grep -q " $URL\$" "$LOG_CURL"'
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

echo ""
echo "$PRUEBAS comprobaciones, $FALLOS fallo(s)"
[ "$FALLOS" -eq 0 ]
