#!/bin/bash
# Prueba de la copia cifrada de las claves de Data Protection (P38) con `age`
# REAL — el cifrado es justo lo que no se puede simular — y un `docker` falso
# que entrega un llavero de mentira con `docker cp`.
#
# Propiedades: (1) la copia NO deja ver el XML de las claves; (2) la identidad
# correcta la abre y muestra las ids; (3) otra identidad NO la abre; (4) sin
# claves no queda ningún fichero a medias; (5) pasar la identidad secreta en vez
# de la clave pública se rechaza antes de escribir nada.
#
# Requiere `age` y `age-keygen` en el PATH (CI: apt-get install age).
# Ejecutable a mano: bash scripts/dataprotection-copia-cifrada.tests.sh
set -uo pipefail

command -v age >/dev/null && command -v age-keygen >/dev/null \
  || { echo "ERROR: faltan age / age-keygen en el PATH — esta prueba no puede dar verde sin ellos"; exit 1; }

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPT="$SCRIPT_DIR/dataprotection-copia-cifrada.sh"
TMP_ROOT="$(mktemp -d)"
trap 'rm -rf "$TMP_ROOT"' EXIT
MOCK_BIN="$TMP_ROOT/bin"; mkdir -p "$MOCK_BIN"
# Id de fixture armada en ejecución: el GUID literal lo marcó gitleaks (generic-api-key) en la PR #738
# Id de fixture armada en ejecución: un GUID literal asignado a una variable con «key» lo marca gitleaks (generic-api-key)
ID_CLAVE="$(printf "%s-%s-%s-%s-%s" a4055d2f cdc8 4eac b982 9c93dfca7cc2)"
cat > "$MOCK_BIN/docker" <<'EOF'
#!/bin/bash
# docker cp CONTENEDOR:/data/dataprotection-keys DEST
if [ "$1" = "cp" ]; then
  mkdir -p "$3"
  if [ "${ESC_SIN_CLAVES:-0}" != "1" ]; then
    printf '<?xml version="1.0"?><key id="%s" version="1"><creationDate>2026-01-01T00:00:00Z</creationDate><expirationDate>2026-04-01T00:00:00Z</expirationDate><descriptor><masterKey>SECRETO-EN-CLARO</masterKey></descriptor></key>' "$ESC_ID" > "$3/key-$ESC_ID.xml"
  fi
  exit 0
fi
exit 0
EOF
chmod +x "$MOCK_BIN/docker"
export ESC_ID="$ID_CLAVE"

age-keygen -o "$TMP_ROOT/id-buena.txt" 2>/dev/null
age-keygen -o "$TMP_ROOT/id-otra.txt" 2>/dev/null
PUB="$(age-keygen -y "$TMP_ROOT/id-buena.txt")"
SECRETA="$(grep '^AGE-SECRET-KEY-' "$TMP_ROOT/id-buena.txt")"

FALLOS=0; PRUEBAS=0
comprobar() {
  PRUEBAS=$((PRUEBAS + 1))
  if eval "$2"; then echo "  ok   $1"; else echo "  FALLO $1"; FALLOS=$((FALLOS + 1)); fi
}
correr() { env PATH="$MOCK_BIN:$PATH" "$@" 2>&1; }

DEST="$TMP_ROOT/destino"

echo "== exportar"
SALIDA=$(correr AGE_RECIPIENT="$PUB" bash "$SCRIPT" exportar "$DEST"); CODIGO=$?
FICHERO=$(ls "$DEST"/dataprotection-keys-*.tar.age 2>/dev/null | head -1)
comprobar "termina con 0 y deja un .age" '[ "$CODIGO" -eq 0 ] && [ -s "$FICHERO" ]'
comprobar "el fichero no contiene el XML ni el secreto en claro" '! grep -aq "SECRETO-EN-CLARO\|masterKey\|<key id" "$FICHERO"'
comprobar "la salida da la huella y la id de la clave" 'printf "%s" "$SALIDA" | grep -q "SHA-256" && printf "%s" "$SALIDA" | grep -q "$ID_CLAVE"'
comprobar "la salida NO imprime el material de la clave" '! printf "%s" "$SALIDA" | grep -q "SECRETO-EN-CLARO"'
comprobar "permisos 0600" '[ "$(stat -c %a "$FICHERO" 2>/dev/null || stat -f %Lp "$FICHERO")" = "600" ]'

echo "== verificar"
SALIDA=$(correr bash "$SCRIPT" verificar "$FICHERO" "$TMP_ROOT/id-buena.txt"); CODIGO=$?
comprobar "con la identidad correcta: 0 y muestra la id" '[ "$CODIGO" -eq 0 ] && printf "%s" "$SALIDA" | grep -q "$ID_CLAVE"'
SALIDA=$(correr bash "$SCRIPT" verificar "$FICHERO" "$TMP_ROOT/id-otra.txt"); CODIGO=$?
comprobar "con otra identidad: falla" '[ "$CODIGO" -ne 0 ]'

echo "== sin claves en el contenedor"
DEST2="$TMP_ROOT/destino2"
SALIDA=$(correr ESC_SIN_CLAVES=1 AGE_RECIPIENT="$PUB" bash "$SCRIPT" exportar "$DEST2"); CODIGO=$?
comprobar "falla" '[ "$CODIGO" -ne 0 ]'
comprobar "no deja ningún .age" '! ls "$DEST2"/*.age >/dev/null 2>&1'

echo "== AGE_RECIPIENT es la identidad secreta"
DEST3="$TMP_ROOT/destino3"
SALIDA=$(correr AGE_RECIPIENT="$SECRETA" bash "$SCRIPT" exportar "$DEST3"); CODIGO=$?
comprobar "se rechaza (código 2)" '[ "$CODIGO" -eq 2 ]'
comprobar "no escribe nada" '! ls "$DEST3"/*.age >/dev/null 2>&1'

echo "== desde-directorio (sin docker)"
ORIGEN="$TMP_ROOT/origen"; mkdir -p "$ORIGEN/dataprotection-keys"
printf '<key id="%s" version="1"><expirationDate>2026-04-01T00:00:00Z</expirationDate><descriptor><masterKey>SECRETO-EN-CLARO</masterKey></descriptor></key>' "$ID_CLAVE" > "$ORIGEN/dataprotection-keys/key-$ID_CLAVE.xml"
DEST4="$TMP_ROOT/destino4"
SALIDA=$(correr AGE_RECIPIENT="$PUB" bash "$SCRIPT" desde-directorio "$ORIGEN" "$DEST4"); CODIGO=$?
F4=$(ls "$DEST4"/dataprotection-keys-*.tar.age 2>/dev/null | head -1)
comprobar "deja un .age con la id de la clave y sin el secreto en claro" '[ "$CODIGO" -eq 0 ] && [ -s "$F4" ] && printf "%s" "$SALIDA" | grep -q "$ID_CLAVE" && ! grep -aq "SECRETO-EN-CLARO" "$F4"'
SALIDA=$(correr bash "$SCRIPT" verificar "$F4" "$TMP_ROOT/id-buena.txt"); CODIGO=$?
comprobar "la identidad correcta lo abre" '[ "$CODIGO" -eq 0 ] && printf "%s" "$SALIDA" | grep -q "$ID_CLAVE"'
SALIDA=$(correr AGE_RECIPIENT="$PUB" bash "$SCRIPT" desde-directorio "$TMP_ROOT/vacio" "$TMP_ROOT/destino5"); CODIGO=$?
comprobar "carpeta sin claves: falla y no deja .age" '[ "$CODIGO" -ne 0 ] && ! ls "$TMP_ROOT"/destino5/*.age >/dev/null 2>&1'

echo ""
echo "$PRUEBAS comprobaciones, $FALLOS fallo(s)"
[ "$FALLOS" -eq 0 ]
