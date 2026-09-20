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
bash "$GUION" --espera-max-s 15 -- bash -c "$(tarea L 0.1)" > "$CASO/o" 2>&1; rc=$?
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
HYDRA_TURNO_CADUCIDAD_S=abc bash "$GUION" -- bash -c "touch '$CASO/marca'" > "$CASO/o" 2>&1; rc=$?
comprobar "tiempo no numérico: se rechaza con 64" "64" "$rc"

echo
if [ "$fallos" -eq 0 ]; then
  echo "TODOS LOS CASOS EN VERDE"
  exit 0
fi
echo "$fallos caso(s) en FALLO"
exit 1
