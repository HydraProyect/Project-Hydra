#!/usr/bin/env bash
# Tests de scripts/turno-postgres.sh
#
# No necesitan .NET ni PostgreSQL: el «comando» es un bash que apunta en un
# registro cuándo empieza y cuándo acaba, así que lo que se comprueba es la
# EXCLUSIÓN MUTUA y el ORDEN, que es lo que puede romperse en silencio.
#
# TURNO_SCRIPT permite apuntar a una copia mutada del guion (prueba de
# sensibilidad) sin tocar el árbol de trabajo.

set -uo pipefail

AQUI=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
GUION="${TURNO_SCRIPT:-$AQUI/turno-postgres.sh}"

# Tiempos cortos para ir rápido, pero con margen: una máquina con quince
# worktrees compilando tarda en planificar un latido.
export HYDRA_TURNO_SONDEO_S=0.2
export HYDRA_TURNO_LATIDO_S=0.3
export HYDRA_TURNO_CADUCIDAD_S=20
export HYDRA_TURNO_AVISO_S=3600

BASE=$(mktemp -d)
PIDS_AJENOS=()
limpiar() {
  local p
  for p in "${PIDS_AJENOS[@]:-}"; do [ -n "$p" ] && kill "$p" 2>/dev/null; done
  rm -rf "$BASE"
}
trap limpiar EXIT

fallos=0
comprobar() {
  local caso=$1 esperado=$2 obtenido=$3
  if [ "$esperado" = "$obtenido" ]; then
    echo "  ok   $caso"
  else
    echo "  FALLO $caso"
    echo "        esperado: $esperado"
    echo "        obtenido: $obtenido"
    fallos=$((fallos + 1))
  fi
}

# Cada caso vive en su propio directorio de turno: no se contaminan entre sí.
nuevo_caso() {
  CASO="$BASE/$1"
  mkdir -p "$CASO"
  export HYDRA_TURNO_DIR="$CASO/turno"
  REGISTRO="$CASO/registro"
  : > "$REGISTRO"
}

# Comando de prueba: apunta «<id>_ini», mantiene el turno N segundos, apunta «<id>_fin».
tarea() { echo "echo ${1}_ini >> '$REGISTRO'; sleep $2; echo ${1}_fin >> '$REGISTRO'"; }

esperar_a() {  # esperar_a "texto" fichero [segundos]
  local i max=${3:-30}
  for i in $(seq 1 $((max * 10))); do
    grep -q "$1" "$2" 2>/dev/null && return 0
    sleep 0.1
  done
  return 1
}

n_tickets() { ls "$HYDRA_TURNO_DIR/cola" 2>/dev/null | grep -vc '\.tmp$'; }

dormilon() {  # lanza un proceso ajeno vivo y deja su pid en DORMILON
  sleep 300 &
  DORMILON=$!
  PIDS_AJENOS+=("$DORMILON")
}

pid_muerto() {
  local p
  p=$(bash -c 'echo $$'); sleep 0.3
  echo "$p"
}

echo "turno-postgres.sh"

# 1 -------------------------------------------------------------------------
nuevo_caso propaga-salida
bash "$GUION" -- bash -c 'exit 3' > "$CASO/o" 2>&1; rc=$?
comprobar "propaga la salida del comando (3)" "3" "$rc"
comprobar "la línea final dice EJECUTADO salida=3" "1" "$(grep -c 'TURNO-POSTGRES: EJECUTADO salida=3' "$CASO/o")"

# 2 -------------------------------------------------------------------------
# EL CASO CENTRAL: dos invocaciones concurrentes. La segunda debe ESPERAR y
# EJECUTARSE, no abortar; y sus secciones críticas no pueden solaparse.
nuevo_caso concurrencia
bash "$GUION" --etiqueta A -- bash -c "$(tarea A 2)" > "$CASO/oA" 2>&1 & pa=$!
esperar_a "A_ini" "$REGISTRO" || echo "  (A no arrancó a tiempo)"
bash "$GUION" --etiqueta B -- bash -c "$(tarea B 0.2)" > "$CASO/oB" 2>&1 & pb=$!
wait "$pa"; rca=$?
wait "$pb"; rcb=$?
comprobar "ambas terminan con 0 (la segunda no aborta)" "0 0" "$rca $rcb"
comprobar "secciones críticas SIN solape: A entero, luego B" "A_ini A_fin B_ini B_fin" "$(tr '\n' ' ' < "$REGISTRO" | sed 's/ $//')"
comprobar "la segunda no imprimió ABORTADO" "0" "$(grep -c 'ABORTADO' "$CASO/oB")"
comprobar "no quedan restos: ni cerrojo ni tickets" "sin-cerrojo 0" "$([ -d "$HYDRA_TURNO_DIR/cerrojo" ] && echo con-cerrojo || echo sin-cerrojo) $(n_tickets)"

# 3 -------------------------------------------------------------------------
nuevo_caso orden-de-llegada
bash "$GUION" -- bash -c "$(tarea A 2.5)" > "$CASO/oA" 2>&1 & pa=$!
esperar_a "A_ini" "$REGISTRO" || true
bash "$GUION" -- bash -c "$(tarea B 0.3)" > "$CASO/oB" 2>&1 & pb=$!
for i in $(seq 1 100); do [ "$(n_tickets)" -ge 1 ] && break; sleep 0.1; done
bash "$GUION" -- bash -c "$(tarea C 0.3)" > "$CASO/oC" 2>&1 & pc=$!
wait "$pa" "$pb" "$pc"
comprobar "orden de llegada A, B, C" "A_ini A_fin B_ini B_fin C_ini C_fin" "$(tr '\n' ' ' < "$REGISTRO" | sed 's/ $//')"

# 4 -------------------------------------------------------------------------
nuevo_caso espera-agotada
rm -f "$CASO/marca"
bash "$GUION" -- bash -c "$(tarea A 3)" > "$CASO/oA" 2>&1 & pa=$!
esperar_a "A_ini" "$REGISTRO" || true
bash "$GUION" --espera-max-s 1 -- bash -c "touch '$CASO/marca'; echo B_ini >> '$REGISTRO'" > "$CASO/oB" 2>&1; rcb=$?
comprobar "sin turno: salida propia 75" "75" "$rcb"
comprobar "el comando NO se ejecutó" "no" "$([ -e "$CASO/marca" ] && echo si || echo no)"
comprobar "mensaje inequívoco: ABORTADO_SIN_EJECUTAR motivo=espera_agotada" "1" "$(grep -c 'ABORTADO_SIN_EJECUTAR motivo=espera_agotada' "$CASO/oB")"
comprobar "no imprime EJECUTADO ni nada con aspecto de resultado" "0" "$(grep -cE 'EJECUTADO|Passed|Superado|Correctas' "$CASO/oB")"
wait "$pa"; rca=$?
comprobar "el dueño no se ve afectado: termina 0 y su registro está completo" "0 A_ini A_fin" "$rca $(tr '\n' ' ' < "$REGISTRO" | sed 's/ $//')"
comprobar "el que se rindió no deja ticket" "0" "$(n_tickets)"

# 5 -------------------------------------------------------------------------
nuevo_caso salida-reservada-ambigua
bash "$GUION" -- bash -c 'exit 75' > "$CASO/o" 2>&1; rc=$?
comprobar "un comando que sale con 75 se distingue por la línea EJECUTADO" "75 1" "$rc $(grep -c 'EJECUTADO salida=75' "$CASO/o")"

# 6 -------------------------------------------------------------------------
# Cerrojo huérfano: el dueño murió sin liberar. No puede parar la máquina.
nuevo_caso huerfano-pid-muerto
muerto=$(pid_muerto)
mkdir -p "$HYDRA_TURNO_DIR/cerrojo"
printf 'pid=%s\ninicio=%s\nworktree=/otro\ncomando=cosa\n' "$muerto" "$(date +%s)" > "$HYDRA_TURNO_DIR/cerrojo/dueno"
date +%s > "$HYDRA_TURNO_DIR/cerrojo/latido"      # latido FRESCO: solo el pid delata al huérfano
bash "$GUION" --espera-max-s 15 -- bash -c "$(tarea H 0.1)" > "$CASO/o" 2>&1; rc=$?
comprobar "retira el cerrojo de un pid muerto y ejecuta" "0 H_ini H_fin" "$rc $(tr '\n' ' ' < "$REGISTRO" | sed 's/ $//')"
comprobar "lo dice: cerrojo huérfano retirado" "1" "$(grep -c 'cerrojo huérfano retirado' "$CASO/o")"

# 7 -------------------------------------------------------------------------
nuevo_caso huerfano-latido-caducado
dormilon
mkdir -p "$HYDRA_TURNO_DIR/cerrojo"
printf 'pid=%s\ninicio=%s\nworktree=/otro\ncomando=cosa\n' "$DORMILON" "$(( $(date +%s) - 1000 ))" > "$HYDRA_TURNO_DIR/cerrojo/dueno"
echo $(( $(date +%s) - 1000 )) > "$HYDRA_TURNO_DIR/cerrojo/latido"   # pid VIVO, latido caducado
# Quien llega solo cuenta el tiempo que ha presenciado: aunque el latido tenga
# 1000 s, no lo da por caducado hasta llevar CADUCIDAD observándolo (ver 11b).
# Por eso aquí una caducidad corta, para que quepa en la espera.
HYDRA_TURNO_CADUCIDAD_S=6 bash "$GUION" --espera-max-s 15 -- bash -c "$(tarea L 0.1)" > "$CASO/o" 2>&1; rc=$?
comprobar "retira el cerrojo de un dueño vivo pero sin latido y ejecuta" "0 L_ini L_fin" "$rc $(tr '\n' ' ' < "$REGISTRO" | sed 's/ $//')"
comprobar "lo dice: latido" "1" "$(grep -c 'sin refrescarse' "$CASO/o")"

# 8 -------------------------------------------------------------------------
# Contrapeso de los dos anteriores: un dueño VIVO con latido FRESCO no se toca.
nuevo_caso dueno-vivo-no-se-toca
dormilon
mkdir -p "$HYDRA_TURNO_DIR/cerrojo"
printf 'pid=%s\ninicio=%s\nworktree=/otro\ncomando=cosa\n' "$DORMILON" "$(date +%s)" > "$HYDRA_TURNO_DIR/cerrojo/dueno"
( while kill -0 "$DORMILON" 2>/dev/null; do date +%s > "$HYDRA_TURNO_DIR/cerrojo/latido"; sleep 0.2; done ) >/dev/null 2>&1 &
PIDS_AJENOS+=("$!")
sleep 0.5
bash "$GUION" --espera-max-s 3 -- bash -c "touch '$CASO/marca'" > "$CASO/o" 2>&1; rc=$?
comprobar "un dueño vivo con latido fresco no se retira: 75 y no ejecuta" "75 no" "$rc $([ -e "$CASO/marca" ] && echo si || echo no)"
kill "$DORMILON" 2>/dev/null

# 9 -------------------------------------------------------------------------
nuevo_caso ticket-huerfano-no-bloquea
muerto=$(pid_muerto)
mkdir -p "$HYDRA_TURNO_DIR/cola"
printf 'pid=%s\nlatido=%s\n' "$muerto" "$(date +%s)" > "$HYDRA_TURNO_DIR/cola/0000000000000000001-$muerto"
bash "$GUION" --espera-max-s 15 -- bash -c "$(tarea T 0.1)" > "$CASO/o" 2>&1; rc=$?
comprobar "un ticket de un proceso muerto, aunque llegara antes, no bloquea la cola" "0 T_ini T_fin" "$rc $(tr '\n' ' ' < "$REGISTRO" | sed 's/ $//')"

# 10 ------------------------------------------------------------------------
nuevo_caso interrupcion-libera
bash "$GUION" -- bash -c "echo \$\$ > '$CASO/pidhijo'; echo I_ini >> '$REGISTRO'; exec sleep 60" > "$CASO/o" 2>&1 & pw=$!
esperar_a "I_ini" "$REGISTRO" || true
kill -TERM "$pw" 2>/dev/null
wait "$pw" 2>/dev/null; rc=$?
for i in $(seq 1 50); do [ ! -d "$HYDRA_TURNO_DIR/cerrojo" ] && break; sleep 0.1; done
comprobar "SIGTERM: sale con 143 y el cerrojo queda liberado" "143 sin-cerrojo" "$rc $([ -d "$HYDRA_TURNO_DIR/cerrojo" ] && echo con-cerrojo || echo sin-cerrojo)"
comprobar "SIGTERM: lo dice como INTERRUMPIDO" "1" "$(grep -c 'INTERRUMPIDO señal=15' "$CASO/o")"

# 10b -----------------------------------------------------------------------
# Hallazgo de la revisión previa: al interrumpir hay que matar el ÁRBOL (dotnet
# test -> testhost) y esperar a que muera ANTES de liberar el cerrojo; si no, la
# suite siguiente entra con el testhost anterior aún vivo sobre el mismo clúster.
nuevo_caso interrupcion-mata-el-arbol
bash "$GUION" -- bash -c "sleep 300 & echo \$! > '$CASO/pidnieto'; echo N_ini >> '$REGISTRO'; wait" > "$CASO/o" 2>&1 & pw=$!
esperar_a "N_ini" "$REGISTRO" || true
nieto=$(cat "$CASO/pidnieto" 2>/dev/null)
PIDS_AJENOS+=("$nieto")
kill -TERM "$pw" 2>/dev/null
wait "$pw" 2>/dev/null
# Sin sondeo: en el instante en que el guion ha terminado, el nieto ya no puede vivir.
comprobar "SIGTERM: el descendiente del comando ha muerto cuando el guion termina" "muerto sin-cerrojo" "$(kill -0 "$nieto" 2>/dev/null && echo vivo || echo muerto) $([ -d "$HYDRA_TURNO_DIR/cerrojo" ] && echo con-cerrojo || echo sin-cerrojo)"

# 10c -----------------------------------------------------------------------
# Un descendiente que IGNORA SIGTERM no puede quedar vivo cuando se libera el cerrojo.
nuevo_caso interrupcion-descendiente-rebelde
bash "$GUION" -- bash -c "trap '' TERM; sleep 300 & echo \$! > '$CASO/pidnieto'; echo R_ini >> '$REGISTRO'; wait" > "$CASO/o" 2>&1 & pw=$!
esperar_a "R_ini" "$REGISTRO" || true
nieto=$(cat "$CASO/pidnieto" 2>/dev/null)
PIDS_AJENOS+=("$nieto")
kill -TERM "$pw" 2>/dev/null
wait "$pw" 2>/dev/null
comprobar "SIGTERM ignorado por el descendiente: muere igualmente antes de liberar" "muerto sin-cerrojo" "$(kill -0 "$nieto" 2>/dev/null && echo vivo || echo muerto) $([ -d "$HYDRA_TURNO_DIR/cerrojo" ] && echo con-cerrojo || echo sin-cerrojo)"

# 10d -----------------------------------------------------------------------
# Un descendiente PARADO (SIGSTOP) tampoco puede sobrevivir al guion. En MSYS/Git
# Bash, `ps` antepone una columna de estado («S») a los procesos parados y
# descoloca el PPID: sin arreglo, el guion no lo veía como descendiente, no lo
# mataba y seguía vivo contra el clúster tras liberar el cerrojo.
nuevo_caso interrupcion-descendiente-parado
# `taskkill /T` (solo Windows) alcanza a este nieto por el árbol de Windows y
# taparía el defecto; se sustituye por uno que no hace nada para observar solo el
# recorrido MSYS de descendientes, que es el que usa matar_arbol cuando taskkill
# no llega (o no existe).
mkdir -p "$CASO/sin-taskkill"; printf '#!/bin/sh\nexit 0\n' > "$CASO/sin-taskkill/taskkill"; chmod +x "$CASO/sin-taskkill/taskkill"
PATH="$CASO/sin-taskkill:$PATH" bash "$GUION" -- bash -c "sleep 300 & echo \$! > '$CASO/pidnieto'; kill -STOP \$!; echo P_ini >> '$REGISTRO'; wait" > "$CASO/o" 2>&1 & pw=$!
esperar_a "P_ini" "$REGISTRO" || true
nieto=$(cat "$CASO/pidnieto" 2>/dev/null)
PIDS_AJENOS+=("$nieto")
kill -TERM "$pw" 2>/dev/null
wait "$pw" 2>/dev/null
comprobar "SIGTERM con un descendiente parado: muere antes de liberar" "muerto sin-cerrojo" "$(kill -0 "$nieto" 2>/dev/null && echo vivo || echo muerto) $([ -d "$HYDRA_TURNO_DIR/cerrojo" ] && echo con-cerrojo || echo sin-cerrojo)"
[ -n "$nieto" ] && { kill -CONT "$nieto"; kill -9 "$nieto"; } 2>/dev/null

# 11 ------------------------------------------------------------------------
# Un turno LARGO no caduca mientras el dueño siga latiendo: sin latido, la
# caducidad retiraría un cerrojo legítimo y dos suites correrían a la vez.
nuevo_caso latido-mantiene-turno-largo
HYDRA_TURNO_CADUCIDAD_S=3 bash "$GUION" -- bash -c "$(tarea A 7)" > "$CASO/oA" 2>&1 & pa=$!
esperar_a "A_ini" "$REGISTRO" || true
HYDRA_TURNO_CADUCIDAD_S=3 bash "$GUION" -- bash -c "$(tarea B 0.2)" > "$CASO/oB" 2>&1 & pb=$!
wait "$pa"; rca=$?
wait "$pb"; rcb=$?
comprobar "turno más largo que la caducidad: no se retira mientras hay latido" "0 0 A_ini A_fin B_ini B_fin" "$rca $rcb $(tr '
' ' ' < "$REGISTRO" | sed 's/ $//')"

# 11b -----------------------------------------------------------------------
# Suspensión del equipo. Mientras el equipo duerme nadie refresca nada, y al
# despertar todos los latidos parecen caducados por reloj de pared aunque todos
# los procesos sigan vivos. Se simula congelando con SIGSTOP a los tres procesos
# (y a sus hijos) más tiempo que la caducidad, y despertando primero a C, el
# último en llegar. Sin arreglo, C descarta el ticket de B y retira el cerrojo del
# dueño vivo A: ejecuta con A a medias (solape) y adelanta a B.
# Límite deliberado: si C sigue despierto más de CADUCIDAD con los otros aún
# parados, eso ya no es una suspensión del equipo sino procesos colgados, y se
# retiran como en el caso 7.
pares_pid_ppid() {  # «pid ppid» de todos los procesos, en UNA lectura (rápida y casi atómica)
  if ps -e -o pid=,ppid= >/dev/null 2>&1; then ps -e -o pid=,ppid=
  else grep -H . /proc/[0-9]*/ppid 2>/dev/null | awk -F: '{ n = split($1, a, "/"); print a[n - 1], $2 }'; fi   # grep, no awk: awk aborta si un proceso muere a mitad
  # En MSYS/Git Bash `ps` no admite -o y antepone una columna de estado a los
  # procesos parados, lo que descoloca el PPID justo en este caso: /proc/*/ppid.
}
arbol() {  # pids de los procesos dados y de todos sus descendientes
  pares_pid_ppid | awk -v raices="$*" '
    { hijo[$2] = hijo[$2] " " $1 }
    END { n = split(raices, cola, " "); for (i = 1; i <= n; i++) { print cola[i]; m = split(hijo[cola[i]], h, " "); for (j = 1; j <= m; j++) cola[++n] = h[j] } }'
}
# Dos pasadas: la segunda alcanza a los hijos que se crearon mientras se paraba a los padres.
senal_arbol() { local s=$1; shift; kill "-$s" $(arbol "$@") 2>/dev/null; kill "-$s" $(arbol "$@") 2>/dev/null; }
nuevo_caso suspension-del-equipo
export HYDRA_TURNO_CADUCIDAD_S=6
bash "$GUION" -- bash -c "$(tarea A 2)" > "$CASO/oA" 2>&1 & pa=$!
esperar_a "A_ini" "$REGISTRO" || true
bash "$GUION" -- bash -c "$(tarea B 0.2)" > "$CASO/oB" 2>&1 & pb=$!
for i in $(seq 1 100); do [ "$(n_tickets)" -ge 1 ] && break; sleep 0.1; done
bash "$GUION" -- bash -c "$(tarea C 0.2)" > "$CASO/oC" 2>&1 & pc=$!
for i in $(seq 1 100); do [ "$(n_tickets)" -ge 2 ] && break; sleep 0.1; done
senal_arbol STOP "$pa" "$pb" "$pc"
sleep 8                                    # > CADUCIDAD: al despertar todo parece caducado
senal_arbol CONT "$pc"
sleep 3                                    # C solo, con el mundo aún dormido: sin arreglo le basta
                                           # (todo lleva 8 s caducado); con arreglo, 3 s < CADUCIDAD.
senal_arbol CONT "$pa" "$pb"
wait "$pa" "$pb" "$pc"
comprobar "tras una suspensión: sin solape y en orden de llegada A, B, C" "A_ini A_fin B_ini B_fin C_ini C_fin" "$(tr '\n' ' ' < "$REGISTRO" | sed 's/ $//')"
comprobar "tras una suspensión: no retira el cerrojo de un dueño vivo" "0" "$(cat "$CASO"/o? | grep -c 'cerrojo huérfano retirado')"

# 11c -----------------------------------------------------------------------
# La espera máxima mide cola, no sueño: sin arreglo, B se rinde con 75 al
# despertar porque el tiempo suspendido ha agotado su presupuesto, y pierde el turno.
nuevo_caso suspension-no-gasta-la-espera
bash "$GUION" -- bash -c "$(tarea A 2)" > "$CASO/oA" 2>&1 & pa=$!
esperar_a "A_ini" "$REGISTRO" || true
bash "$GUION" --espera-max-s 4 -- bash -c "$(tarea B 0.2)" > "$CASO/oB" 2>&1 & pb=$!
for i in $(seq 1 100); do [ "$(n_tickets)" -ge 1 ] && break; sleep 0.1; done
senal_arbol STOP "$pa" "$pb"
sleep 5
senal_arbol CONT "$pa" "$pb"
wait "$pa"; wait "$pb"; rcb=$?
comprobar "tras una suspensión más larga que la espera máxima: B no se rinde y ejecuta" "0 A_ini A_fin B_ini B_fin" "$rcb $(tr '\n' ' ' < "$REGISTRO" | sed 's/ $//')"
export HYDRA_TURNO_CADUCIDAD_S=20

# 12 ------------------------------------------------------------------------
nuevo_caso uso
bash "$GUION" > "$CASO/o" 2>&1; rc=$?
comprobar "sin comando: salida 64" "64" "$rc"

# 13 ------------------------------------------------------------------------
# Hallazgo de la revisión previa: caducidad por debajo del latido = un dueño
# legítimo no puede refrescar a tiempo y se le retira el cerrojo. Se rechaza.
nuevo_caso configuracion-invalida
rm -f "$CASO/marca"
HYDRA_TURNO_CADUCIDAD_S=1 HYDRA_TURNO_LATIDO_S=5 bash "$GUION" -- bash -c "touch '$CASO/marca'" > "$CASO/o" 2>&1; rc=$?
comprobar "caducidad < latido: se rechaza con 64 y NO ejecuta" "64 no" "$rc $([ -e "$CASO/marca" ] && echo si || echo no)"
comprobar "lo dice: ABORTADO_SIN_EJECUTAR motivo=configuracion_invalida" "1" "$(grep -c 'ABORTADO_SIN_EJECUTAR motivo=configuracion_invalida' "$CASO/o")"
# Formas que una comprobación laxa (awk «c + 0») deja pasar y que luego rompen la
# aritmética de bash: prefijo numérico, notación científica, espera no entera.
for malo in abc 120junk 1e2 -5; do
  rm -f "$CASO/marca"
  HYDRA_TURNO_CADUCIDAD_S=$malo bash "$GUION" -- bash -c "touch '$CASO/marca'" > "$CASO/o" 2>&1; rc=$?
  comprobar "HYDRA_TURNO_CADUCIDAD_S='$malo': se rechaza con 64 y no ejecuta" "64 no" "$rc $([ -e "$CASO/marca" ] && echo si || echo no)"
done
HYDRA_TURNO_LATIDO_S=1e-1 bash "$GUION" -- bash -c "touch '$CASO/marca'" > "$CASO/o" 2>&1; rc=$?
comprobar "HYDRA_TURNO_LATIDO_S='1e-1': se rechaza con 64" "64" "$rc"

# 13b -----------------------------------------------------------------------
# La cola pierde a B. El ticket de quien espera solo se refresca una vez por
# sondeo, y cualquier otro waiter lo borra en cuanto lleva más de CADUCIDAD sin
# refrescarse. Con SONDEO >= CADUCIDAD, B (vivo, primero tras el dueño) pierde su
# ticket mientras duerme entre sondeos, y C, llegado después, toma el turno antes
# que B. Medido antes del arreglo con A 4 s, B SONDEO=3, C SONDEO=0.2, CADUCIDAD=1:
# orden A C B en 2 de 3 vueltas. Esa configuración se aceptaba; ahora se rechaza.
rm -f "$CASO/marca"
HYDRA_TURNO_CADUCIDAD_S=1 HYDRA_TURNO_SONDEO_S=3 bash "$GUION" -- bash -c "touch '$CASO/marca'" > "$CASO/o" 2>&1; rc=$?
comprobar "sondeo > caducidad (la cola perdería a B): se rechaza con 64 y NO ejecuta" "64 no" "$rc $([ -e "$CASO/marca" ] && echo si || echo no)"
comprobar "lo dice: HYDRA_TURNO_SONDEO_S en el motivo" "1" "$(grep -c 'configuración inválida:.*HYDRA_TURNO_SONDEO_S' "$CASO/o")"
rm -f "$CASO/marca"
HYDRA_TURNO_CADUCIDAD_S=2 HYDRA_TURNO_SONDEO_S=1 HYDRA_TURNO_LATIDO_S=0.3 bash "$GUION" -- bash -c "touch '$CASO/marca'" > "$CASO/o" 2>&1; rc=$?
comprobar "sondeo por debajo de la caducidad pero sin margen de 3x: se rechaza con 64" "64 no" "$rc $([ -e "$CASO/marca" ] && echo si || echo no)"
rm -f "$CASO/marca"
HYDRA_TURNO_CADUCIDAD_S=3 HYDRA_TURNO_SONDEO_S=1 HYDRA_TURNO_LATIDO_S=1 bash "$GUION" -- bash -c "touch '$CASO/marca'" > "$CASO/o" 2>&1; rc=$?
comprobar "contrapeso: caducidad = 3 x sondeo = 3 x latido se acepta y ejecuta" "0 si" "$rc $([ -e "$CASO/marca" ] && echo si || echo no)"
for malo in abc 5s 1.5 -1; do
  rm -f "$CASO/marca"
  bash "$GUION" --espera-max-s "$malo" -- bash -c "touch '$CASO/marca'" > "$CASO/o" 2>&1; rc=$?
  comprobar "--espera-max-s '$malo': se rechaza con 64 y no ejecuta" "64 no" "$rc $([ -e "$CASO/marca" ] && echo si || echo no)"
done
bash "$GUION" --espera-max-min x -- bash -c "touch '$CASO/marca'" > "$CASO/o" 2>&1; rc=$?
comprobar "--espera-max-min 'x': se rechaza con 64" "64" "$rc"

echo
if [ "$fallos" -eq 0 ]; then
  echo "TODOS LOS CASOS EN VERDE"
  exit 0
fi
echo "$fallos caso(s) en FALLO"
exit 1
