#!/usr/bin/env bash
# Control de estado-ramas.sh sobre repositorios sintéticos.
#
# Existe porque estado-ramas.sh no tiene forma de comprobarse contra el
# repositorio real: allí no hay una respuesta conocida con la que comparar, y
# las cuatro que da son justamente las que evitan retrabajo (CLAUDE.md § 21).
# Aquí sí: cada caso se construye con la respuesta decidida de antemano.
#
# Cada comprobación va emparejada con su MUTACIÓN: se altera el repositorio de
# forma que la respuesta correcta cambie, y se exige que el guion cambie con
# ella. Un detector que dice "OK" siempre también acertaría en los casos
# positivos sin observar nada (CLAUDE.md § 3).
#
# Uso:  bash scripts/control-estado-ramas.sh
# Sale con 0 si todo pasa, 1 a la primera que falle.

set -uo pipefail

GUION="$(cd "$(dirname "$0")" && pwd)/estado-ramas.sh"
[ -f "$GUION" ] || { echo "No encuentro $GUION" >&2; exit 1; }

RAIZ=$(mktemp -d)
trap 'rm -rf "$RAIZ"' EXIT

export GIT_AUTHOR_NAME=control GIT_AUTHOR_EMAIL=control@example.com
export GIT_COMMITTER_NAME=control GIT_COMMITTER_EMAIL=control@example.com

FALLOS=0
comprobar() { # <descripción> <esperado: si|no> <patrón> <salida>
  local desc="$1" esperado="$2" patron="$3" salida="$4" hay="no"
  grep -qE "$patron" <<< "$salida" && hay="si"
  if [ "$hay" = "$esperado" ]; then
    printf 'OK    %s\n' "$desc"
  else
    printf 'FALLO %s\n      esperaba %s "%s", obtuve %s\n' "$desc" "$esperado" "$patron" "$hay"
    FALLOS=$((FALLOS+1))
  fi
}

informe() { ( cd "$1" && bash "$GUION" --sin-fetch --todas 2>&1 ); }

fecha() { export GIT_AUTHOR_DATE="2026-01-$1T00:00:00 +0000"
          export GIT_COMMITTER_DATE="$GIT_AUTHOR_DATE"; }

# ── Caso 1: ramas cortadas de otra rama, y ficheros disputados ────────────────
uno="$RAIZ/uno"
mkdir -p "$uno"; ( cd "$uno" && git init --bare -q remoto.git && git init -q trabajo ) || exit 1
T="$uno/trabajo"
(
  cd "$T"
  git config user.name control; git config user.email control@example.com
  git remote add origin ../remoto.git
  fecha 01
  echo base > F1; echo base > F2; echo base > F4; git add .; git commit -qm c0
  git branch -M main; git push -q -u origin main

  fecha 04; git checkout -qb rama-a origin/main
  echo a >> F1; git commit -qam "a toca F1"; git push -q -u origin rama-a

  # Cortada de rama-a en vez de origin/main: lo que la sección 3 debe cazar.
  fecha 05; git checkout -qb rama-hija rama-a
  echo h > F3; git add F3; git commit -qm "hija toca F3"

  # Toca F1, que ya toca rama-a: lo que la sección 4 debe cazar.
  fecha 06; git checkout -qb rama-b origin/main
  echo b >> F1; echo b >> F2; git commit -qam "b toca F1 y F2"

  fecha 07; git checkout -qb rama-sola origin/main
  echo s >> F4; git commit -qam "sola toca F4"

  # Su punta está en el remoto bajo OTRO nombre.
  fecha 09; git checkout -qb rama-publicada-como origin/main
  echo p > F6; git add F6; git commit -qm "publicada con otro nombre"
  git push -q origin rama-publicada-como:refs/heads/otro-nombre
  git branch -q --unset-upstream rama-publicada-como 2>/dev/null

  fecha 10; git checkout -qb rama-grande origin/main
  for i in $(seq 1 61); do echo g > "G$i"; done
  git add .; git commit -qm "61 ficheros"

  git checkout -q main
  git worktree add -q ../wt-sola rama-sola
  git fetch -q origin
) >/dev/null 2>&1 || { echo "no pude construir el caso 1" >&2; exit 1; }

S=$(informe "$T")
comprobar "sección 3 caza la rama cortada de otra"  si "AVISO.*rama-a.*rama-hija" "$S"
comprobar "sección 4 caza el fichero disputado"     si "^F1$"                     "$S"
comprobar "  y nombra a las dos ramas que lo tocan" si "<- rama-b"                "$S"
comprobar "marca la rama sin empujar"               si "rama-hija.*SIN-EMPUJAR"   "$S"
comprobar "marca la publicada con otro nombre"      si "publicada-como:otro-nombre" "$S"
comprobar "marca el worktree que la ocupa"          si "rama-sola.*wt:wt-sola"    "$S"
comprobar "marca el diff grande"                    si "rama-grande.*DIFF-GRANDE" "$S"
comprobar "no inventa avisos donde no los hay"      no "rama-sola.*rama-b"        "$S"

# Mutación 1: la rama hija pasa a cortarse de origin/main. La sección 3 debe
# dejar de avisar; si sigue avisando, no estaba observando el parentesco.
( cd "$T" && git checkout -q rama-hija \
  && git rebase -q --onto origin/main rama-a rama-hija && git checkout -q main ) >/dev/null 2>&1
S=$(informe "$T")
comprobar "MUTACIÓN: recortada desde origin/main, la 3 calla" no "AVISO.*rama-hija" "$S"
comprobar "  y la 4 sigue viendo el fichero disputado"        si "^F1$"             "$S"

# Mutación 2: rama-b deja de tocar F1. La sección 4 debe quedarse sin disputa.
( cd "$T" && git branch -q -D rama-b && git checkout -qb rama-b origin/main \
  && echo b >> F2 && git commit -qam "b toca solo F2" && git checkout -q main ) >/dev/null 2>&1
S=$(informe "$T")
comprobar "MUTACIÓN: sin disputa, la 4 lo dice"     si "ninguna línea viva pisa"  "$S"

# ── Caso 2: fichero que solo aparece en la resolución de un merge ─────────────
# `git log --name-only` no emite los ficheros de un commit de merge salvo que se
# le pida el diff. Sin eso, un fichero creado al resolver desaparece del informe.
dos="$RAIZ/dos"
mkdir -p "$dos"; ( cd "$dos" && git init --bare -q remoto.git && git init -q trabajo ) || exit 1
T2="$dos/trabajo"
(
  cd "$T2"
  git config user.name control; git config user.email control@example.com
  git remote add origin ../remoto.git
  fecha 01
  echo base > F0; echo base > FM; git add .; git commit -qm c0
  git branch -M main; git push -q -u origin main

  fecha 02; git checkout -qb otra origin/main
  echo otra > FM; git commit -qam "otra toca FM"

  fecha 03; git checkout -qb con-merge origin/main
  echo mio > FM; git commit -qam "con-merge toca FM"
  git merge --no-ff otra -m "merge de otra" >/dev/null 2>&1
  echo resuelto > FM
  echo "solo en la resolucion" > FX      # ningún padre tocó FX
  git add FM FX; git commit -qm "merge de otra"

  fecha 04; git checkout -qb rival origin/main
  echo rival > FX; git add FX; git commit -qm "rival toca FX"
  git checkout -q main
  git fetch -q origin
) >/dev/null 2>&1 || { echo "no pude construir el caso 2" >&2; exit 1; }

# El caso solo prueba algo si el commit es de verdad un merge y contiene FX.
n=$( cd "$T2" && git rev-list --no-walk --parents con-merge | wc -w )
if [ "$n" -ne 3 ]; then
  echo "FALLO el caso 2 no construyó un commit de merge ($n): no prueba nada"
  FALLOS=$((FALLOS+1))
fi
S=$(informe "$T2")
comprobar "sección 4 ve el fichero creado al resolver un merge" si "^FX$" "$S"

# Mutación 3: el mismo guion sin el diff extra de las ramas con merge. Debe
# perder FX; si no lo pierde, este caso no estaba probando nada.
MUTADO="$RAIZ/estado-ramas-sin-diff-merge.sh"
awk '/^  sed -n .s\/\^M \/\/p/ {saltar=1} saltar && /^  fi$/ {saltar=0; next} !saltar'   "$GUION" > "$MUTADO"
S=$( cd "$T2" && bash "$MUTADO" --sin-fetch --todas 2>&1 )
comprobar "MUTACIÓN: sin el diff de las ramas con merge, FX se pierde" no "^FX$" "$S"

# ── Caso 3: resolución de merge que conserva la versión del primer padre ──────
# La rama nunca toca F en un commit propio; main sí lo cambia; la rama integra
# main y al resolver conserva su F heredado. El árbol de la rama difiere
# entonces de origin/main en F, pero ningún --diff-merges lo emite (el
# resultado coincide con un padre) ni hay commit propio que lo toque.
tres="$RAIZ/tres"
mkdir -p "$tres"; ( cd "$tres" && git init --bare -q remoto.git && git init -q trabajo ) || exit 1
T3="$tres/trabajo"
(
  cd "$T3"
  git config user.name control; git config user.email control@example.com
  git remote add origin ../remoto.git
  fecha 01
  echo v1 > F; echo x > OTRO; git add .; git commit -qm c0
  git branch -M main; git push -q -u origin main

  fecha 02; git checkout -qb rama origin/main
  echo r > OTRO; git commit -qam "rama toca OTRO, nunca F"

  fecha 03; git checkout -q main
  echo v2 > F; git commit -qam "main pasa F a v2"; git push -q origin main

  fecha 04; git checkout -q rama
  git merge --no-commit --no-ff main >/dev/null 2>&1
  echo v1 > F; git add F; git commit -qm "merge conservando F=v1"

  # Otra rama toca F: el solape con "rama" DEBE aparecer.
  fecha 05; git checkout -qb rival origin/main
  echo rival > F; git commit -qam "rival toca F"
  git checkout -q main
  git fetch -q origin
) >/dev/null 2>&1 || { echo "no pude construir el caso 3" >&2; exit 1; }

# El caso solo prueba algo si el árbol de la rama difiere de origin/main en F.
( cd "$T3" && git diff --name-only origin/main...rama | grep -qx F ) || {
  echo "FALLO el caso 3 no dejó F distinto de origin/main: no prueba nada"
  FALLOS=$((FALLOS+1)); }
S=$(informe "$T3")
comprobar "sección 4 ve el fichero que solo el diff completo revela" si "^F$" "$S"

# Mutación 4: sin el diff extra de las ramas con merge, F debe perderse.
MUT2="$RAIZ/estado-ramas-sin-diff-merge.sh"
awk '/^  sed -n .s\/\^M \/\/p/ {saltar=1} saltar && /^  fi$/ {saltar=0; next} !saltar' \
  "$GUION" > "$MUT2"
S=$( cd "$T3" && bash "$MUT2" --sin-fetch --todas 2>&1 )
comprobar "MUTACIÓN: sin el diff de las ramas con merge, F se pierde" no "^F$" "$S"

# ── Caso 4: un fichero con acentos llega por las dos vías ────────────────────
# Los ficheros salen de dos sitios: el log del grafo y, para las ramas con
# merge, el diff completo. Si uno escribe año.md y el otro "a\303\261o.md",
# son dos nombres distintos y el solape se pierde. Ocurre con la configuración
# por defecto de git, y este repositorio está lleno de ficheros con acentos.
cuatro="$RAIZ/cuatro"
mkdir -p "$cuatro"; ( cd "$cuatro" && git init --bare -q remoto.git && git init -q trabajo ) || exit 1
T4="$cuatro/trabajo"
(
  cd "$T4"
  git config user.name control; git config user.email control@example.com
  git remote add origin ../remoto.git
  fecha 01
  echo base > F0; echo v1 > "año.md"; git add .; git commit -qm c0
  git branch -M main; git push -q -u origin main

  # con-merge llega al fichero por la vía del DIFF: no lo toca en un commit
  # propio, pero al integrar main conserva su versión heredada.
  fecha 02; git checkout -qb con-merge origin/main
  echo mio > F0; git commit -qam "con-merge toca F0"
  fecha 03; git checkout -q main
  echo v2 > "año.md"; git commit -qam "main cambia el fichero con acento"
  git push -q origin main
  fecha 04; git checkout -q con-merge
  git merge --no-commit --no-ff main >/dev/null 2>&1
  echo v1 > "año.md"; git add "año.md"; git commit -qm "merge conservando su version"

  # rival llega al mismo fichero por la vía del LOG.
  fecha 05; git checkout -qb rival origin/main
  echo rival > "año.md"; git commit -qam "rival toca el fichero con acento"
  git checkout -q main
  git fetch -q origin
) >/dev/null 2>&1 || { echo "no pude construir el caso 4" >&2; exit 1; }

S=$(informe "$T4")
comprobar "las dos vías escriben igual un nombre con acentos" si "^año\.md$"        "$S"
comprobar "  y el solape aparece con las dos ramas"           si "<- con-merge"     "$S"
comprobar "  sin dejar la grafía escapada suelta"             no 'a.303.261o'       "$S"

# Mutación 5: sin core.quotePath=false en el diff, las dos grafías no se cruzan
# y el solape desaparece.
MUT3="$RAIZ/estado-ramas-con-quotepath.sh"
sed 's/git -c core.quotePath=false diff --name-only/git diff --name-only/' "$GUION" > "$MUT3"
S=$( cd "$T4" && bash "$MUT3" --sin-fetch --todas 2>&1 )
comprobar "MUTACIÓN: sin quotePath=false, el solape se pierde" no "<- con-merge"    "$S"

echo
if [ $FALLOS -eq 0 ]; then
  echo "Todas las comprobaciones pasaron."
else
  echo "$FALLOS comprobación(es) fallaron."
  exit 1
fi
