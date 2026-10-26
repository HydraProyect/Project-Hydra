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
#  (5) trinquete estático (P1-F3 cerrada): la bandera de claves sin cifrar se
#      emite una sola vez y solo en la rama SIN --certificado-dp; con certificado
#      el ensayo arranca como los compose desplegados, sin ella.
#  (6) --certificado-dp: si el UID de la app en el contenedor (1654) no puede leer
#      los certificados o atravesar el directorio, sale con 2 ANTES de la fase 1
#      (sin llamadas a docker) y dice el chown/chmod que lo arregla;
#  (7) la cadencia se mide solo entre las copias de los últimos 8 días (con un `borg`
#      falso): un mensual lejano no la rompe y un día sin copia sí.
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
  SALIDA=$(env PATH="$MOCK_BIN:$PATH" "$@" bash "$SCRIPT" --desde-dir "$BACKUP" --marca-backup "$MARCA" --sin-app ${ARGS_SCRIPT:-} 2>&1); CODIGO=$?
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

echo "== trinquete estático: con --certificado-dp, sin bandera de claves en claro"
comprobar "la bandera se emite una sola vez" '[ "$(grep -c "^[^#]*PermitirClavesSinCifrar" "$SCRIPT")" -eq 1 ]'
comprobar "y justo tras el else de la rama con certificado" 'grep -B1 "^[^#]*PermitirClavesSinCifrar" "$SCRIPT" | head -1 | grep -qx " *else"'

echo "== --certificado-dp: el UID 1654 de la app (no quien ejecuta el guion) debe poder leerlo"
# Propietario y modo se leen con `stat -c`; aquí el propietario es quien ejecuta la
# prueba (no 1654), así que cuentan los bits de «otros». Si la prueba corriera como
# 1654 contaría el propietario y los casos no significarían lo mismo.
if [ "$(id -u)" = "1654" ]; then
  echo "  (omitido: la prueba corre como UID 1654)"
else
  # crear_certs NOMBRE MODO_FICHEROS MODO_DIR -> $DIR_CERT con el par principal
  crear_certs() {
    DIR_CERT="$TMP_ROOT/$1"; mkdir -p "$DIR_CERT"
    echo crt > "$DIR_CERT/dataprotection.crt"; echo key > "$DIR_CERT/dataprotection.key"
    chmod "$2" "$DIR_CERT/dataprotection.crt" "$DIR_CERT/dataprotection.key"; chmod "$3" "$DIR_CERT"
  }

  crear_certs cert-600 600 750
  ARGS_SCRIPT="--certificado-dp $DIR_CERT" ejecutar cert-600
  comprobar "ficheros 600 de otro propietario: sale con 2" '[ "$CODIGO" -eq 2 ]'
  comprobar "nombra el fichero ilegible" 'printf "%s" "$SALIDA" | grep -q "dataprotection.key  (el UID 1654 no puede leerlo)"'
  comprobar "da el comando que lo arregla" 'printf "%s" "$SALIDA" | grep -qF "sudo chown -R 1654:\$(id -g) $DIR_CERT; sudo chmod 750 $DIR_CERT; sudo chmod 440 $DIR_CERT/*"'
  comprobar "antes de la fase 1: ni una llamada a docker" '[ "$(llamadas_docker)" -eq 0 ]'

  crear_certs cert-644 644 755
  ARGS_SCRIPT="--certificado-dp $DIR_CERT" ejecutar cert-644
  comprobar "ficheros 644 y directorio 755: no sale con 2" '[ "$CODIGO" -ne 2 ]'
  comprobar "y sigue hasta crear la red" 'grep -q "^docker network create" "$LOG_DOCKER"'

  crear_certs cert-dir-700 644 700
  ARGS_SCRIPT="--certificado-dp $DIR_CERT" ejecutar cert-dir-700
  comprobar "directorio 700 (sin x para otros): sale con 2" '[ "$CODIGO" -eq 2 ]'
  comprobar "dice que no puede atravesarlo" 'printf "%s" "$SALIDA" | grep -q "no puede atravesarlo"'

  crear_certs cert-anterior 644 755
  echo key > "$DIR_CERT/dataprotection-2026.key"; chmod 600 "$DIR_CERT/dataprotection-2026.key"
  echo crt > "$DIR_CERT/dataprotection-2026.crt"; chmod 644 "$DIR_CERT/dataprotection-2026.crt"
  ARGS_SCRIPT="--certificado-dp $DIR_CERT" ejecutar cert-anterior
  comprobar "un dataprotection-*.key anterior en 600: sale con 2" '[ "$CODIGO" -eq 2 ]'
  comprobar "nombra el anterior" 'printf "%s" "$SALIDA" | grep -q "dataprotection-2026.key"'
  comprobar "y no nombra el principal, que sí se lee" '! printf "%s" "$SALIDA" | grep -q "dataprotection.key  "'

  crear_certs cert-otro 644 755
  echo key > "$DIR_CERT/otro.key"; chmod 600 "$DIR_CERT/otro.key"
  ARGS_SCRIPT="--certificado-dp $DIR_CERT" ejecutar cert-otro
  comprobar "otros certificados del directorio (otro.key 600) no cuentan" '[ "$CODIGO" -ne 2 ]'
fi

echo "== cadencia: el hueco se mide solo entre las copias diarias recientes (8 días)"
# Un `borg` falso: `list` imprime los archivos de ESC_ARCHIVOS y `extract` deja un
# «backup extraído» mínimo en el directorio actual. Con el docker falso, el ensayo
# se detiene en `network create`, después de registrar el resultado de la cadencia.
cat > "$MOCK_BIN/borg" <<'EOF'
#!/bin/bash
case "$1" in
  list) printf '%s\n' $ESC_ARCHIVOS ;;
  extract) echo dump > CaeManager.dump; mkdir -p dataprotection-keys documentos ;;
esac
exit 0
EOF
chmod +x "$MOCK_BIN/borg"

marca_hace() { date -u -d "$1 days ago 00:00:01" +%Y-%m-%dT%H-%M-%S; }
# archivos_diarios SALTADOS... -> los de hace 0..6 días (uno al día) menos los días saltados,
# más un mensual de hace 40 días (el hueco entre retenidos, de más de 5 semanas, no es cadencia).
archivos_diarios() {
  local d s lista="caemanager-$(marca_hace 40)"
  for d in 0 1 2 3 4 5 6; do
    for s in "$@"; do [ "$s" = "$d" ] && continue 2; done
    lista="$lista caemanager-$(marca_hace "$d")"
  done
  printf '%s' "$lista"
}
ejecutar_borg() {   # ejecutar_borg NOMBRE ARCHIVOS -> $SALIDA, $CODIGO
  local nombre="$1" archivos="$2"
  export LOG_DOCKER="$TMP_ROOT/$nombre.docker"; : > "$LOG_DOCKER"
  SALIDA=$(env PATH="$MOCK_BIN:$PATH" BORG_REPO=ssh://prueba BORG_PASSPHRASE=prueba \
      ESC_ARCHIVOS="$archivos" bash "$SCRIPT" --sin-app 2>&1); CODIGO=$?
}

ejecutar_borg cadencia-ok "$(archivos_diarios)"
comprobar "siete diarios y un mensual de hace 40 días: la cadencia diaria sale OK" 'printf "%s" "$SALIDA" | grep -q "\[OK\] Hueco máximo entre copias diarias recientes"'
comprobar "el hueco de 40 días no cuenta (24 h, no ~900 h)" 'printf "%s" "$SALIDA" | grep -q "Hueco máximo entre copias diarias recientes.*24 h 0 min"'
comprobar "llegó a crear la red (el ensayo siguió)" 'grep -q "^docker network create" "$LOG_DOCKER"'

ejecutar_borg cadencia-falta-un-dia "$(archivos_diarios 3)"
comprobar "falta la copia de un día: la cadencia diaria sale FALLO" 'printf "%s" "$SALIDA" | grep -q "\[FALLO\] Hueco máximo entre copias diarias recientes"'
comprobar "con 48 h de hueco" 'printf "%s" "$SALIDA" | grep -q "Hueco máximo entre copias diarias recientes.*48 h 0 min"'

echo "== trinquete estático: el hueco total entre retenidos no se llama cadencia"
comprobar "el resumen lo rotula informativo" 'grep -q "^echo \"Hueco entre retenidos: .*informativo, NO es cadencia" "$SCRIPT"'
comprobar "el resumen de RPO habla de copias diarias recientes" 'grep -q "hueco máximo entre copias diarias recientes: " "$SCRIPT"'

echo "== trinquete estático: el UID de la app sale a una constante"
comprobar "APP_UID_CONTENEDOR=1654 definido una vez" '[ "$(grep -c "^APP_UID_CONTENEDOR=1654$" "$SCRIPT")" -eq 1 ]'
comprobar "ningún 1654 literal fuera de la constante y de comentarios" '! grep -nE "^[^#]*1654" "$SCRIPT" | grep -v "APP_UID_CONTENEDOR=1654"'

echo ""
echo "$PRUEBAS comprobaciones, $FALLOS fallo(s)"
[ "$FALLOS" -eq 0 ]
