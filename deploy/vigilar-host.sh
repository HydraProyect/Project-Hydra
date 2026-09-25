#!/bin/bash
# Vigilancia del host del VPS: disco y memoria. Cron del host, cada 5 minutos.
#
# Nace de dos huecos medidos, no supuestos:
#   - Disco: el 2026-08-26, el 2026-08-29 y el 2026-09-13 el disco llego al
#     100% (cache de build primero, 88 imagenes/26 GB despues) y tumbo el
#     servicio sin que nadie lo supiera hasta verlo caido. liberar-disco.sh
#     solo mira el disco al DESPLEGAR; entre despliegues no mira nadie.
#   - Memoria (REC-196): el VPS no tiene swap y el OOM killer del kernel elige
#     victima por tamano, no por importancia (el 2026-09-04 mato systemd).
#     Staging y produccion comparten esta maquina y compiten en igualdad.
#
# Un unico host, un unico cron: staging y produccion viven en la misma maquina,
# asi que este guion vigila las dos a la vez.
#
# Como avisa: reutiliza el mecanismo ya en uso para el backup
# (scripts/backup-borg.sh), un monitor Heartbeat de Better Stack:
#   - todo en orden      -> ping a <url>         (el monitor sigue arriba)
#   - umbral superado    -> ping a <url>/fail    (incidente inmediato)
#   - host caido o cron  -> ningun ping          (incidente al vencer la ventana)
# La ultima fila es la que no se puede perder: un host sin memoria o sin disco
# puede no llegar a ejecutar este guion, y la AUSENCIA del ping lo cubre.
#
# Condiciones de alerta (cada una basta):
#   1. Disco de / al UMBRAL_DISCO% o mas (defecto 85). Por encima del 75 con el
#      que poda liberar-disco.sh y por debajo del 95 critico con el que corta
#      el despliegue: deja margen para actuar antes de que PostgreSQL deje de
#      poder escribir.
#   2. Memoria disponible (MemAvailable) por debajo del UMBRAL_MEMORIA% del total
#      (defecto 10) en MUESTRAS_MEMORIA lecturas seguidas (defecto 2, es decir
#      10 minutos con el cron de 5): un pico de una sola lectura no despierta a
#      nadie; uno que llega a matar algo lo recoge la condicion 3.
#   3. El contador oom_kill de /proc/vmstat ha subido desde la ultima lectura:
#      el kernel ya ha matado un proceso por falta de memoria, sea cual sea la
#      memoria disponible en este instante. La primera ejecucion (sin estado
#      previo) solo toma la referencia y no puede avisar de lo anterior.
#
# Uso (cron del host; ver ## Paso operativo en servidores de la PR):
#   */5 * * * *  set -a; . /etc/caemanager-vigilancia.env; set +a; /opt/talveg/deploy/vigilar-host.sh >> /var/log/caemanager-vigilancia.log 2>&1
#
# Variables (todas opcionales):
#   BETTERSTACK_HOST_HEARTBEAT_URL  URL del monitor Heartbeat. Sin ella no hay
#                                   llamada de red: el guion solo informa y
#                                   devuelve el codigo de salida.
#   UMBRAL_DISCO, UMBRAL_MEMORIA, MUESTRAS_MEMORIA   ver arriba.
#   VIGILANCIA_ESTADO               fichero de estado entre ejecuciones
#                                   (defecto /var/lib/caemanager-vigilancia.estado).
# Para probarlo sin VPS: VIGILANCIA_USO_DISCO (porcentaje forzado),
# VIGILANCIA_MEMINFO y VIGILANCIA_VMSTAT (rutas a ficheros con el formato de
# /proc/meminfo y /proc/vmstat). Tests en vigilar-host.tests.sh.
#
# Codigos de salida: 0 en orden, 1 alerta, 2 configuracion invalida. Cualquier
# salida distinta de 0 que no sea una alerta ya avisada (incluido un aborto con
# codigo 1) avisa con /fail (ver al_salir).

set -euo pipefail

UMBRAL_DISCO="${UMBRAL_DISCO:-85}"
UMBRAL_MEMORIA="${UMBRAL_MEMORIA:-10}"
MUESTRAS_MEMORIA="${MUESTRAS_MEMORIA:-2}"
ESTADO="${VIGILANCIA_ESTADO:-/var/lib/caemanager-vigilancia.estado}"
MEMINFO="${VIGILANCIA_MEMINFO:-/proc/meminfo}"
VMSTAT="${VIGILANCIA_VMSTAT:-/proc/vmstat}"
BOOT_ID="${VIGILANCIA_BOOT_ID:-/proc/sys/kernel/random/boot_id}"

# Entero decimal canonico: sin ceros a la izquierda (bash leeria "08" como
# octal y abortaria) y acotado (un valor de 20 cifras desborda `[ -lt ]`, que
# devuelve 2 y dejaria pasar la validacion).
NUM='^(0|[1-9][0-9]{0,17})$'
avisado=0

# La URL del heartbeat es un secreto (quien la conozca puede fingir que el host
# esta sano): va por stdin con `curl -K -`, no como argumento, para que no salga
# en `ps` ni en /proc/<pid>/cmdline. Mismo patron que backup-borg.sh.
llamar_heartbeat() {   # llamar_heartbeat URL
  printf 'url = "%s"\n' "$1" | curl -fsS -m 10 --retry 2 --retry-delay 3 -K - >/dev/null
}

# Un guion de vigilancia que muere a medias (configuracion invalida, un comando
# que falla con `set -e`, un log que no se puede escribir con el disco lleno)
# no puede quedarse callado: toda salida distinta de 0 avisa con /fail, salvo
# la alerta que el propio guion ya intento avisar (avisado=1). No se deduce del
# codigo: un aborto de `set -e` tambien sale con 1.
al_salir() {
  local codigo=$?
  if [ "$codigo" -ne 0 ] && [ "$avisado" != "1" ] && [ -n "${BETTERSTACK_HOST_HEARTBEAT_URL:-}" ]; then
    llamar_heartbeat "${BETTERSTACK_HOST_HEARTBEAT_URL%/}/fail" 2>/dev/null \
      || echo "AVISO: la vigilancia fallo (codigo $codigo) y tampoco se pudo avisar al heartbeat."
  fi
  return "$codigo"
}
trap al_salir EXIT

entero_en_rango() {   # entero_en_rango NOMBRE VALOR MIN MAX
  if ! [[ "$2" =~ $NUM ]] || [ "$2" -lt "$3" ] || [ "$2" -gt "$4" ]; then
    echo "Configuracion invalida: $1='$2' (se espera un entero entre $3 y $4)." >&2
    exit 2
  fi
}
entero_en_rango UMBRAL_DISCO "$UMBRAL_DISCO" 1 99
entero_en_rango UMBRAL_MEMORIA "$UMBRAL_MEMORIA" 1 99
entero_en_rango MUESTRAS_MEMORIA "$MUESTRAS_MEMORIA" 1 100

# Las lecturas no abortan: un valor ilegible se convierte en motivo de alerta
# (o en hueco declarado, para oom_kill) mas abajo, nunca en un final mudo.
uso_disco() {
  if [ -n "${VIGILANCIA_USO_DISCO:-}" ]; then
    echo "$VIGILANCIA_USO_DISCO"
  else
    { df --output=pcent / 2>/dev/null || true; } | tail -1 | tr -dc '0-9'
  fi
}

# Temporal + mv: un disco lleno o un corte a mitad nunca deja el estado vacio
# (que haria tomar la referencia de oom_kill en silencio).
guardar_estado() {   # guardar_estado BAJAS OOM ARRANQUE
  local tmp="$ESTADO.tmp.$$"
  { printf '%s %s %s\n' "$1" "$2" "$3" > "$tmp" && mv -f "$tmp" "$ESTADO"; } 2>/dev/null \
    || { rm -f "$tmp" 2>/dev/null; return 1; }
}

campo() {   # campo FICHERO CLAVE -> primer numero de la linea que empieza por CLAVE
  awk -v k="$2" '$1 == k || $1 == k":" { print $2; exit }' "$1" 2>/dev/null || true
}

marca="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
motivos=()

# 1. Disco
disco=$(uso_disco)
if ! [[ "$disco" =~ $NUM ]]; then
  motivos+=("disco ilegible")
elif [ "$disco" -ge "$UMBRAL_DISCO" ]; then
  motivos+=("disco ${disco}% >= ${UMBRAL_DISCO}%")
fi

# 2. Memoria disponible
total=$(campo "$MEMINFO" MemTotal)
disponible=$(campo "$MEMINFO" MemAvailable)
bajas_previas=0
oom_previo=""
arranque_previo=""
if [ -r "$ESTADO" ]; then
  # Formato: "<muestras bajas seguidas> <oom_kill|-> <boot_id|->"; se lee sin `source`.
  read -r bajas_previas oom_previo arranque_previo < "$ESTADO" || true
  # Un estado editado a mano o corrupto puede traer ceros a la izquierda: se
  # normaliza en base 10 (10#) en vez de descartarlo, porque descartar la
  # referencia de oom_kill haria perder los OOM ocurridos desde entonces.
  if [[ "$bajas_previas" =~ ^[0-9]{1,18}$ ]]; then bajas_previas=$(( 10#$bajas_previas )); else bajas_previas=0; fi
  if [[ "$oom_previo" =~ ^[0-9]{1,18}$ ]]; then oom_previo=$(( 10#$oom_previo )); else oom_previo=""; fi
fi
arranque=$({ tr -dc '0-9a-f-' < "$BOOT_ID"; } 2>/dev/null || true)
[ -n "$arranque" ] || arranque="-"
if ! [[ "${total:-}" =~ $NUM && "${disponible:-}" =~ $NUM ]] || [ "$total" -eq 0 ]; then
  motivos+=("memoria ilegible")
  pct_mem="?"
  bajas=0
else
  pct_mem=$(( disponible * 100 / total ))
  if [ "$pct_mem" -lt "$UMBRAL_MEMORIA" ]; then
    bajas=$(( bajas_previas + 1 ))
  else
    bajas=0
  fi
  if [ "$bajas" -ge "$MUESTRAS_MEMORIA" ]; then
    motivos+=("memoria disponible ${pct_mem}% < ${UMBRAL_MEMORIA}% en ${bajas} lecturas seguidas")
  fi
fi

# 3. OOM killer
oom=$(campo "$VMSTAT" oom_kill)
alerta_oom=0
if ! [[ "${oom:-}" =~ $NUM ]]; then
  # Kernels < 4.13 no exponen oom_kill: no es una alerta, es un hueco declarado.
  oom=""
elif [ -n "$oom_previo" ]; then
  # oom_kill cuenta desde el arranque del kernel: tras un reinicio vuelve a 0
  # y compararlo con la referencia del arranque anterior ocultaria los OOM del
  # nuevo. Con otro boot_id (o un contador que ha bajado) la referencia es 0.
  if { [ "$arranque" != "-" ] && [ -n "$arranque_previo" ] && [ "$arranque_previo" != "-" ] && [ "$arranque" != "$arranque_previo" ]; } \
     || [ "$oom" -lt "$oom_previo" ]; then
    oom_previo=0
  fi
fi
if [ -n "$oom" ] && [ -n "$oom_previo" ] && [ "$oom" -gt "$oom_previo" ]; then
  motivos+=("el OOM killer ha matado $(( oom - oom_previo )) proceso(s)")
  alerta_oom=1
fi

# Guardar estado. Si no se puede escribir, se avisa: sin estado las condiciones
# 2 y 3 quedan ciegas, y eso tambien es un fallo de la vigilancia.
if ! guardar_estado "$bajas" "${oom:--}" "$arranque"; then
  motivos+=("no se pudo escribir el estado en $ESTADO")
fi

datos="disco=${disco}% memoria_disponible=${pct_mem}% oom_kill=${oom:-n/d}"
if [ "${#motivos[@]}" -eq 0 ]; then
  linea="$marca ok $datos"
  codigo=0
  destino="${BETTERSTACK_HOST_HEARTBEAT_URL:-}"
else
  resumen=$(IFS=';'; echo "${motivos[*]}")
  linea="$marca ALERTA $resumen ($datos)"
  codigo=1
  destino=""
  [ -n "${BETTERSTACK_HOST_HEARTBEAT_URL:-}" ] && destino="${BETTERSTACK_HOST_HEARTBEAT_URL%/}/fail"
fi

# El aviso va ANTES que el log y el log nunca aborta: con el disco lleno, el
# `>>` del cron falla, y un `echo` fallido bajo `set -e` no puede costar el aviso.
if [ -n "$destino" ]; then
  if llamar_heartbeat "$destino" 2>/dev/null; then
    echo "$linea" || true
  else
    echo "$linea" || true
    echo "$marca AVISO no se pudo llamar al heartbeat: se reintenta en la siguiente ejecucion." || true
    # Disco y memoria se vuelven a medir y siguen en alerta mientras dure la
    # condicion; el OOM es un suceso y se perderia: la referencia no avanza
    # hasta que el aviso se entregue, para que la siguiente lectura lo repita.
    if [ "$alerta_oom" = "1" ]; then
      guardar_estado "$bajas" "$oom_previo" "$arranque" || true
    fi
  fi
else
  echo "$linea" || true
fi

# La alerta ya se intento avisar (o no hay URL): al_salir no debe repetirla.
[ "$codigo" -eq 1 ] && avisado=1
exit "$codigo"
