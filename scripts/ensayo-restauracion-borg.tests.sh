#!/bin/bash
# Prueba de la guarda de producción de ensayo-restauracion-borg.sh, con un
# `docker` falso en el PATH que responde a `docker ps -a` y registra cada
# llamada. Sin Docker, sin red, sin Borg.
#
# Propiedades:
#  (1) si existe el contenedor caemanager-app o caemanager-db, el ensayo se
#      niega con código 3 ANTES de tocar nada (la única llamada a docker es `ps`);
#  (2) ENSAYO_PERMITIR_EN_SERVIDOR=1 lo levanta (prueba de que la guarda era lo
#      único que lo frenaba: el doble de docker hace fallar `network create` y el
#      ensayo aborta ahí, con un código que no es 3);
#  (3) nombres parecidos (caemanager-app-staging, caemanager-db2) NO disparan la
#      guarda: la comparación es por nombre exacto;
#  (4) trinquete estático, NO de comportamiento: la contraseña de login no vuelve
#      a pasarse con `-e VAR=valor` ni como argumento de `--data-urlencode`.
#      El comportamiento real (que no salga en `ps`) solo se comprobaría con Docker
#      y una app arrancada; esto solo impide reintroducir la forma que lo causó.
#
# Ejecutable a mano: bash scripts/ensayo-restauracion-borg.tests.sh
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPT="${ENSAYO_SCRIPT:-$SCRIPT_DIR/ensayo-restauracion-borg.sh}"
TMP_ROOT="$(mktemp -d)"
trap 'rm -rf "$TMP_ROOT"' EXIT
MOCK_BIN="$TMP_ROOT/bin"; mkdir -p "$MOCK_BIN"

cat > "$MOCK_BIN/docker" <<'EOF'
#!/bin/bash
echo "docker $*" >> "$LOG_DOCKER"
case "$1" in
  ps) printf '%s\n' ${ESC_CONTENEDORES:-}; exit 0 ;;
  network) exit 1 ;;   # el ensayo real llegaría hasta aquí; el doble lo detiene
esac
exit 0
EOF
chmod +x "$MOCK_BIN/docker"

# Un «backup ya extraído» mínimo: basta para que el ensayo llegue a `docker network create`.
BACKUP="$TMP_ROOT/backup"; mkdir -p "$BACKUP/dataprotection-keys" "$BACKUP/documentos"
echo "dump" > "$BACKUP/CaeManager.dump"
MARCA="$(date -u +%Y-%m-%dT%H-%M-%S)"

FALLOS=0; PRUEBAS=0
comprobar() {
  PRUEBAS=$((PRUEBAS + 1))
  if eval "$2"; then echo "  ok   $1"; else echo "  FALLO $1"; FALLOS=$((FALLOS + 1)); fi
}

# ejecutar NOMBRE [VAR=valor ...] -> $SALIDA, $CODIGO, $LOG_DOCKER
ejecutar() {
  local nombre="$1"; shift
  export LOG_DOCKER="$TMP_ROOT/$nombre.docker"; : > "$LOG_DOCKER"
  SALIDA=$(env PATH="$MOCK_BIN:$PATH" "$@" bash "$SCRIPT" --desde-dir "$BACKUP" --marca-backup "$MARCA" --sin-app 2>&1); CODIGO=$?
}
llamadas_docker() { grep -c '^docker' "$LOG_DOCKER"; }

for c in caemanager-app caemanager-db; do
  echo "== existe $c: se niega"
  ejecutar "guarda-$c" ESC_CONTENEDORES="$c otro"
  comprobar "sale con 3" '[ "$CODIGO" -eq 3 ]'
  comprobar "dice por qué" 'printf "%s" "$SALIDA" | grep -q "existe el stack real"'
  comprobar "la única llamada a docker fue ps" '[ "$(llamadas_docker)" -eq 1 ] && grep -q "^docker ps" "$LOG_DOCKER"'
done

echo "== ENSAYO_PERMITIR_EN_SERVIDOR=1: la guarda se levanta"
ejecutar permitido ESC_CONTENEDORES="caemanager-app" ENSAYO_PERMITIR_EN_SERVIDOR=1
comprobar "no sale con 3" '[ "$CODIGO" -ne 3 ]'
comprobar "llegó a crear la red (lo único que lo frenaba era la guarda)" 'grep -q "^docker network create" "$LOG_DOCKER"'

echo "== nombres parecidos: no disparan la guarda"
ejecutar parecidos ESC_CONTENEDORES="caemanager-app-staging caemanager-db2 xcaemanager-app"
comprobar "no sale con 3" '[ "$CODIGO" -ne 3 ]'
comprobar "llegó a crear la red" 'grep -q "^docker network create" "$LOG_DOCKER"'

echo "== trinquete estático: la contraseña de login no va en argv"
comprobar "sin docker exec -e ...CLAVE...=valor" '! grep -nE -- "-e +[A-Za-z_]*CLAVE[A-Za-z_]*=" "$SCRIPT"'
comprobar "sin --data-urlencode Entrada.Password=valor" '! grep -nE -- "--data-urlencode +\"Entrada\.Password=" "$SCRIPT"'
comprobar "la contraseña entra por stdin (Entrada.Password@-)" 'grep -q "Entrada\.Password@-" "$SCRIPT"'

echo ""
echo "$PRUEBAS comprobaciones, $FALLOS fallo(s)"
[ "$FALLOS" -eq 0 ]
