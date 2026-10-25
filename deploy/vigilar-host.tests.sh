#!/bin/bash
# Tests de deploy/vigilar-host.sh
#
# El disco se fuerza con VIGILANCIA_USO_DISCO, /proc/meminfo y /proc/vmstat se
# sustituyen por ficheros y curl por un doble que anota a que URL se llamo (la
# lee de stdin, como hace el guion), asi que corren en CI sin VPS, sin red y
# sin llenar nada de verdad. Los parches de seguridad se fuerzan con
# VIGILANCIA_PARCHES_SEGURIDAD, el reboot-required es un fichero del temporal y
# el reloj se fija con VIGILANCIA_AHORA: ningun caso mira el apt ni el /var/run
# del runner.
#
# VIGILAR_HOST_GUION permite apuntar a una copia mutada del guion para la
# prueba de sensibilidad sin tocar el arbol de trabajo.

set -uo pipefail

AQUI=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
GUION="${VIGILAR_HOST_GUION:-$AQUI/vigilar-host.sh}"
URL="https://uptime.betterstack.com/api/v1/heartbeat/SECRETO-DE-PRUEBA"

TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT
mkdir -p "$TMP/bin"
cat > "$TMP/bin/curl" <<'EOF'
#!/bin/bash
# Doble de curl: registra la URL recibida por stdin (-K -) y los argumentos.
entrada=$(cat)
echo "$entrada" | sed -n 's/^url = "\(.*\)"$/\1/p' >> "$CURL_LOG"
echo "ARGS $*" >> "$CURL_ARGS"
[ "${CURL_FALLA:-0}" = "1" ] && exit 7
exit 0
EOF
chmod +x "$TMP/bin/curl"
export PATH="$TMP/bin:$PATH" CURL_LOG="$TMP/curl.log" CURL_ARGS="$TMP/curl.args"

meminfo() {   # meminfo PORCENTAJE_DISPONIBLE -> fichero con total 4000000 kB
  printf 'MemTotal:        4000000 kB\nMemFree:          100000 kB\nMemAvailable:    %s kB\n' \
    "$(( 4000000 * $1 / 100 ))" > "$TMP/meminfo"
}
vmstat() { printf 'nr_free_pages 1234\noom_kill %s\npgfault 99\n' "$1" > "$TMP/vmstat"; }

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

# ejecutar DISCO [VAR=valor ...] -> deja $codigo, $salida y $llamada (ultima URL
# llamada, o "ninguna").
ejecutar() {
  local disco=$1; shift
  : > "$CURL_LOG"; : > "$CURL_ARGS"
  salida=$(env VIGILANCIA_USO_DISCO="$disco" VIGILANCIA_MEMINFO="$TMP/meminfo" \
    VIGILANCIA_VMSTAT="$TMP/vmstat" VIGILANCIA_ESTADO="$TMP/estado" \
    VIGILANCIA_BOOT_ID="$TMP/boot_id" VIGILANCIA_PARCHES_SEGURIDAD=0 \
    VIGILANCIA_REINICIO_REQUERIDO="$TMP/reboot-required" \
    BETTERSTACK_HOST_HEARTBEAT_URL="$URL" "$@" bash "$GUION" 2>&1)
  codigo=$?
  llamada=$(tail -1 "$CURL_LOG"); [ -n "$llamada" ] || llamada="ninguna"
}
arranque() { echo "$1" > "$TMP/boot_id"; }
reiniciar() { rm -f "$TMP/estado" "$TMP/reboot-required"; meminfo 50; vmstat 3; arranque aaaa-1111; }
BASE=$(date -u +%s)
DIA=86400

echo "vigilar-host.sh"

# --- Control positivo del instrumento: el doble de curl ve la llamada. Si el
# guion dejara de llamar, o el doble no estuviera en el PATH, todos los casos
# "no llama a /fail" pasarian en verde sin observar nada.
reiniciar
ejecutar 40
comprobar "control positivo: en orden, sale 0" "0" "$codigo"
comprobar "control positivo: en orden, ping de exito observado" "$URL" "$llamada"

# --- La URL es un secreto: nunca en la linea de argumentos de curl.
if grep -q "SECRETO-DE-PRUEBA" "$CURL_ARGS"; then
  comprobar "la URL no va en los argumentos de curl" "no" "si"
else
  comprobar "la URL no va en los argumentos de curl" "no" "no"
fi

# --- Disco
reiniciar
ejecutar 90
comprobar "disco 90% (umbral 85): alerta" "1" "$codigo"
comprobar "disco 90%: llama a /fail" "$URL/fail" "$llamada"
case "$salida" in *"disco 90% >= 85%"*) r=si ;; *) r="no ($salida)" ;; esac
comprobar "disco 90%: el motivo nombra el disco" "si" "$r"

reiniciar
ejecutar 85
comprobar "disco justo en el umbral: alerta (>=, no >)" "$URL/fail" "$llamada"

reiniciar
ejecutar 84
comprobar "disco 84%: sin alerta" "$URL" "$llamada"

reiniciar
ejecutar 60 UMBRAL_DISCO=50
comprobar "umbral de disco forzado a 50 con 60%: alerta" "$URL/fail" "$llamada"

# --- Memoria: hacen falta MUESTRAS_MEMORIA lecturas bajas seguidas.
reiniciar; meminfo 5
ejecutar 40
comprobar "memoria 5% primera lectura: aun sin alerta" "$URL" "$llamada"
ejecutar 40
comprobar "memoria 5% segunda lectura seguida: alerta" "$URL/fail" "$llamada"
case "$salida" in *"memoria disponible 5% < 10%"*) r=si ;; *) r="no ($salida)" ;; esac
comprobar "memoria: el motivo nombra la memoria" "si" "$r"

reiniciar; meminfo 5
ejecutar 40
meminfo 30
ejecutar 40
meminfo 5
ejecutar 40
comprobar "una lectura sana entre dos bajas reinicia la cuenta" "$URL" "$llamada"

reiniciar; meminfo 10
ejecutar 40; ejecutar 40
comprobar "memoria justo en el umbral (10%): sin alerta" "$URL" "$llamada"

reiniciar; meminfo 5
ejecutar 40 MUESTRAS_MEMORIA=1
comprobar "MUESTRAS_MEMORIA=1: alerta a la primera" "$URL/fail" "$llamada"

# --- OOM killer
reiniciar
ejecutar 40
vmstat 4
ejecutar 40
comprobar "oom_kill sube de 3 a 4: alerta" "$URL/fail" "$llamada"
case "$salida" in *"OOM killer ha matado 1"*) r=si ;; *) r="no ($salida)" ;; esac
comprobar "oom: el motivo nombra el OOM killer" "si" "$r"
ejecutar 40
comprobar "oom_kill sin cambios en la siguiente: sin alerta" "$URL" "$llamada"

rm -f "$TMP/estado"; meminfo 50; vmstat 9
ejecutar 40
comprobar "primera ejecucion sin estado: toma referencia, sin alerta" "$URL" "$llamada"

# --- Sin URL: no hay red, pero el codigo de salida sigue diciendo la verdad.
reiniciar
: > "$CURL_LOG"
env VIGILANCIA_USO_DISCO=95 VIGILANCIA_MEMINFO="$TMP/meminfo" VIGILANCIA_VMSTAT="$TMP/vmstat" \
  VIGILANCIA_ESTADO="$TMP/estado" VIGILANCIA_BOOT_ID="$TMP/boot_id" VIGILANCIA_PARCHES_SEGURIDAD=0 \
  VIGILANCIA_REINICIO_REQUERIDO="$TMP/reboot-required" bash "$GUION" >/dev/null 2>&1
codigo=$?
comprobar "sin URL y disco 95%: sale 1" "1" "$codigo"
comprobar "sin URL: ninguna llamada de red" "0" "$(wc -l < "$CURL_LOG" | tr -d ' ')"

# --- Heartbeat caido: se anota y el codigo no cambia.
reiniciar
ejecutar 90 CURL_FALLA=1
comprobar "heartbeat inalcanzable: sigue saliendo 1" "1" "$codigo"
case "$salida" in *"no se pudo llamar al heartbeat"*) r=si ;; *) r="no ($salida)" ;; esac
comprobar "heartbeat inalcanzable: lo dice" "si" "$r"

# --- Reinicio del host: oom_kill vuelve a contar desde 0.
reiniciar; vmstat 8
ejecutar 40
arranque bbbb-2222; vmstat 1
ejecutar 40
comprobar "tras reiniciar (otro boot_id), 8 -> 1 es un OOM nuevo: alerta" "$URL/fail" "$llamada"
ejecutar 40
comprobar "tras reiniciar, sin OOM nuevos: sin alerta" "$URL" "$llamada"

reiniciar; vmstat 8
ejecutar 40
arranque bbbb-2222; vmstat 0
ejecutar 40
comprobar "tras reiniciar sin OOM (8 -> 0): sin alerta" "$URL" "$llamada"

# Solo el boot_id lo distingue: 2 OOM antes del reinicio y 2 despues.
reiniciar; vmstat 2
ejecutar 40
arranque bbbb-2222
ejecutar 40
comprobar "tras reiniciar, el mismo valor (2 -> 2) son OOM nuevos: alerta" "$URL/fail" "$llamada"

reiniciar; vmstat 8; rm -f "$TMP/boot_id"
ejecutar 40
vmstat 2
ejecutar 40
comprobar "sin boot_id legible, un contador que baja se toma como reinicio: alerta" "$URL/fail" "$llamada"

reiniciar; rm -f "$TMP/vmstat"
ejecutar 40
vmstat 5
ejecutar 40
comprobar "estado guardado sin oom_kill no desplaza campos: toma referencia" "$URL" "$llamada"

# --- Un OOM cuyo aviso no se entrego no se pierde: se repite en la siguiente.
reiniciar
ejecutar 40
vmstat 4
ejecutar 40 CURL_FALLA=1
comprobar "oom con heartbeat caido: sale 1" "1" "$codigo"
ejecutar 40
comprobar "oom no entregado: se repite al volver la red" "$URL/fail" "$llamada"
ejecutar 40
comprobar "oom ya entregado: no se repite" "$URL" "$llamada"

# --- Estado no escribible: la vigilancia de memoria quedaria ciega; avisa.
reiniciar
ejecutar 40 VIGILANCIA_ESTADO="$TMP/no-existe/estado"
comprobar "estado no escribible: alerta" "$URL/fail" "$llamada"

# --- Log imposible de escribir (disco lleno: el `>>` del cron falla). Antes,
# el echo abortaba con 1 bajo `set -e`, se confundia con "alerta ya avisada" y
# no llegaba ningun /fail (revision puente). Aqui stdout va cerrado.
reiniciar
: > "$CURL_LOG"
env VIGILANCIA_USO_DISCO=99 VIGILANCIA_MEMINFO="$TMP/meminfo" VIGILANCIA_VMSTAT="$TMP/vmstat" \
  VIGILANCIA_ESTADO="$TMP/estado" VIGILANCIA_BOOT_ID="$TMP/boot_id" VIGILANCIA_PARCHES_SEGURIDAD=0 \
  VIGILANCIA_REINICIO_REQUERIDO="$TMP/reboot-required" \
  BETTERSTACK_HOST_HEARTBEAT_URL="$URL" bash "$GUION" >&- 2>/dev/null
codigo=$?
llamada=$(tail -1 "$CURL_LOG"); [ -n "$llamada" ] || llamada="ninguna"
comprobar "log no escribible y disco 99%: /fail igualmente" "$URL/fail" "$llamada"
comprobar "log no escribible y disco 99%: sale 1" "1" "$codigo"

# Y con el disco sano, un log no escribible no es motivo de alarma.
reiniciar
: > "$CURL_LOG"
env VIGILANCIA_USO_DISCO=40 VIGILANCIA_MEMINFO="$TMP/meminfo" VIGILANCIA_VMSTAT="$TMP/vmstat" \
  VIGILANCIA_ESTADO="$TMP/estado" VIGILANCIA_BOOT_ID="$TMP/boot_id" VIGILANCIA_PARCHES_SEGURIDAD=0 \
  VIGILANCIA_REINICIO_REQUERIDO="$TMP/reboot-required" \
  BETTERSTACK_HOST_HEARTBEAT_URL="$URL" bash "$GUION" >&- 2>/dev/null
codigo=$?
llamada=$(tail -1 "$CURL_LOG"); [ -n "$llamada" ] || llamada="ninguna"
comprobar "log no escribible y todo en orden: ping de exito, no /fail" "$URL" "$llamada"
comprobar "log no escribible y todo en orden: sale 0" "0" "$codigo"

# --- Estado corrupto con ceros a la izquierda: bash lo leeria en octal ("08"
# aborta, "09" se pierde). Se descarta el valor y se sigue vigilando.
reiniciar; meminfo 5; echo "08 3 aaaa-1111" > "$TMP/estado"
ejecutar 40
comprobar "estado '08 ...' y memoria baja: se lee como 8 (no octal) y alerta" "$URL/fail" "$llamada"
case "$salida" in *"en 9 lecturas seguidas"*) r=si ;; *) r="no ($salida)" ;; esac
comprobar "estado '08 ...': la cuenta sigue en base 10 (9 lecturas)" "si" "$r"

reiniciar; echo "0 09 aaaa-1111" > "$TMP/estado"; vmstat 12
ejecutar 40
comprobar "estado '0 09 ...' y oom_kill 12: los 3 OOM no se pierden" "$URL/fail" "$llamada"
case "$salida" in *"ha matado 3 proceso"*) r=si ;; *) r="no ($salida)" ;; esac
comprobar "estado '0 09 ...': cuenta 3, en base 10" "si" "$r"

# --- Umbral de 20 cifras: desborda `[ -lt ]` y pasaba la validacion.
reiniciar
ejecutar 99 UMBRAL_DISCO=99999999999999999999
comprobar "umbral de 20 cifras: configuracion invalida (2)" "2" "$codigo"

# --- boot_id previo ilegible ("-") no es un reinicio.
reiniciar; echo "0 3 -" > "$TMP/estado"
ejecutar 40
comprobar "boot_id previo '-' y oom_kill estable: sin alerta" "$URL" "$llamada"

# --- Configuracion invalida
reiniciar
ejecutar 40 UMBRAL_DISCO=abc
comprobar "umbral no numerico: sale 2" "2" "$codigo"
comprobar "umbral no numerico: la vigilancia rota avisa con /fail" "$URL/fail" "$llamada"

# --- Lecturas ilegibles: nunca un final mudo.
reiniciar; rm -f "$TMP/vmstat"
ejecutar 40
comprobar "sin /proc/vmstat (kernel antiguo): no aborta, sale 0" "0" "$codigo"
comprobar "sin /proc/vmstat: ping de exito" "$URL" "$llamada"
case "$salida" in *"oom_kill=n/d"*) r=si ;; *) r="no ($salida)" ;; esac
comprobar "sin /proc/vmstat: lo declara n/d" "si" "$r"

reiniciar; rm -f "$TMP/meminfo"
ejecutar 40
comprobar "sin /proc/meminfo: alerta" "$URL/fail" "$llamada"
case "$salida" in *"memoria ilegible"*) r=si ;; *) r="no ($salida)" ;; esac
comprobar "sin /proc/meminfo: lo dice" "si" "$r"

reiniciar
ejecutar "ilegible"
comprobar "disco ilegible (lectura no numerica): alerta" "$URL/fail" "$llamada"

# --- 4. Parches de seguridad pendientes (umbral 3 dias por defecto).
reiniciar
ejecutar 40 VIGILANCIA_AHORA="$BASE" VIGILANCIA_PARCHES_SEGURIDAD=0
comprobar "sin parches pendientes: sin alerta" "$URL" "$llamada"
case "$salida" in *"parches_seguridad=0 reinicio_pendiente=no"*) r=si ;; *) r="no ($salida)" ;; esac
comprobar "sin parches: la linea lo dice" "si" "$r"

reiniciar
ejecutar 40 VIGILANCIA_AHORA="$BASE" VIGILANCIA_PARCHES_SEGURIDAD=5
comprobar "5 parches vistos por primera vez: sin alerta" "$URL" "$llamada"
ejecutar 40 VIGILANCIA_AHORA="$((BASE + 3 * DIA - 60))" VIGILANCIA_PARCHES_SEGURIDAD=5
comprobar "parches pendientes desde hace casi 3 dias: sin alerta" "$URL" "$llamada"
ejecutar 40 VIGILANCIA_AHORA="$((BASE + 3 * DIA))" VIGILANCIA_PARCHES_SEGURIDAD=4
comprobar "parches pendientes desde hace 3 dias: alerta" "$URL/fail" "$llamada"
case "$salida" in *"parches de seguridad pendientes (4) desde hace 3 dias >= 3"*) r=si ;; *) r="no ($salida)" ;; esac
comprobar "parches: el motivo nombra recuento, dias y umbral" "si" "$r"
ejecutar 40 VIGILANCIA_AHORA="$((BASE + 3 * DIA + 300))" VIGILANCIA_PARCHES_SEGURIDAD=0
comprobar "parches instalados (recuento 0): fin de la alerta" "$URL" "$llamada"
ejecutar 40 VIGILANCIA_AHORA="$((BASE + 4 * DIA))" VIGILANCIA_PARCHES_SEGURIDAD=2
ejecutar 40 VIGILANCIA_AHORA="$((BASE + 6 * DIA))" VIGILANCIA_PARCHES_SEGURIDAD=2
comprobar "tras volver a 0, la cuenta empieza de nuevo (2 dias): sin alerta" "$URL" "$llamada"

reiniciar
ejecutar 40 VIGILANCIA_AHORA="$BASE" VIGILANCIA_PARCHES_SEGURIDAD=1 UMBRAL_DIAS_PARCHES=1
ejecutar 40 VIGILANCIA_AHORA="$((BASE + DIA))" VIGILANCIA_PARCHES_SEGURIDAD=1 UMBRAL_DIAS_PARCHES=1
comprobar "UMBRAL_DIAS_PARCHES=1: alerta al dia" "$URL/fail" "$llamada"

# Recuento ilegible: por si solo no alerta, pero no borra la fecha de partida.
reiniciar
ejecutar 40 VIGILANCIA_AHORA="$BASE" VIGILANCIA_PARCHES_SEGURIDAD=
comprobar "recuento ilegible sin historial: sin alerta" "$URL" "$llamada"
case "$salida" in *"parches_seguridad=n/d"*) r=si ;; *) r="no ($salida)" ;; esac
comprobar "recuento ilegible: lo declara n/d" "si" "$r"
reiniciar
ejecutar 40 VIGILANCIA_AHORA="$BASE" VIGILANCIA_PARCHES_SEGURIDAD=3
ejecutar 40 VIGILANCIA_AHORA="$((BASE + DIA))" VIGILANCIA_PARCHES_SEGURIDAD=
ejecutar 40 VIGILANCIA_AHORA="$((BASE + 3 * DIA))" VIGILANCIA_PARCHES_SEGURIDAD=
comprobar "pendientes y luego ilegible: la fecha sigue contando y alerta" "$URL/fail" "$llamada"

# Estado de la version anterior (tres campos): se empieza a contar, sin abortar.
reiniciar; echo "0 3 aaaa-1111" > "$TMP/estado"
ejecutar 40 VIGILANCIA_AHORA="$BASE" VIGILANCIA_PARCHES_SEGURIDAD=7
comprobar "estado de tres campos y parches pendientes: sin alerta" "$URL" "$llamada"
comprobar "estado de tres campos: se reescribe con cinco" "0 3 aaaa-1111 $BASE -" "$(cat "$TMP/estado")"

# Una fecha de partida en el futuro (reloj corregido) no retrasa el aviso.
reiniciar; echo "0 3 aaaa-1111 $((BASE + 30 * DIA)) -" > "$TMP/estado"
ejecutar 40 VIGILANCIA_AHORA="$BASE" VIGILANCIA_PARCHES_SEGURIDAD=1
comprobar "fecha de partida futura: se descarta y se cuenta desde ahora" "0 3 aaaa-1111 $BASE -" "$(cat "$TMP/estado")"

reiniciar
ejecutar 40 UMBRAL_DIAS_PARCHES=0
comprobar "UMBRAL_DIAS_PARCHES=0: configuracion invalida (2)" "2" "$codigo"

# --- 5. Reinicio pendiente (reboot-required).
reiniciar; touch -d "@$BASE" "$TMP/reboot-required"
ejecutar 40 VIGILANCIA_AHORA="$BASE"
comprobar "reboot-required recien aparecido: sin alerta" "$URL" "$llamada"
case "$salida" in *"reinicio_pendiente=si"*) r=si ;; *) r="no ($salida)" ;; esac
comprobar "reboot-required: la linea lo dice" "si" "$r"
# Que un paquete posterior lo toque no retrasa la cuenta: manda la primera vez vista.
touch -d "@$((BASE + 2 * DIA))" "$TMP/reboot-required"
ejecutar 40 VIGILANCIA_AHORA="$((BASE + 3 * DIA))"
comprobar "reboot-required desde hace 3 dias: alerta" "$URL/fail" "$llamada"
case "$salida" in *"reinicio pendiente (reboot-required) desde hace 3 dias >= 3"*) r=si ;; *) r="no ($salida)" ;; esac
comprobar "reinicio: el motivo nombra dias y umbral" "si" "$r"
rm -f "$TMP/reboot-required"
ejecutar 40 VIGILANCIA_AHORA="$((BASE + 3 * DIA + 300))"
comprobar "tras reiniciar (sin reboot-required): sin alerta" "$URL" "$llamada"

# Sin estado previo, la fecha del fichero cuenta: uno de hace 5 dias alerta ya.
reiniciar; touch -d "@$((BASE - 5 * DIA))" "$TMP/reboot-required"
ejecutar 40 VIGILANCIA_AHORA="$BASE"
comprobar "reboot-required de hace 5 dias sin estado: alerta a la primera" "$URL/fail" "$llamada"

reiniciar; touch -d "@$BASE" "$TMP/reboot-required"
ejecutar 40 VIGILANCIA_AHORA="$BASE" UMBRAL_DIAS_REINICIO=1
ejecutar 40 VIGILANCIA_AHORA="$((BASE + DIA - 1))" UMBRAL_DIAS_REINICIO=1
comprobar "UMBRAL_DIAS_REINICIO=1 a las 23:59: sin alerta" "$URL" "$llamada"

echo
if [ "$fallos" -gt 0 ]; then
  echo "vigilar-host.tests.sh: $fallos fallo(s)."
  exit 1
fi
echo "vigilar-host.tests.sh: todo en orden."
