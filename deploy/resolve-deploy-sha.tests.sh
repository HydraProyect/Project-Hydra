#!/bin/bash
# Prueba de mutación de resolve-deploy-sha.sh, sin Docker ni el VPS real:
# un repo git en un directorio temporal hace de "origin" y de working tree.
#
# La propiedad que importa de verdad no es "despliega" — es que un SHA
# aprobado siga siendo el que se despliega aunque main avance mientras tanto:
#
#   workflow pide SHA A
#   main avanza a SHA B
#   -> la máquina debe seguir desplegando A, no B
#
# Corre en CI (ver .github/workflows/ci.yml, job deploy-script-tests) sobre
# cada PR que toque deploy/resolve-deploy-sha.sh o deploy/ci-deploy.sh.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RESOLVER="$SCRIPT_DIR/resolve-deploy-sha.sh"

TMP_ROOT="$(mktemp -d)"
trap 'rm -rf "$TMP_ROOT"' EXIT

ORIGIN_DIR="$TMP_ROOT/origin.git"
WORK_DIR="$TMP_ROOT/trabajo"

git init --quiet --bare "$ORIGIN_DIR"

git init --quiet "$WORK_DIR"
git -C "$WORK_DIR" config user.email "test@example.com"
git -C "$WORK_DIR" config user.name "Test"
git -C "$WORK_DIR" config commit.gpgsign false
git -C "$WORK_DIR" remote add origin "$ORIGIN_DIR"

FALLOS=0
PRUEBAS=0

assert_eq() {
  local descripcion="$1" esperado="$2" real="$3"
  PRUEBAS=$((PRUEBAS + 1))
  if [[ "$esperado" != "$real" ]]; then
    echo "FALLO: $descripcion — esperado '$esperado', obtenido '$real'" >&2
    FALLOS=$((FALLOS + 1))
  else
    echo "OK: $descripcion"
  fi
}

assert_fails() {
  local descripcion="$1"; shift
  PRUEBAS=$((PRUEBAS + 1))
  if "$@" >/tmp/resolve-deploy-sha-test-output 2>&1; then
    echo "FALLO: $descripcion — se esperaba que el comando fallara y tuvo éxito" >&2
    FALLOS=$((FALLOS + 1))
  else
    echo "OK: $descripcion"
  fi
}

# --- Preparación: commit A en main, luego commit B (main "avanza") ---
echo "a" > "$WORK_DIR/archivo.txt"
git -C "$WORK_DIR" add archivo.txt
git -C "$WORK_DIR" commit --quiet -m "commit A"
SHA_A="$(git -C "$WORK_DIR" rev-parse HEAD)"
git -C "$WORK_DIR" branch -M main
git -C "$WORK_DIR" push --quiet origin main

echo "b" > "$WORK_DIR/archivo.txt"
git -C "$WORK_DIR" add archivo.txt
git -C "$WORK_DIR" commit --quiet -m "commit B"
SHA_B="$(git -C "$WORK_DIR" rev-parse HEAD)"
git -C "$WORK_DIR" push --quiet origin main

# --- Prueba 1 (la que importa): pedir A cuando origin/main ya está en B ---
"$RESOLVER" "$WORK_DIR" "$SHA_A"
HEAD_TRAS_RESOLVER="$(git -C "$WORK_DIR" rev-parse HEAD)"
assert_eq "pide SHA_A con origin/main ya avanzado a SHA_B -> despliega A, no B" \
  "$SHA_A" "$HEAD_TRAS_RESOLVER"

# --- Prueba 2: pedir B (el más reciente) sigue funcionando ---
"$RESOLVER" "$WORK_DIR" "$SHA_B"
HEAD_TRAS_RESOLVER_B="$(git -C "$WORK_DIR" rev-parse HEAD)"
assert_eq "pide SHA_B (el más reciente) -> despliega B" \
  "$SHA_B" "$HEAD_TRAS_RESOLVER_B"

# --- Prueba 3: SHA con formato inválido se rechaza sin tocar el working tree ---
assert_fails "rechaza un SHA corto (abreviado)" "$RESOLVER" "$WORK_DIR" "${SHA_A:0:7}"
assert_fails "rechaza un SHA con caracteres no hexadecimales" "$RESOLVER" "$WORK_DIR" "g0000000000000000000000000000000000000"
assert_fails "rechaza intento de inyección de comando" "$RESOLVER" "$WORK_DIR" "; rm -rf / #"

# --- Prueba 4: SHA bien formado pero inexistente se rechaza, no revienta ---
assert_fails "rechaza un SHA de 40 hex bien formado pero inexistente" \
  "$RESOLVER" "$WORK_DIR" "0000000000000000000000000000000000000000"

# --- Prueba 5: SHA real pero de una rama nunca mergeada a main se rechaza ---
git -C "$WORK_DIR" checkout --quiet -b rama-no-mergeada
echo "c" > "$WORK_DIR/archivo.txt"
git -C "$WORK_DIR" add archivo.txt
git -C "$WORK_DIR" commit --quiet -m "commit fuera de main"
SHA_FUERA_DE_MAIN="$(git -C "$WORK_DIR" rev-parse HEAD)"
git -C "$WORK_DIR" push --quiet origin rama-no-mergeada
git -C "$WORK_DIR" checkout --quiet main

assert_fails "rechaza un commit real que no es ancestro de origin/main" \
  "$RESOLVER" "$WORK_DIR" "$SHA_FUERA_DE_MAIN"

echo
echo "$PRUEBAS pruebas, $FALLOS fallos"
[[ "$FALLOS" -eq 0 ]]
