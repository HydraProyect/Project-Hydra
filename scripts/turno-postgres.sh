#!/usr/bin/env bash
# scripts/turno-postgres.sh — turno de uso del clúster PostgreSQL local compartido.
#
# Uso:
#   bash scripts/turno-postgres.sh [--espera-max-min N | --espera-max-s N] [--etiqueta TXT] \
#        -- COMANDO [ARGS...]
#
# Por qué existe. Todos los worktrees de esta máquina comparten UN clúster de
# PostgreSQL, y los roles son objetos de clúster: dos suites de integración/E2E
# (o una mutación de rol) a la vez se contaminan y fallan de forma intermitente
# en la sesión vecina. La protección anterior era un bucle «espera a que no haya
# ningún testhost» escrito a mano en el scratchpad de cada sesión. Medido el
# 2026-09-20 sobre ese patrón: tras ~10 min imprimía `testhost=3`, SALTABA el
# test en silencio y terminaba con el código del último comando de limpieza (0).
# Una mutación abortada y una mutación que no encuentra nada se parecían.
#
# Qué hace. Encola al que llega (orden de llegada), le deja pasar cuando el
# anterior termina, ejecuta COMANDO con el cerrojo tomado y sale con EL CÓDIGO
# DE COMANDO. Toda invocación termina con UNA línea final parseable:
#
#   TURNO-POSTGRES: EJECUTADO salida=<n> esperado_s=<s>
#   TURNO-POSTGRES: ABORTADO_SIN_EJECUTAR motivo=<...> salida=<75|74> ...
#   TURNO-POSTGRES: INTERRUMPIDO señal=<n> salida=<128+n>
#
# Códigos de salida PROPIOS, reservados (no los emite dotnet/vstest):
#   75  se agotó la espera sin obtener turno: el comando NO se ejecutó. No hay
#       resultado de test, ni verde ni rojo, y no debe leerse como ninguno.
#   74  no se pudo crear el directorio de turno: el comando NO se ejecutó.
#   64  uso incorrecto.
# Cualquier otro código es el del propio COMANDO; la línea final dice si de
# verdad se ejecutó (un COMANDO que saliera con 75 se distingue por «EJECUTADO»).
#
# Mecanismo.
#   · Seguridad (exclusión mutua): `mkdir` atómico de $DIR/cerrojo. Quien lo crea
#     escribe $DIR/cerrojo/dueno (pid, worktree, comando, inicio) y mantiene
#     $DIR/cerrojo/latido (época) con un latido en segundo plano que muere con
#     el proceso dueño.
#   · Orden (equidad): tickets $DIR/cola/<ns>-<pid>, refrescados en cada sondeo.
#     Solo el primero vivo intenta el cerrojo. El orden es de mejor esfuerzo; la
#     exclusión NO depende de él: si dos creyeran ser primeros, `mkdir` decide.
#   · Huérfanos: un cerrojo se retira si su pid ya no existe (`kill -0`), o si su
#     latido lleva más de CADUCIDAD sin refrescarse (confirmado en dos sondeos
#     seguidos, para no retirar el de un dueño vivo tras una suspensión). Solo
#     retira el primero de la cola, por rename atómico. Un ticket cuyo proceso
#     murió, o cuyo latido caducó, se descarta. El latido del ticket es el
#     sondeo (se refresca en cada vuelta de la espera), así que CADUCIDAD debe
#     ser >= 3 x SONDEO además de >= 3 x LATIDO; si no, la cola descartaría a un
#     waiter vivo entre dos sondeos y el siguiente le adelantaría.
#   · Suspensión: la edad de un latido ajeno solo cuenta el tiempo que quien lo
#     juzga ha presenciado despierto (desde su arranque o su último despertar).
#     Una vuelta de la espera más larga que CADUCIDAD/2 se toma por suspensión:
#     no gasta la espera máxima y reinicia lo presenciado (un sondeo normal no
#     llega a tanto: CADUCIDAD >= 3 x SONDEO). Así, al despertar el
#     equipo, nadie descarta el ticket de un waiter vivo ni retira el cerrojo de
#     un dueño vivo antes de que hayan tenido ocasión de latir.
#
# Límites conocidos:
#   · Solo excluye a quien también pase por aquí: una `dotnet test` lanzada a mano
#     contra el clúster no ve este cerrojo.
#   · La protección frente a la suspensión solo vale para quien ejecute esta
#     versión: una copia anterior del guion (otra rama) sigue juzgando los
#     latidos por reloj de pared al despertar.
#   · Si el guion muere a la fuerza (sin ejecutar traps) el COMANDO hijo puede
#     seguir vivo un rato tras liberarse el cerrojo.
#
# Entorno (todos opcionales; los tests los reducen para ir rápido):
#   HYDRA_TURNO_DIR            directorio compartido (por defecto %LOCALAPPDATA%/Hydra/turno-postgres)
#   HYDRA_TURNO_ESPERA_MAX_MIN espera máxima por defecto, en minutos (120)
#   HYDRA_TURNO_SONDEO_S       segundos entre sondeos (5)
#   HYDRA_TURNO_LATIDO_S       segundos entre latidos del dueño (15)
#   HYDRA_TURNO_CADUCIDAD_S    latido más viejo que esto = dueño caducado (120)
#   HYDRA_TURNO_AVISO_S        cada cuántos segundos avisa mientras espera (60)

set -uo pipefail
export LC_ALL=C

EX_USO=64
EX_INFRA=74
EX_SIN_TURNO=75

uso() {
  sed -n '2,9p' "${BASH_SOURCE[0]}" >&2
  echo "TURNO-POSTGRES: ABORTADO_SIN_EJECUTAR motivo=uso salida=$EX_USO" >&2
}

ruta_unix() {
  if command -v cygpath >/dev/null 2>&1; then cygpath -u "$1" 2>/dev/null || printf '%s' "$1"
  else printf '%s' "$1"; fi
}

if [ -n "${HYDRA_TURNO_DIR:-}" ]; then
  DIR=$(ruta_unix "$HYDRA_TURNO_DIR")
elif [ -n "${LOCALAPPDATA:-}" ]; then
  DIR="$(ruta_unix "$LOCALAPPDATA")/Hydra/turno-postgres"
else
  DIR="${HOME:-/tmp}/.hydra/turno-postgres"
fi
SONDEO_S=${HYDRA_TURNO_SONDEO_S:-5}
LATIDO_S=${HYDRA_TURNO_LATIDO_S:-15}
CADUCIDAD_S=${HYDRA_TURNO_CADUCIDAD_S:-120}
AVISO_S=${HYDRA_TURNO_AVISO_S:-60}
espera_max_min=${HYDRA_TURNO_ESPERA_MAX_MIN:-120}
espera_max_s=""
etiqueta=""

while [ $# -gt 0 ]; do
  case "$1" in
    --espera-max-min) [ $# -ge 2 ] || { uso; exit $EX_USO; }; espera_max_min=$2; espera_max_s=""; shift 2 ;;
    --espera-max-s)   [ $# -ge 2 ] || { uso; exit $EX_USO; }; espera_max_s=$2; shift 2 ;;
    --etiqueta)       [ $# -ge 2 ] || { uso; exit $EX_USO; }; etiqueta=$2; shift 2 ;;
    -h|--help)        uso; exit $EX_USO ;;
    --)               shift; break ;;
    *)                uso; exit $EX_USO ;;
  esac
done

# Validación ESTRICTA de todo lo numérico, en un solo sitio y antes de encolar.
# Bash y `test` solo entienden enteros: un «120junk» o un «1e2» que pasara una
# comprobación laxa reventaba luego la aritmética (error, no falso), con lo que la
# espera quedaba sin tope o la retirada de huérfanos nunca ocurría. Se rechaza al
# arrancar, sin ejecutar nada, en vez de degradar la garantía en silencio.
config_invalida() {
  echo "TURNO-POSTGRES: configuración inválida: $1" >&2
  echo "TURNO-POSTGRES: ABORTADO_SIN_EJECUTAR motivo=configuracion_invalida salida=$EX_USO" >&2
  exit $EX_USO
}
es_entero()  { [[ "${1:-}" =~ ^[0-9]+$ ]]; }
es_decimal() { [[ "${1:-}" =~ ^[0-9]+([.][0-9]+)?$ ]]; }
es_entero "$espera_max_min" || config_invalida "espera máxima en minutos '$espera_max_min': debe ser un entero >= 0."
[ -n "$espera_max_s" ] || espera_max_s=$(( 10#$espera_max_min * 60 ))
es_entero "$espera_max_s"   || config_invalida "espera máxima en segundos '$espera_max_s': debe ser un entero >= 0."
es_entero "$CADUCIDAD_S"    || config_invalida "HYDRA_TURNO_CADUCIDAD_S '$CADUCIDAD_S': debe ser un entero de segundos."
es_entero "$AVISO_S"        || config_invalida "HYDRA_TURNO_AVISO_S '$AVISO_S': debe ser un entero de segundos."
es_decimal "$LATIDO_S"      || config_invalida "HYDRA_TURNO_LATIDO_S '$LATIDO_S': debe ser un número decimal simple (sin exponente ni sufijos)."
es_decimal "$SONDEO_S"      || config_invalida "HYDRA_TURNO_SONDEO_S '$SONDEO_S': debe ser un número decimal simple (sin exponente ni sufijos)."
CADUCIDAD_S=$(( 10#$CADUCIDAD_S )); AVISO_S=$(( 10#$AVISO_S )); espera_max_s=$(( 10#$espera_max_s ))
# La exclusión depende de que un dueño vivo refresque su latido mucho antes de que
# otro lo dé por caducado: con CADUCIDAD <= LATIDO un dueño legítimo perdería el
# cerrojo y dos suites correrían a la vez.
if ! awk -v c="$CADUCIDAD_S" -v l="$LATIDO_S" -v s="$SONDEO_S" 'BEGIN { exit !(c > 0 && l > 0 && s > 0 && c >= 3 * l) }'; then
  config_invalida "HYDRA_TURNO_CADUCIDAD_S ($CADUCIDAD_S) debe ser >= 3 x HYDRA_TURNO_LATIDO_S ($LATIDO_S), y latido, sondeo y caducidad deben ser positivos."
fi
# Lo mismo vale para el ticket de quien espera, pero su latido es el SONDEO: solo
# se refresca una vez por vuelta del bucle de espera. Con CADUCIDAD < 3 x SONDEO,
# otro waiter borra el ticket de B (vivo) mientras B duerme entre sondeos, y el
# siguiente en llegar le adelanta: la cola pierde a B.
if ! awk -v c="$CADUCIDAD_S" -v s="$SONDEO_S" 'BEGIN { exit !(c >= 3 * s) }'; then
  config_invalida "HYDRA_TURNO_CADUCIDAD_S ($CADUCIDAD_S) debe ser >= 3 x HYDRA_TURNO_SONDEO_S ($SONDEO_S): el ticket de quien espera solo se refresca en cada sondeo."
fi
[ $# -ge 1 ] || { uso; exit $EX_USO; }

CERROJO="$DIR/cerrojo"
COLA="$DIR/cola"
YO=$$
TICKET=""
HIJO=""
LATIDO_PID=""
TENGO_CERROJO=0
INICIO=$SECONDS
# Lo que ESTE proceso ha presenciado despierto. Una suspensión del equipo congela a
# todos a la vez y, al despertar, todos los latidos parecen caducados por reloj de
# pared aunque sus dueños sigan vivos: el primero en despertar descartaba el ticket
# de B y retiraba el cerrojo de un dueño vivo. Por eso la edad de un latido ajeno
# se cuenta como mucho desde PRESENCIA_DESDE (el arranque de este proceso o su
# último despertar), nunca desde un tiempo que no ha visto pasar. HUECO_S: una vuelta
# de la espera que tarda más que esto es una suspensión o una parada, no un sondeo.
# Con CADUCIDAD >= 3 x SONDEO y >= 3 x LATIDO, un hueco <= CADUCIDAD/2 no puede
# hacer caducar un latido vivo, así que basta con detectar los mayores.
PRESENCIA_DESDE=$(date +%s)
HUECO_S=$(( CADUCIDAD_S / 2 )); [ "$HUECO_S" -ge 2 ] || HUECO_S=2   # >= 2: `date +%s` redondea a segundos
SUSPENDIDO_S=0
ULTIMA_VISTA=$PRESENCIA_DESDE
INICIO_EP=$PRESENCIA_DESDE
sospecha=""

epoca() { date +%s; }
vivo()  { [ -n "${1:-}" ] && kill -0 "$1" 2>/dev/null; }
# leer FICHERO CLAVE -> valor de la primera línea «CLAVE=valor»
leer()  { sed -n "s/^$2=//p" "$1" 2>/dev/null | head -n 1; }
esperado_s() { echo $(( SECONDS - INICIO )); }   # reloj de pared; la espera máxima descuenta SUSPENDIDO_S
# edad_de AHORA T -> EDAD: segundos desde T, contados como mucho desde PRESENCIA_DESDE.
# Un T no numérico cuenta como PRESENCIA_DESDE (el `[` falla sin abortar el guion).
edad_de() { local t=$2; [ "$t" -gt "$PRESENCIA_DESDE" ] 2>/dev/null || t=$PRESENCIA_DESDE; EDAD=$(( $1 - t )); }
# presenciar -> AHORA_P. Se llama justo antes de cada juicio de edad y del control de
# la espera máxima: si desde la llamada anterior ha pasado más de HUECO_S, este
# proceso estuvo parado (suspensión del equipo o proceso congelado). Ese tiempo no
# se ha presenciado, no gasta la espera máxima y ningún latido ajeno se da por
# caducado hasta volver a observarlo. Un parón entre esta llamada y el juicio que
# la sigue no engaña: el juicio usa AHORA_P, anterior al parón.
presenciar() {
  AHORA_P=$(epoca)
  if [ $(( AHORA_P - ULTIMA_VISTA )) -gt "$HUECO_S" ]; then
    SUSPENDIDO_S=$(( SUSPENDIDO_S + AHORA_P - ULTIMA_VISTA )); PRESENCIA_DESDE=$AHORA_P; sospecha=""
    echo "TURNO-POSTGRES: la espera estuvo parada $(( AHORA_P - ULTIMA_VISTA ))s (¿suspensión del equipo?): no cuenta para la espera máxima y no se da por caducado ningún latido ajeno hasta volver a observarlo." >&2
  fi
  ULTIMA_VISTA=$AHORA_P
}

mkdir -p "$COLA" 2>/dev/null
if [ ! -d "$COLA" ]; then
  echo "TURNO-POSTGRES: ABORTADO — NO SE EJECUTÓ EL COMANDO: no se pudo crear '$COLA'." >&2
  echo "TURNO-POSTGRES: ABORTADO_SIN_EJECUTAR motivo=sin_directorio_de_turno salida=$EX_INFRA" >&2
  exit $EX_INFRA
fi

marca_ns() {
  local n; n=$(date +%s%N 2>/dev/null)
  case "$n" in *[!0-9]*|"") n="$(date +%s)000000000" ;; esac
  printf '%019d' "$n"
}

refrescar_ticket() {
  printf 'pid=%s\nlatido=%s\netiqueta=%s\n' "$YO" "$(epoca)" "$etiqueta" > "$TICKET.tmp" 2>/dev/null \
    && mv -f "$TICKET.tmp" "$TICKET" 2>/dev/null
}

# Descarta tickets de procesos muertos o sin latido reciente.
limpiar_cola() {
  local t pid lat ahora; presenciar; ahora=$AHORA_P
  for t in "$COLA"/*; do
    [ -f "$t" ] || continue
    case "$t" in *.tmp) continue ;; esac
    pid=$(leer "$t" pid); lat=$(leer "$t" latido)
    if [ -z "$pid" ] || [ -z "$lat" ]; then continue; fi   # a medio escribir: no decidir
    if ! vivo "$pid"; then rm -f "$t"; continue; fi
    edad_de "$ahora" "$lat"
    if [ "$EDAD" -gt "$CADUCIDAD_S" ]; then rm -f "$t"; fi
  done
}

primero_de_la_cola() {
  local t
  for t in "$COLA"/*; do
    [ -f "$t" ] || continue
    case "$t" in *.tmp) continue ;; esac
    basename "$t"; return 0
  done
}

posicion_en_cola() {
  local t n=0
  for t in "$COLA"/*; do
    [ -f "$t" ] || continue
    case "$t" in *.tmp) continue ;; esac
    n=$((n + 1))
    [ "$(basename "$t")" = "$(basename "$TICKET")" ] && { echo "$n"; return; }
  done
  echo "?"
}

# Devuelve 0 y fija MOTIVO/HUELLA si el cerrojo existente parece huérfano.
# CERTEZA=1: no hace falta confirmar (el pid ya no existe).
cerrojo_huerfano() {
  MOTIVO=""; HUELLA=""; CERTEZA=0
  local d="$CERROJO/dueno" pid lat ini ahora; presenciar; ahora=$AHORA_P
  if [ ! -f "$d" ]; then
    local mt; mt=$(stat -c %Y "$CERROJO" 2>/dev/null || echo "$ahora")
    edad_de "$ahora" "$mt"
    if [ "$EDAD" -gt "$CADUCIDAD_S" ]; then
      MOTIVO="cerrojo sin registro de dueño desde hace ${EDAD}s observados"; HUELLA="sin-dueno"; return 0
    fi
    return 1
  fi
  pid=$(leer "$d" pid); ini=$(leer "$d" inicio)
  [ -n "$pid" ] || return 1                                 # a medio escribir
  if ! vivo "$pid"; then
    MOTIVO="el pid dueño $pid ya no existe"; HUELLA="$pid:$ini"; CERTEZA=1; return 0
  fi
  lat=$(cat "$CERROJO/latido" 2>/dev/null)
  [ -n "$lat" ] || return 1
  edad_de "$ahora" "$lat"
  if [ "$EDAD" -gt "$CADUCIDAD_S" ]; then
    MOTIVO="el pid dueño $pid vive pero su latido lleva ${EDAD}s observados sin refrescarse"; HUELLA="$pid:$ini"; return 0
  fi
  return 1
}

retirar_cerrojo_huerfano() {
  # rename atómico: solo uno de dos retiradores concurrentes lo consigue.
  if mv "$CERROJO" "$CERROJO.roto.$YO" 2>/dev/null; then
    rm -rf "$CERROJO.roto.$YO"
    echo "TURNO-POSTGRES: cerrojo huérfano retirado ($1)." >&2
    return 0
  fi
  return 1
}

descripcion_dueno() {
  local d="$CERROJO/dueno"
  [ -f "$d" ] || { echo "desconocido"; return; }
  echo "pid $(leer "$d" pid), worktree $(leer "$d" worktree), desde hace $(( $(epoca) - $(leer "$d" inicio) ))s, «$(leer "$d" comando)»"
}

iniciar_latido() {
  (
    while kill -0 "$YO" 2>/dev/null; do
      sleep "$LATIDO_S"
      # Solo si el cerrojo sigue siendo mío: no pisar el de quien lo retiró.
      [ "$(leer "$CERROJO/dueno" pid)" = "$YO" ] || exit 0
      printf '%s\n' "$(epoca)" > "$CERROJO/latido.tmp" 2>/dev/null && mv -f "$CERROJO/latido.tmp" "$CERROJO/latido" 2>/dev/null
    done
  ) >/dev/null 2>&1 </dev/null &
  LATIDO_PID=$!
}

tomar_cerrojo() {
  mkdir "$CERROJO" 2>/dev/null || return 1
  TENGO_CERROJO=1
  # Sin registro de dueño y latido válidos el cerrojo caducaría con el comando aún
  # en marcha y otro waiter entraría a la vez: si no se pueden escribir, no se ejecuta.
  if ! { printf 'pid=%s\ninicio=%s\nworktree=%s\netiqueta=%s\ncomando=%s\n' \
           "$YO" "$(epoca)" "$PWD" "$etiqueta" "$*" > "$CERROJO/dueno" \
         && epoca > "$CERROJO/latido"; } 2>/dev/null; then
    rm -rf "$CERROJO"; TENGO_CERROJO=0; rm -f "$TICKET" "$TICKET.tmp"
    echo "TURNO-POSTGRES: ABORTADO — NO SE EJECUTÓ EL COMANDO: no se pudo escribir el registro del cerrojo en '$CERROJO'." >&2
    echo "TURNO-POSTGRES: ABORTADO_SIN_EJECUTAR motivo=cerrojo_sin_registro salida=$EX_INFRA" >&2
    trap - EXIT
    exit $EX_INFRA
  fi
  rm -f "$TICKET" "$TICKET.tmp"
  iniciar_latido "$@"
  return 0
}

liberar() {
  [ -n "$LATIDO_PID" ] && kill "$LATIDO_PID" 2>/dev/null
  LATIDO_PID=""
  rm -f "$TICKET" "$TICKET.tmp" 2>/dev/null
  if [ "$TENGO_CERROJO" = 1 ] && [ "$(leer "$CERROJO/dueno" pid)" = "$YO" ]; then
    rm -rf "$CERROJO"
  fi
  TENGO_CERROJO=0
}

hijos_directos() {  # pgrep en Linux; /proc/*/ppid en MSYS/Git Bash, donde no hay pgrep
  # No se usa el `ps` de MSYS: a los procesos parados les antepone una columna de
  # estado («S») que descoloca el PPID, y un descendiente parado se quedaba sin matar.
  # `grep -H` y no `awk` sobre los ficheros: un proceso que termina entre el glob y
  # la lectura hace abortar a awk entero («fatal: cannot open file») y se perdían
  # al azar los descendientes que venían detrás; grep sigue con el siguiente.
  if command -v pgrep >/dev/null 2>&1; then pgrep -P "$1" 2>/dev/null
  elif [ -r "/proc/$$/ppid" ]; then grep -H . /proc/[0-9]*/ppid 2>/dev/null | awk -F: -v p="$1" '$2 == p { n = split($1, a, "/"); print a[n - 1] }'
  else ps -e -o pid=,ppid= 2>/dev/null | awk -v p="$1" '$2 == p { print $1 }'   # POSIX, sin pgrep ni /proc/*/ppid
  fi
}

descendientes() {  # pids descendientes de $1, los más profundos primero
  local c
  for c in $(hijos_directos "$1"); do descendientes "$c"; echo "$c"; done
}

alguno_vivo() { local q; for q in "$@"; do vivo "$q" && return 0; done; return 1; }

# Mata el comando Y a sus descendientes (dotnet test -> testhost) y espera a que
# TODOS hayan muerto (SIGKILL a los que ignoren SIGTERM). El cerrojo NO se libera
# hasta entonces: liberarlo con el testhost aún vivo dejaba entrar a la suite
# siguiente contra el mismo clúster. Solo si alguno resiste incluso al SIGKILL se
# libera igualmente, con un AVISO explícito (no hay otra salida: el guion no puede
# quedarse vivo para siempre).
matar_arbol() {
  local p=$1 w lista todos i
  # Los descendientes MSYS se leen ANTES de matar nada (`taskkill /T` no los
  # alcanza: no cuelgan del padre en el árbol de Windows) y se matan al final.
  lista=$(descendientes "$p")
  todos="$lista $p"
  if command -v taskkill >/dev/null 2>&1 && [ -r "/proc/$p/winpid" ]; then
    w=$(cat "/proc/$p/winpid" 2>/dev/null)
    [ -n "$w" ] && taskkill //T //F //PID "$w" >/dev/null 2>&1   # hijos nativos: dotnet -> testhost
  fi
  # shellcheck disable=SC2086
  kill $todos 2>/dev/null
  for i in $(seq 1 100); do alguno_vivo $todos || return 0; sleep 0.1; done
  # shellcheck disable=SC2086
  kill -9 $todos 2>/dev/null
  for i in $(seq 1 50); do alguno_vivo $todos || return 0; sleep 0.1; done
  echo "TURNO-POSTGRES: AVISO: quedan procesos del comando vivos tras SIGKILL (pids: $todos); se libera el cerrojo igualmente." >&2
  return 0
}

al_interrumpir() {
  local senal=$1
  [ -n "$HIJO" ] && matar_arbol "$HIJO"
  local ejecutando=0; [ "$TENGO_CERROJO" = 1 ] && ejecutando=1
  liberar
  if [ "$ejecutando" = 1 ]; then
    echo "TURNO-POSTGRES: INTERRUMPIDO señal=$senal salida=$((128 + senal)) esperado_s=$(esperado_s)" >&2
  else
    echo "TURNO-POSTGRES: ABORTADO_SIN_EJECUTAR motivo=interrumpido_esperando señal=$senal salida=$((128 + senal))" >&2
  fi
  trap - EXIT
  exit $((128 + senal))
}
trap 'al_interrumpir 2' INT
trap 'al_interrumpir 15' TERM
trap 'al_interrumpir 1' HUP
trap 'liberar' EXIT

# ---- Esperar turno ---------------------------------------------------------
TICKET="$COLA/$(marca_ns)-$YO"
refrescar_ticket

sospecha=""
proximo_aviso=0
while true; do
  refrescar_ticket
  limpiar_cola
  if [ "$(primero_de_la_cola)" = "$(basename "$TICKET")" ]; then
    if tomar_cerrojo "$@"; then break; fi
    if [ ! -d "$CERROJO" ]; then
      # El `mkdir` falló porque lo tenía el dueño, y este pudo liberarlo ANTES de esta
      # comprobación: «no hay directorio» no prueba que no se pueda crear. Tras una
      # suspensión dueño y waiter despiertan a la vez y la ventana se abre (visto en CI:
      # el waiter abortaba con 74 sin ejecutar y perdía el turno). Se reintenta una vez;
      # un fallo real (permisos, directorio padre ausente) persiste y se aborta igual.
      if tomar_cerrojo "$@"; then break; fi
    fi
    if [ ! -d "$CERROJO" ]; then
      echo "TURNO-POSTGRES: ABORTADO — NO SE EJECUTÓ EL COMANDO: no se pudo crear '$CERROJO'." >&2
      echo "TURNO-POSTGRES: ABORTADO_SIN_EJECUTAR motivo=sin_directorio_de_turno salida=$EX_INFRA" >&2
      rm -f "$TICKET" "$TICKET.tmp"
      exit $EX_INFRA
    fi
    if cerrojo_huerfano; then
      if [ "$CERTEZA" = 1 ] || [ "$sospecha" = "$HUELLA" ]; then
        # Solo reintenta de inmediato si de verdad lo retiró; si el rename falla
        # (permisos, otro retirador) se cae al control de tiempo y al sondeo: un
        # `continue` incondicional aquí giraba en caliente sin tope.
        if retirar_cerrojo_huerfano "$MOTIVO"; then sospecha=""; continue; fi
      else
        sospecha=$HUELLA
      fi
    else
      sospecha=""
    fi
  fi

  presenciar
  if [ $(( AHORA_P - INICIO_EP - SUSPENDIDO_S )) -ge "$espera_max_s" ]; then
    liberar
    echo "" >&2
    echo "TURNO-POSTGRES: ABORTADO — NO SE EJECUTÓ EL COMANDO. No hay resultado de test: ni verde ni rojo." >&2
    echo "TURNO-POSTGRES: sin turno tras $(esperado_s)s; dueño actual: $(descripcion_dueno)." >&2
    echo "TURNO-POSTGRES: ABORTADO_SIN_EJECUTAR motivo=espera_agotada esperado_s=$(esperado_s) salida=$EX_SIN_TURNO" >&2
    trap - EXIT
    exit $EX_SIN_TURNO
  fi

  if [ "$(esperado_s)" -ge "$proximo_aviso" ]; then
    echo "TURNO-POSTGRES: esperando turno (posición $(posicion_en_cola) en la cola, $(( AHORA_P - INICIO_EP - SUSPENDIDO_S ))s de $espera_max_s s); dueño: $(descripcion_dueno)." >&2
    proximo_aviso=$(( $(esperado_s) + AVISO_S ))
  fi
  sleep "$SONDEO_S"
done

# ---- Ejecutar con el cerrojo tomado ---------------------------------------
esperado=$(esperado_s)
"$@" <&0 &
HIJO=$!
wait "$HIJO"
salida=$?
HIJO=""
liberar
echo "TURNO-POSTGRES: EJECUTADO salida=$salida esperado_s=$esperado" >&2
trap - EXIT
exit "$salida"
