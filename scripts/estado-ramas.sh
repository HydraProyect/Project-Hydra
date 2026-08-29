#!/usr/bin/env bash
# Situación de las líneas de trabajo vivas, para que una sesión sepa de dónde
# parte y con quién se va a chocar ANTES de escribir la primera línea.
#
# Nace de un fallo real y medido (2026-08-29): los PRs #228, #229, #230 y #302
# se cortaron de una rama local de trabajo en vez de origin/main. Cuando el
# contenido de esa rama entró en main por squash (#227), sus 18 commits
# quedaron con SHA distinto pese a estar ya el contenido integrado, el diff de
# cada PR hijo se infló a ~170 ficheros y los cuatro hubo que tirarlos y
# rehacerlos. La regla de CLAUDE.md § 21 ("toda sesión declara su base") ya lo
# decía en prosa; esto la vuelve ejecutable.
#
# Solo lee: hace fetch y consulta. No toca ramas, worktrees ni el índice.
#
# Uso:  bash scripts/estado-ramas.sh [--sin-fetch]

set -uo pipefail

SIN_FETCH=0
[ "${1:-}" = "--sin-fetch" ] && SIN_FETCH=1

hay_color=0
[ -t 1 ] && hay_color=1
c() { [ $hay_color -eq 1 ] && printf '\033[%sm' "$1" || true; }
rojo() { c 31; }; verde() { c 32; }; ambar() { c 33; }; gris() { c 90; }; fin() { c 0; }

if [ $SIN_FETCH -eq 0 ]; then
  echo "Actualizando referencias remotas..."
  if ! git fetch --all --prune --quiet; then
    echo "fetch falló: el resto del informe miraría referencias obsoletas." >&2
    exit 1
  fi
fi

BASE=origin/main
if ! git rev-parse --verify --quiet "$BASE" >/dev/null; then
  echo "No existe $BASE." >&2; exit 1
fi

# ── 1. Tu base ────────────────────────────────────────────────────────────────
RAMA_ACTUAL=$(git branch --show-current)
echo
echo "════ 1. TU BASE ════"
echo "Rama actual: ${RAMA_ACTUAL:-(HEAD suelto)}"
if git merge-base --is-ancestor "$BASE" HEAD 2>/dev/null; then
  verde; echo "OK  origin/main es ancestro de HEAD: partes de la punta actual."; fin
else
  DETRAS=$(git rev-list --count "HEAD..$BASE")
  rojo; echo "AVISO  origin/main NO es ancestro de HEAD ($DETRAS commits por delante)."; fin
  echo "       Antes de trabajar:  git merge origin/main"
  echo "       Cortar de una base vieja produce diffs enormes que no son tuyos."
fi

# ── Inventario de líneas vivas ────────────────────────────────────────────────
# Línea viva = rama con commits fuera de origin/main. Una rama local y su copia
# remota son la MISMA línea: se cuentan una vez, con el nombre local, que es el
# que alguien está editando.
declare -A PUNTA_DE NOMBRE_POR_SHA PUBLICADA WT_DE
VIVAS=()
while read -r ref; do
  [ -z "$ref" ] && continue
  case "$ref" in
    main|origin|origin/main|origin/staging|staging) continue ;;
    origin/gh-readonly-queue/*) continue ;;
  esac
  git rev-parse --verify --quiet "$ref" >/dev/null || continue
  [ "$(git rev-list --count "$BASE..$ref")" -eq 0 ] && continue

  sha=$(git rev-parse "$ref")
  anterior="${NOMBRE_POR_SHA[$sha]:-}"
  if [ -n "$anterior" ]; then
    case "$ref" in
      origin/*) PUBLICADA["$anterior"]=1; continue ;;
    esac
    # Llega la local y ya teníamos la remota: sustituimos el nombre.
    nuevas=()
    for r in "${VIVAS[@]}"; do [ "$r" != "$anterior" ] && nuevas+=("$r"); done
    VIVAS=("${nuevas[@]}")
    unset "PUNTA_DE[$anterior]"
    PUBLICADA["$ref"]=1
  fi
  NOMBRE_POR_SHA[$sha]="$ref"
  VIVAS+=("$ref")
  PUNTA_DE["$ref"]=$sha
done < <(git for-each-ref --format='%(refname:short)' refs/heads refs/remotes/origin)

# Worktree que ocupa cada rama: dice si hay una sesión sentada encima.
WT_ACT=""
while read -r linea; do
  case "$linea" in
    worktree\ *) WT_ACT="${linea#worktree }" ;;
    branch\ *) WT_DE["${linea#branch refs/heads/}"]="$(basename "$WT_ACT")" ;;
  esac
done < <(git worktree list --porcelain)

# ── 2. Líneas vivas ───────────────────────────────────────────────────────────
echo
echo "════ 2. LÍNEAS DE TRABAJO VIVAS ════"
if [ ${#VIVAS[@]} -eq 0 ]; then
  echo "(ninguna: todo lo que existe está en origin/main)"
else
  printf '%-52s %6s %7s %6s  %s\n' "RAMA" "AHEAD" "BEHIND" "FICH." "ESTADO"
  for r in "${VIVAS[@]}"; do
    A=$(git rev-list --count "$BASE..$r")
    B=$(git rev-list --count "$r..$BASE")
    F=$(git diff --name-only "$BASE...$r" | wc -l | tr -d ' ')
    notas=""
    case "$r" in
      origin/*) ;;
      *) up=$(git for-each-ref --format='%(upstream:short)' "refs/heads/$r")
         if [ -z "${PUBLICADA[$r]:-}" ] && [ -z "$up" ]; then
           notas="${notas}SIN-EMPUJAR "
         fi ;;
    esac
    [ -n "${WT_DE[$r]:-}" ] && notas="${notas}wt:${WT_DE[$r]} "
    [ "$F" -gt 60 ] && notas="${notas}DIFF-GRANDE "
    printf '%-52s %6s %7s %6s  %s\n' "$r" "$A" "$B" "$F" "$notas"
  done
  echo
  gris
  echo "SIN-EMPUJAR: el trabajo solo existe en este disco (CLAUDE.md § 21)."
  echo "DIFF-GRANDE: >60 ficheros. Comprueba que sean tuyos y no de una base vieja."
  fin
fi

# ── 3. El detector que habría cazado #228/#229/#230 ───────────────────────────
# Dos líneas vivas que comparten commits propios significan que una se cortó de
# la otra. En cuanto la primera entre en main por squash, la segunda arrastrará
# contenido ya integrado con SHA distinto y su diff se volverá ilegible.
echo
echo "════ 3. RAMAS CORTADAS DE OTRA RAMA ════"
hallazgos=0
total=${#VIVAS[@]}
for ((i=0; i<total; i++)); do
  for ((j=i+1; j<total; j++)); do
    a="${VIVAS[$i]}"; b="${VIVAS[$j]}"
    comunes=$(comm -12 \
      <(git rev-list "$BASE..$a" | sort) \
      <(git rev-list "$BASE..$b" | sort) | wc -l | tr -d ' ')
    if [ "$comunes" -gt 0 ]; then
      rojo; echo "AVISO  '$a' y '$b' comparten $comunes commit(s) fuera de main."; fin
      echo "       Una se cortó de la otra. Si la primera entra por squash, la"
      echo "       segunda traerá contenido ya integrado con SHA distinto."
      echo "       Recorta la de después desde origin/main en cuanto aquella mergee."
      hallazgos=$((hallazgos+1))
    fi
  done
done
if [ $hallazgos -eq 0 ]; then
  verde; echo "OK  ninguna línea viva se cortó de otra."; fin
fi

# ── 4. Ficheros que se disputan dos líneas ────────────────────────────────────
# Se comparan TODAS las líneas vivas, también las remotas: el solape que más
# cuesta es el de tu rama local contra un PR que ya está abierto.
echo
echo "════ 4. FICHEROS DISPUTADOS ════"
TMP=$(mktemp -d); trap 'rm -rf "$TMP"' EXIT
: > "$TMP/todos"
for r in "${VIVAS[@]}"; do
  git diff --name-only "$BASE...$r" 2>/dev/null | sed "s|^|$r\t|" >> "$TMP/todos"
done
if [ -s "$TMP/todos" ]; then
  cut -f2 "$TMP/todos" | sort | uniq -d > "$TMP/dup"
  if [ -s "$TMP/dup" ]; then
    while read -r f; do
      ambar; printf '%s\n' "$f"; fin
      awk -F'\t' -v f="$f" '$2==f{print "    <- " $1}' "$TMP/todos"
    done < "$TMP/dup"
    echo
    gris
    echo "Dos líneas vivas editan estos ficheros. Habla con la otra sesión antes"
    echo "de seguir, o te espera un conflicto o un retrabajo."
    fin
  else
    verde; echo "OK  ninguna línea viva pisa ficheros de otra."; fin
  fi
else
  echo "(no hay líneas vivas que comparar)"
fi

# ── 5. PRs abiertas ───────────────────────────────────────────────────────────
echo
echo "════ 5. PRs ABIERTAS ════"
if command -v gh >/dev/null 2>&1 && gh auth status >/dev/null 2>&1; then
  gh pr list --state open --limit 100 \
    --json number,title,headRefName,isDraft \
    --template '{{range .}}#{{.number}}  {{.headRefName}}{{if .isDraft}} (borrador){{end}}
    {{.title}}
{{end}}' 2>/dev/null || echo "(no se pudieron listar)"
else
  echo "(gh no disponible o sin autenticar)"
fi
echo
