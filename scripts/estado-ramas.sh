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
# COSTE (medido el 2026-09-17, 299 ramas locales y 53 remotas). La primera
# versión llamaba a git una vez por ref en el inventario, dos más por línea
# viva, y en la sección 3 dos veces por CADA PAR de ramas. A ~1,9 s por
# `rev-list` en Windows dejó de terminar: 904 s sin pasar de la sección 1. Como
# es la primera orden obligatoria de toda sesión que toca código, bloqueaba
# cada arranque.
#
# Esta versión hace un número de llamadas a git que NO crece con el número de
# ramas: una para el inventario (con ahead/behind), una para los worktrees y
# una para el grafo de commits fuera de origin/main con sus ficheros. Lo demás
# —intersecciones, ficheros por rama, ramas contenidas en otras— se calcula en
# memoria sobre esa única pasada.
#
# Requiere git >= 2.41 por %(ahead-behind:).
#
# Uso:  bash scripts/estado-ramas.sh [--sin-fetch] [--todas]
#       --sin-fetch  no actualiza referencias remotas (el informe puede estar viejo)
#       --todas      lista también las ramas sin cambios de contenido sobre origin/main

set -uo pipefail

SIN_FETCH=0
TODAS=0
for arg in "$@"; do
  case "$arg" in
    --sin-fetch) SIN_FETCH=1 ;;
    --todas) TODAS=1 ;;
    -h|--help) sed -n '31,34p' "$0"; exit 0 ;;
    *) echo "Opción desconocida: $arg" >&2; exit 2 ;;
  esac
done

hay_color=0
[ -t 1 ] && hay_color=1
c() { [ $hay_color -eq 1 ] && printf '\033[%sm' "$1" || true; }
rojo() { c 31; }; verde() { c 32; }; ambar() { c 33; }; gris() { c 90; }; fin() { c 0; }
# Las mismas secuencias como variables, para pasarlas a awk sin abrir un
# subshell por cada una (aquí cuesta ~1,1 s cada uno).
AMB=""; OFF=""
if [ $hay_color -eq 1 ]; then printf -v AMB '\033[33m'; printf -v OFF '\033[0m'; fi

# %(ahead-behind:) es de git 2.41. Sin él el inventario volvería a costar una
# invocación por ref: se para antes de prometer un informe que no llegaría.
if ! git for-each-ref --count=1 --format='%(ahead-behind:HEAD)' refs/heads >/dev/null 2>&1; then
  echo "Este guion necesita git >= 2.41: %(ahead-behind:) no está disponible." >&2
  echo "Versión encontrada: $(git --version)" >&2
  exit 1
fi

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

TMP=$(mktemp -d); trap 'rm -rf "$TMP"' EXIT

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
#
# Todo el inventario sale de UNA llamada: nombre, punta, upstream y el par
# ahead/behind contra origin/main.
#
# El separador es 0x1F (unit separator), no un tabulador: `read` con IFS=tab
# colapsa tabuladores consecutivos —tab es whitespace— y una rama SIN upstream
# perdía el campo siguiente, el de ahead/behind. Medido el 2026-09-17 contra el
# repositorio de control: dos ramas desaparecían del informe y la sección 3
# daba un OK sin haber comprobado nada. git prohíbe los caracteres de control
# en los nombres de ref, así que 0x1F no puede aparecer dentro de un campo.
git for-each-ref \
  --format='%(refname)%1f%(objectname)%1f%(upstream:short)%1f%(ahead-behind:'"$BASE"')%1f%(upstream:track,nobracket)%1f%(committerdate:unix)' \
  refs/heads refs/remotes/origin > "$TMP/refs"

declare -A PUNTA_DE AHEAD_DE BEHIND_DE UP_DE NOMBRE_POR_SHA PUBLICADA WT_DE
declare -A SHA_REMOTA REMOTA_POR_SHA MUERTA FECHA_DE EXCLUIDA
VIVAS=()
N_EXCLUIDAS=0

# Pasada 1: punta de cada rama remota, para responder "¿está publicada?" sin
# preguntárselo a git una vez por rama.
while IFS=$'\x1f' read -r refname sha _up _ab _tr _fe; do
  case "$refname" in
    refs/remotes/origin/*)
      SHA_REMOTA["${refname#refs/remotes/}"]="$sha"
      # Si varias remotas comparten punta, se queda la primera alfabéticamente,
      # que es el orden en que for-each-ref las entrega.
      [ -z "${REMOTA_POR_SHA[$sha]:-}" ] && REMOTA_POR_SHA["$sha"]="${refname#refs/remotes/}"
      ;;
  esac
done < "$TMP/refs"

# Pasada 2: el inventario propiamente dicho.
while IFS=$'\x1f' read -r refname sha up ab track fecha; do
  case "$refname" in
    refs/heads/*)          ref="${refname#refs/heads/}" ;;
    refs/remotes/origin/*) ref="origin/${refname#refs/remotes/origin/}" ;;
    *) continue ;;
  esac
  case "$ref" in
    main|origin|origin/main|origin/staging|staging) continue ;;
    origin/gh-readonly-queue/*) continue ;;
  esac
  ahead="${ab%% *}"; behind="${ab##* }"
  # Un campo vacío aquí no es "esta rama no está adelantada": es que el informe
  # no pudo leerse. Descartarlo en silencio fue un fallo real de esta misma
  # reescritura (§ 3: un resultado vacío no es una ausencia).
  case "$ahead" in
    ''|*[!0-9]*)
      echo "ERROR  ahead/behind ilegible para '$ref': '$ab'" >&2
      echo "       El inventario estaría incompleto; no se sigue." >&2
      exit 1 ;;
  esac
  [ "$ahead" -eq 0 ] && continue

  anterior="${NOMBRE_POR_SHA[$sha]:-}"
  if [ -n "$anterior" ]; then
    case "$ref" in
      origin/*) PUBLICADA["$anterior"]=1; continue ;;
    esac
    # Llega la local y ya teníamos la remota: sustituimos el nombre. Se marca
    # como excluida en vez de reconstruir el array entero, que con 279 ramas
    # era un bucle cuadrático dentro de otro bucle.
    EXCLUIDA["$anterior"]=1
    N_EXCLUIDAS=$((N_EXCLUIDAS+1))
    unset "PUNTA_DE[$anterior]"
    PUBLICADA["$ref"]=1
  fi
  NOMBRE_POR_SHA[$sha]="$ref"
  VIVAS+=("$ref")
  PUNTA_DE["$ref"]=$sha
  AHEAD_DE["$ref"]=$ahead
  BEHIND_DE["$ref"]=$behind
  UP_DE["$ref"]=$up
  FECHA_DE["$ref"]=$fecha
  # GitHub borra la rama remota al mergear su PR. Una rama LOCAL cuyo upstream
  # existía y ha desaparecido es, por tanto, trabajo ya integrado: no hay
  # ninguna sesión detrás con la que chocar. Aquí eran 163 de 279, y listarlas
  # enterraba las líneas de trabajo de verdad. Se omiten de la tabla y de los
  # ficheros disputados, pero NO de la sección 3: una rama así es justamente la
  # madre posible de otra que se cortó de ella (el caso #228), que es lo que
  # este guion existe para cazar.
  [ "$track" = "gone" ] && MUERTA["$ref"]=1
done < "$TMP/refs"

# Compacta VIVAS quitando las sustituidas por su nombre local. Se lleva la
# cuenta aparte porque `${#array[@]}` sobre un array asociativo vacío aborta
# con `set -u` en las versiones de bash donde eso sigue contando como variable
# sin asignar (observado aquí contra el repositorio de control).
if [ "$N_EXCLUIDAS" -gt 0 ]; then
  nuevas=()
  for r in "${VIVAS[@]}"; do [ -z "${EXCLUIDA[$r]:-}" ] && nuevas+=("$r"); done
  VIVAS=("${nuevas[@]}")
fi

# Worktree que ocupa cada rama: dice si hay una sesión sentada encima.
#
# `basename` iba en un subshell por worktree. Con 75 worktrees registrados y
# ~1,1 s por subshell en este entorno (medido), eran ~82 s de los 123 que
# costaba el guion: más que todas las llamadas a git juntas. La expansión de
# bash da lo mismo sin crear un proceso.
WT_ACT=""
while read -r linea; do
  case "$linea" in
    worktree\ *) WT_ACT="${linea#worktree }"; WT_ACT="${WT_ACT%/}" ;;
    branch\ *) WT_DE["${linea#branch refs/heads/}"]="${WT_ACT##*/}" ;;
  esac
done < <(git worktree list --porcelain)

# ── Una sola pasada del grafo responde a las secciones 2, 3 y 4 ───────────────
# Lo que queda por saber —qué commits propios tiene cada línea, cuáles comparte
# con otra, qué ficheros toca y si su punta ya está en otra rama— se deduce del
# mismo grafo: los commits que quedan fuera de origin/main, con sus padres y
# sus ficheros. Una llamada, no una por rama ni una por par.
#
# Por qué importa, medido aquí con 233 líneas vivas: un
# `git diff --name-only origin/main...rama` por rama obliga a calcular un
# merge-base cada vez y tardaba unos 10 minutos incluso con 8 en paralelo; el
# `for-each-ref --contains` por rama costaba otro tanto; y la sección 3, con
# dos `rev-list` por par, son ~27.000 invocaciones.
#
# Diferencia declarada respecto de la versión anterior: los ficheros de una
# línea son ahora la UNIÓN de los que tocan sus commits, no el diff neto contra
# el merge-base. Un fichero creado y borrado dentro de la misma rama, o un
# cambio revertido, antes no aparecía y ahora sí. Para la pregunta que hace la
# sección 4 —¿voy a chocar con otra sesión en este fichero?— la unión es la
# respuesta conservadora: sobra un aviso antes que faltar.
declare -A NFICH CONTENIDA_EN
TOTAL=${#VIVAS[@]}
if [ "$TOTAL" -gt 0 ]; then
  for ((i=0; i<TOTAL; i++)); do
    printf '%s\t%s\n' "$i" "${PUNTA_DE[${VIVAS[$i]}]}"
  done > "$TMP/puntas"

  { echo "^$BASE"; cut -f2 "$TMP/puntas"; } \
    | git -c core.quotePath=false log --stdin --format='%x01%H %P' --name-only \
      > "$TMP/grafo" 2>/dev/null

  # Un grafo vacío habiendo líneas vivas declaradas significa que el log falló:
  # sin esta guarda, las secciones 3 y 4 darían un OK sin haber comprobado nada
  # (§ 3: un resultado vacío no es una ausencia).
  if [ ! -s "$TMP/grafo" ]; then
    echo "ERROR  no se pudo obtener el grafo de commits fuera de $BASE." >&2
    echo "       Las secciones 2, 3 y 4 no habrían comprobado nada." >&2
    exit 1
  fi

  awk '
    FILENAME==ARGV[1] {
      if (substr($0,1,1) == "\001") {
        s=substr($0,2); n=split(s, t, " "); cur=t[1]; p=""
        for (i=2; i<=n; i++) p = p " " t[i]
        PAD[cur]=p
        next
      }
      if ($0 == "") next
      if (cur != "") FI[cur] = FI[cur] "\n" $0
      next
    }
    {
      idx=$1; sha=$2
      delete vis; delete fs
      n=1; pila[1]=sha
      while (n > 0) {
        c=pila[n]; n--
        if (c in vis) continue
        if (!(c in PAD)) continue          # ya está en origin/main
        vis[c]=1
        DU[c] = (c in DU) ? DU[c] " " idx : idx
        if (c in FI) {
          k=split(FI[c], ff, "\n")
          for (q=1; q<=k; q++) if (ff[q] != "") fs[ff[q]]=1
        }
        k=split(PAD[c], ps, " ")
        for (q=1; q<=k; q++) { n++; pila[n]=ps[q] }
      }
      for (f in fs) print "F", idx, f
      IDX_DE_PUNTA[sha]=idx
    }
    END {
      # Pares que comparten commits propios: una se cortó de la otra.
      for (co in DU) {
        k=split(DU[co], d, " ")
        if (k < 2) continue
        for (i=1; i<k; i++) for (j=i+1; j<=k; j++) CNT[d[i] "," d[j]]++
      }
      for (par in CNT) { split(par, ij, ","); print "P", ij[1], ij[2], CNT[par] }
      # Puntas contenidas en OTRA línea viva: así se sabe si una rama local ya
      # está publicada en el remoto, aunque sea bajo otro nombre.
      for (s in IDX_DE_PUNTA) {
        if (!(s in DU)) continue
        k=split(DU[s], d, " ")
        for (i=1; i<=k; i++) if (d[i] != IDX_DE_PUNTA[s]) print "C", IDX_DE_PUNTA[s], d[i]
      }
    }
  ' "$TMP/grafo" FS='\t' "$TMP/puntas" > "$TMP/analisis"

  sed -n 's/^F //p' "$TMP/analisis" > "$TMP/ficheros"
  sed -n 's/^P //p' "$TMP/analisis" | sort -n -k1,1 -k2,2 > "$TMP/pares"
  sed -n 's/^C //p' "$TMP/analisis" > "$TMP/contenidas"

  # Conteo por rama en UNA pasada de awk: hacerlo con un `read` por línea eran
  # 2.508 iteraciones de bash, y cada una cuesta.
  while read -r i n; do NFICH[$i]=$n; done < <(awk '{c[$1]++} END{for(i in c) print i, c[i]}' "$TMP/ficheros")

  while read -r i j; do
    otra="${VIVAS[$j]}"
    case "$otra" in
      origin/*) nombre="$otra" ;;
      *) nombre="${REMOTA_POR_SHA[${PUNTA_DE[$otra]}]:-}" ;;
    esac
    [ -z "$nombre" ] && continue
    actual="${CONTENIDA_EN[$i]:-}"
    if [ -z "$actual" ] || [[ "$nombre" < "$actual" ]]; then
      CONTENIDA_EN[$i]="$nombre"
    fi
  done < "$TMP/contenidas"
fi

# ── 2. Líneas vivas ───────────────────────────────────────────────────────────
echo
echo "════ 2. LÍNEAS DE TRABAJO VIVAS ════"
if [ "$TOTAL" -eq 0 ]; then
  echo "(ninguna: todo lo que existe está en origin/main)"
else
  OMITIDAS=0
  FILAS="$TMP/filas"; : > "$FILAS"
  for ((i=0; i<TOTAL; i++)); do
    r="${VIVAS[$i]}"
    A="${AHEAD_DE[$r]}"
    B="${BEHIND_DE[$r]}"
    F="${NFICH[$i]:-0}"
    notas=""
    case "$r" in
      origin/*) ;;
      *) # "Sin upstream" NO significa "sin publicar": el mismo commit puede estar
         # ya en el remoto bajo OTRO nombre de rama (p. ej. como cabeza de un PR
         # de dependabot). Falso positivo real, 2026-08-29: la rama local
         # dep-htmlsanitizer se marco SIN-EMPUJAR cuando su punta ya era la
         # cabeza del PR #333. La identidad de una rama no distingue "sin
         # publicar" de "publicado con otro nombre" — se pregunta por el commit.
         #
         # Primero la respuesta barata: ¿origin/<misma rama> apunta ya a esta
         # punta? Sale del inventario. Si no, se mira si alguna otra línea viva
         # la contiene, que ya lo calculó la pasada del grafo. De paso corrige
         # un falso positivo de la versión anterior: tomaba `head -1` de TODAS
         # las remotas que contuvieran la punta, así que una rama correctamente
         # publicada podía salir marcada "publicada-como:<otra>" solo por orden
         # alfabético.
         if [ "${SHA_REMOTA[origin/$r]:-}" = "${PUNTA_DE[$r]}" ]; then
           :
         elif [ -n "${REMOTA_POR_SHA[${PUNTA_DE[$r]}]:-}" ]; then
           # Misma punta exacta que una remota de otro nombre. No sale de la
           # pasada del grafo: al compartir punta, el inventario las trata como
           # una sola línea y la remota no llega a ser una línea viva aparte.
           notas="${notas}publicada-como:${REMOTA_POR_SHA[${PUNTA_DE[$r]}]#origin/} "
         elif [ -n "${CONTENIDA_EN[$i]:-}" ]; then
           case "${CONTENIDA_EN[$i]}" in
             "origin/$r") ;;
             *) notas="${notas}publicada-como:${CONTENIDA_EN[$i]#origin/} " ;;
           esac
         elif [ -z "${PUBLICADA[$r]:-}" ] && [ -z "${UP_DE[$r]}" ]; then
           notas="${notas}SIN-EMPUJAR "
         fi ;;
    esac
    [ -n "${WT_DE[$r]:-}" ] && notas="${notas}wt:${WT_DE[$r]} "
    [ "$F" -gt 60 ] && notas="${notas}DIFF-GRANDE "
    [ -n "${MUERTA[$r]:-}" ] && notas="${notas}MERGEADA "
    # Una rama cuyo remoto borró GitHub al mergear, o cuyos commits no tocan
    # ningún fichero, no es una línea de trabajo con la que nadie vaya a
    # chocar. Un worktree encima manda sobre ambas cosas: ahí hay una sesión.
    if [ $TODAS -eq 0 ] && [ -z "${WT_DE[$r]:-}" ] \
       && { [ -n "${MUERTA[$r]:-}" ] || [ "$F" -eq 0 ]; }; then
      OMITIDAS=$((OMITIDAS+1))
      continue
    fi
    # La fecha encabeza la línea solo para ordenar; se recorta después.
    printf '%s\t%-52s %6s %7s %6s  %s\n' \
      "${FECHA_DE[$r]:-0}" "$r" "$A" "$B" "$F" "$notas" >> "$FILAS"
  done
  if [ -s "$FILAS" ]; then
    printf '%-52s %6s %7s %6s  %s\n' "RAMA" "AHEAD" "BEHIND" "FICH." "ESTADO"
    # Lo más reciente arriba: con decenas de líneas vivas, el orden alfabético
    # deja la rama en la que alguien está trabajando ahora en mitad del muro.
    sort -rn -k1,1 "$FILAS" | cut -f2-
  else
    echo "(ninguna línea viva con cambios de contenido sobre origin/main)"
  fi
  echo
  gris
  if [ "$OMITIDAS" -gt 0 ]; then
    echo "$OMITIDAS rama(s) ya mergeadas (su rama remota fue borrada) o sin"
    echo "             ficheros propios: omitidas de la tabla y de la sección 4."
    echo "             Siguen contando en la sección 3. Con --todas se listan."
  fi
  echo "MERGEADA: su upstream ya no existe; GitHub lo borró al mergear el PR."
  echo "SIN-EMPUJAR: el trabajo solo existe en este disco (CLAUDE.md § 21)."
  echo "             Se comprueba por commit, no por upstream: una rama puede"
  echo "             estar publicada en el remoto bajo otro nombre."
  echo "publicada-como: su punta ya esta en el remoto, con otro nombre de rama."
  echo "DIFF-GRANDE: >60 ficheros. Comprueba que sean tuyos y no de una base vieja."
  fin
fi

# ── 3. El detector que habría cazado #228/#229/#230 ───────────────────────────
# Dos líneas vivas que comparten commits propios significan que una se cortó de
# la otra. En cuanto la primera entre en main por squash, la segunda arrastrará
# contenido ya integrado con SHA distinto y su diff se volverá ilegible.
#
# El resultado es el mismo conjunto de pares, con el mismo recuento de commits,
# que daba la comparación par a par: la intersección se calcula en memoria
# sobre el grafo ya leído.
echo
echo "════ 3. RAMAS CORTADAS DE OTRA RAMA ════"
hallazgos=0
if [ "$TOTAL" -gt 1 ] && [ -s "$TMP/pares" ]; then
  while read -r i j comunes; do
    [ -z "$i" ] && continue
    a="${VIVAS[$i]}"; b="${VIVAS[$j]}"
    rojo; echo "AVISO  '$a' y '$b' comparten $comunes commit(s) fuera de main."; fin
    echo "       Una se cortó de la otra. Si la primera entra por squash, la"
    echo "       segunda traerá contenido ya integrado con SHA distinto."
    echo "       Recorta la de después desde origin/main en cuanto aquella mergee."
    hallazgos=$((hallazgos+1))
  done < "$TMP/pares"
fi
if [ $hallazgos -eq 0 ]; then
  verde; echo "OK  ninguna línea viva se cortó de otra."; fin
fi

# ── 4. Ficheros que se disputan dos líneas ────────────────────────────────────
# Se comparan TODAS las líneas vivas, también las remotas: el solape que más
# cuesta es el de tu rama local contra un PR que ya está abierto.
echo
echo "════ 4. FICHEROS DISPUTADOS ════"
if [ "$TOTAL" -gt 0 ] && [ -s "$TMP/ficheros" ]; then
  # Nombres de las líneas que cuentan aquí: las mergeadas no tienen sesión
  # detrás, así que su solape no es un aviso, es ruido (aquí, 539 ficheros
  # "disputados" entre ramas ya integradas).
  for ((i=0; i<TOTAL; i++)); do
    r="${VIVAS[$i]}"
    if [ $TODAS -eq 0 ] && [ -n "${MUERTA[$r]:-}" ] && [ -z "${WT_DE[$r]:-}" ]; then
      printf '\n'
    else
      printf '%s\n' "$r"
    fi
  done > "$TMP/nombres"
  awk -v OFS='\t' 'FILENAME==ARGV[1] { nombre[FNR-1]=$0; next }
                   { i=$1; sub(/^[0-9]+ /, ""); if (nombre[i] != "") print nombre[i], $0 }' \
    "$TMP/nombres" "$TMP/ficheros" > "$TMP/todos"
  cut -f2 "$TMP/todos" | sort | uniq -d > "$TMP/dup"
  if [ -s "$TMP/dup" ]; then
    # Un solo awk para todos los ficheros: invocarlo una vez por fichero
    # disputado eran 539 procesos y ~730 s del total medido.
    awk -F'\t' -v amb="$AMB" -v off="$OFF" '
      FILENAME==ARGV[1] { dup[$0]=1; orden[++k]=$0; next }   # ya viene ordenado
      $2 in dup { linea[$2] = linea[$2] "    <- " $1 "\n" }
      END { for (i=1; i<=k; i++) { f=orden[i]; printf "%s%s%s\n%s", amb, f, off, linea[f] } }
    ' "$TMP/dup" "$TMP/todos"
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
