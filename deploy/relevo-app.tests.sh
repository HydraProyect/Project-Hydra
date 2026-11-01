#!/bin/bash
# Tests de deploy/relevo-app.sh (P1-F2): relevo sin corte de la aplicación.
#
# `docker`, `systemd-run`, `systemctl` y `flock` son dobles. El estado del VPS
# vive en ficheros bajo $ESTADO: un directorio por contenedor (imagen, en
# marcha, id, conexiones), los montajes de Caddy, y los servicios sin perfil de
# cada compose. Cada llamada se anota en $LOG; cada recarga de Caddy anota
# además lo que había en los ficheros de ranuras en ese instante, que es lo que
# Caddy habría servido. Corren en CI sin VPS ni Docker.
#
# Lo que el comportamiento real de Caddy hace con esos ficheros (afinidad por
# cookie, stream_close_delay, respaldo a la activa) no se puede observar aquí:
# se midió con Caddy real (cabecera del Caddyfile). Aquí se comprueba el
# contrato entre este guion, el Caddyfile y los compose: mismos nombres de
# fichero, de montaje y de ranura, y el drenaje máximo por debajo de
# stream_close_delay.
#
# Al final, mutaciones sobre una COPIA del guion: cada una tiene que cambiar el
# fichero y hacer que su caso dé el resultado equivocado.

set -uo pipefail

AQUI="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
GUION="${RELEVO_GUION:-$AQUI/relevo-app.sh}"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
mkdir -p "$TMP/bin"

A=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
B=bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb
C=cccccccccccccccccccccccccccccccccccccccc

cat > "$TMP/bin/docker" <<'EOF'
#!/bin/bash
# Doble de docker sobre $ESTADO.
echo "docker $* IMAGEN_TAG=${IMAGEN_TAG:-}" >> "$LOG"
c() { echo "$ESTADO/c/$1"; }
nuevo_id() { n=$(( $(cat "$ESTADO/ids" 2>/dev/null || echo 0) + 1 )); echo "$n" > "$ESTADO/ids"; echo "id$n"; }
# REINICIA: el contenedor vuelve a la vida tras la siguiente lectura de su estado,
# salvo que `docker update --restart=no` le haya quitado la política.
reinicia_si_toca() { [ "${REINICIA:-}" = "$1" ] && [ ! -f "$(c "$1")/sin_reinicio" ] && : > "$(c "$1")/reinicia"; }
case "$1" in
  inspect)
    if [ "$2" = "-f" ]; then fmt=$3; nombre=$4; else fmt=""; nombre=$2; fi
    case "$fmt" in
      *guarda-correo*)   # etiqueta de imagen o contenedor; por defecto, declarada
        [ "${INSPECT_GUARDA_FALLA:-0}" = 1 ] && exit 1
        if [ -f "$ESTADO/sin-guarda/${nombre#caemanager:}" ]; then echo "<no value>"; else echo 1; fi
        exit 0 ;;
    esac
    [ -d "$(c "$nombre")" ] || { echo "Error: No such object: $nombre" >&2; exit 1; }
    case "$fmt" in
      *State.Running*)
        [ -f "$(c "$nombre")/estado_perdido" ] && exit 1
        cat "$(c "$nombre")/en_marcha"
        [ -f "$(c "$nombre")/reinicia" ] && { rm "$(c "$nombre")/reinicia"; echo true > "$(c "$nombre")/en_marcha"; } ;;
      *Health*)
        [ "${INSPECT_SALUD_FALLA:-}" = "$nombre" ] && exit 1
        cat "$(c "$nombre")/salud" 2>/dev/null || echo healthy ;;
      *.Id*) cat "$(c "$nombre")/id" ;;
      *Config.Image*) cat "$(c "$nombre")/imagen" ;;
      *Mounts*) cat "$(c "$nombre")/montajes" 2>/dev/null; echo ;;
      "") echo "{}" ;;
    esac ;;
  run)   # Caddy desechable que valida el Caddyfile antes de recrear el de verdad
    cat > /dev/null
    [ "${VALIDA_FALLA:-0}" = 1 ] && exit 1
    exit 0 ;;
  exec)
    shift; [ "$1" = "-i" ] && shift
    nombre=$1; shift
    case "$*" in
      "printenv"*) echo "valor-de-$2" ;;
      "caddy reload"*)
        case "$*" in
          *"/dev/stdin"*) marca="$(grep -o 'MARCA_[A-Z]*' | head -1)" ;;
          *) marca="CONTENEDOR" ;;   # recarga con el Caddyfile montado en el propio Caddy
        esac
        echo "FUENTE ${marca:-?}" >> "$LOG"
        [ "${RECARGA_FALLA:-0}" = 1 ] && exit 1
        { printf 'RECARGA produccion=['; grep -h '^to ' "$DIR_RANURAS/produccion.caddy" 2>/dev/null | tr -d '\n'
          printf '] staging=['; grep -h '^to ' "$DIR_RANURAS/staging.caddy" 2>/dev/null | tr -d '\n'; echo ']'; } >> "$LOG" ;;
      "cat /proc/net/tcp"*)
        [ -d "$(c "$nombre")" ] || exit 1
        f="$(c "$nombre")/conexiones"
        # Una lectura por llamada: cada línea del fichero es el número de
        # conexiones de una vuelta; la última se repite.
        n=$(head -1 "$f" 2>/dev/null || echo 0); [ "$(wc -l < "$f" 2>/dev/null || echo 0)" -gt 1 ] && sed -i 1d "$f"
        echo "  sl  local_address rem_address   st"
        for (( i = 0; i < n; i++ )); do echo "   $i: 0A00000B:1F90 0A000005:D$(printf '%03d' "$i") 01 0"; done
        echo "   9: 0100007F:1F90 0100007F:C000 01 0"   # el healthcheck: loopback, no cuenta
        echo "  10: 0A00000B:1F90 0A000005:E000 06 0" ;; # TIME_WAIT: no cuenta
    esac ;;
  compose)
    case "$*" in
      *" config --services"*)
        case "$*" in *staging*) printf 'migrador\ndb\nseq\n' ;; *) printf 'migrador\ndb\ncaddy\nseq\n' ;; esac ;;
      *" config")   # configuración efectiva: la forma real la mide compose-techos-memoria.tests.sh
        if [ "${CONFIG_FALLA:-0}" = 1 ]; then echo "línea ilegible: SECRETO_CENTINELA" >&2; exit 1; fi
        predeterminada='      EnvioCorreo__Modo: Abierto'
        case "$*" in *staging*) predeterminada='      EnvioCorreo__Modo: Cerrado' ;; esac
        sed '/^$/d' <<YAML
name: caemanager
services:
  app-azul:
    container_name: caemanager-app-azul
    environment:
      ASPNETCORE_URLS: http://+:8080
      Clave__Secreta: SECRETO_CENTINELA
${CORREO_AZUL-$predeterminada}
    image: caemanager:x
  app-verde:
    container_name: caemanager-app-verde
    environment:
      ASPNETCORE_URLS: http://+:8080
      Clave__Secreta: SECRETO_CENTINELA
${CORREO_VERDE-$predeterminada}
    image: caemanager:x
  migrador:
    environment:
      EnvioCorreo__Modo: Abierto
networks:
  app-verde:
    environment:
      EnvioCorreo__Modo: Abierto
YAML
        ;;
      *" up "*" caddy")   # transición: recrear Caddy con el montaje
        mkdir -p "$(c caemanager-caddy)"; echo "/etc/caddy/Caddyfile /etc/caddy/ranuras /data" > "$(c caemanager-caddy)/montajes" ;;
      *" up "*)
        [ "${COMPOSE_FALLA:-0}" = 1 ] && exit 1
        todo="$*"; ranura="${todo##* app-}"; pref=caemanager-app; case "$*" in *staging*) pref=caemanager-staging-app ;; esac
        d="$(c "$pref-$ranura")"; mkdir -p "$d"; echo true > "$d/en_marcha"; nuevo_id > "$d/id"
        if [ "${COMPOSE_NO_CAMBIA:-0}" = 1 ]; then echo "caemanager:otra" > "$d/imagen"; else echo "caemanager:$IMAGEN_TAG" > "$d/imagen"; fi ;;
    esac ;;
  stop)
    nombre="${*: -1}"
    [ "${STOP_CUELGA:-}" = "$nombre" ] && sleep 30
    [ "${STOP_FALLA:-}" = "$nombre" ] && exit 1
    [ "${STOP_NO_PARA:-}" = "$nombre" ] && exit 0   # sale con 0 y el contenedor sigue vivo
    [ "${STOP_PIERDE_ESTADO:-}" = "$nombre" ] && { : > "$(c "$nombre")/estado_perdido"; exit 0; }
    [ "${STOP_ELIMINA:-}" = "$nombre" ] && { rm -rf "$(c "$nombre")"; exit 0; }
    reinicia_si_toca "$nombre"
    [ -d "$(c "$nombre")" ] && echo false > "$(c "$nombre")/en_marcha" ;;
  kill)
    nombre="${*: -1}"
    [ "${KILL_FALLA:-}" = "$nombre" ] && exit 1
    reinicia_si_toca "$nombre"
    [ -d "$(c "$nombre")" ] && echo false > "$(c "$nombre")/en_marcha" ;;
  update)
    nombre="${*: -1}"
    [ "${UPDATE_FALLA:-}" = "$nombre" ] && exit 1
    [ -d "$(c "$nombre")" ] && : > "$(c "$nombre")/sin_reinicio" ;;
  rm) rm -rf "$(c "$2")" ;;
  logs) ;;
esac
exit 0
EOF
cat > "$TMP/bin/systemd-run" <<'EOF'
#!/bin/bash
echo "systemd-run $*" >> "$LOG"
[ "${SYSTEMD_FALLA:-0}" = 1 ] && exit 1
exit 0
EOF
cat > "$TMP/bin/systemctl" <<'EOF'
#!/bin/bash
echo "systemctl $*" >> "$LOG"
EOF
cat > "$TMP/bin/flock" <<'EOF'
#!/bin/bash
echo "flock $*" >> "$LOG"
[ "${FLOCK_FALLA:-0}" = 1 ] && exit 1
exit 0
EOF
chmod +x "$TMP/bin/"*
export PATH="$TMP/bin:$PATH" LOG="$TMP/log" ESTADO="$TMP/estado"
export RAIZ_DESPLIEGUE="$TMP/raiz" DIR_RANURAS="$TMP/ranuras" DRENAJE_INTERVALO=0 RELEVO_ESPERA_PARADA=0
mkdir -p "$RAIZ_DESPLIEGUE/deploy/local"
{ cat "$AQUI/local/Caddyfile"; echo "# MARCA_CHECKOUT"; } > "$RAIZ_DESPLIEGUE/deploy/local/Caddyfile"

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
contiene() { case "$2" in *"$1"*) echo si ;; *) echo "no: $2" ;; esac; }
ranuras() { grep -h '^to ' "$DIR_RANURAS/$1.caddy" 2>/dev/null || echo "<sin fichero>"; }
log() { cat "$LOG"; }
# contenedor NOMBRE IMAGEN [en_marcha] [conexiones...]
contenedor() {
  local d="$ESTADO/c/$1"; mkdir -p "$d"
  echo "caemanager:$2" > "$d/imagen"; echo "${3:-true}" > "$d/en_marcha"; echo "id-$1" > "$d/id"
  shift 3 2>/dev/null || shift $#
  printf '%s\n' "${@:-0}" > "$d/conexiones"
}
# Estado limpio: Caddy ya con el montaje, sin ficheros de ranuras.
limpio() {
  rm -rf "$ESTADO" "$DIR_RANURAS"; mkdir -p "$ESTADO/c/caemanager-caddy"
  echo "/etc/caddy/Caddyfile /etc/caddy/ranuras /data" > "$ESTADO/c/caemanager-caddy/montajes"
  echo true > "$ESTADO/c/caemanager-caddy/en_marcha"; echo id-caddy > "$ESTADO/c/caemanager-caddy/id"
  : > "$LOG"
}
fichero() { mkdir -p "$DIR_RANURAS"; printf '# prueba\n%s\n' "$2" > "$DIR_RANURAS/$1.caddy"; }
# sin_guarda SHA|CONTENEDOR: esa imagen o ese contenedor no lleva la etiqueta.
sin_guarda() { mkdir -p "$ESTADO/sin-guarda"; : > "$ESTADO/sin-guarda/$1"; }
relevo() { # [VAR=valor ...] -- ARGS
  local vars=()
  while [ "$1" != "--" ]; do vars+=("$1"); shift; done; shift
  # timeout: un drenaje que no puede retirar (activa no sana) sigue en bucle a
  # propósito; en un test eso no puede colgar la suite.
  salida=$(timeout 20 env "${vars[@]}" bash "$GUION" "$@" 2>&1); codigo=$?
}
en_marcha() { cat "$ESTADO/c/$1/en_marcha" 2>/dev/null || echo "<no existe>"; }

echo "contrato con el Caddyfile y los compose"
CADDYFILE="$AQUI/local/Caddyfile"
for e in produccion staging; do
  comprobar "el Caddyfile importa el fichero de ranuras de $e" 1 "$(grep -cx "		import /etc/caddy/ranuras/$e.caddy" "$CADDYFILE")"
done
comprobar "el compose de producción monta DIR_RANURAS por defecto en /etc/caddy/ranuras" 1 \
  "$(grep -cx '      - /var/lib/talveg/caddy-ranuras:/etc/caddy/ranuras' "$AQUI/local/docker-compose.produccion.yml")"
comprobar "Caddy arranca con el Caddyfile aprobado si existe (no --resume, no el del checkout)" "1 0" \
  "$(grep -c 'c=/etc/caddy/Caddyfile; \[ -f /etc/caddy/ranuras/Caddyfile.aprobado \] && c=/etc/caddy/ranuras/Caddyfile.aprobado;' "$AQUI/local/docker-compose.produccion.yml") $(grep -v '^[[:space:]]*#' "$AQUI/local/docker-compose.produccion.yml" | grep -c -- '--resume')"
comprobar "el aprobado se guarda en el directorio que Caddy monta" 1 \
  "$(grep -cx 'caddyfile_aprobado() { echo "$DIR_RANURAS/Caddyfile.aprobado"; }' "$GUION")"
comprobar "Caddy crea al arrancar los ficheros que falten, con los contenedores anteriores a P1-F2" "1 1" \
  "$(grep -c "\[ -f /etc/caddy/ranuras/produccion.caddy \] || echo 'to caemanager-app:8080' > /etc/caddy/ranuras/produccion.caddy;" "$AQUI/local/docker-compose.produccion.yml") $(grep -c "\[ -f /etc/caddy/ranuras/staging.caddy \] || echo 'to caemanager-staging-app:8080' > /etc/caddy/ranuras/staging.caddy;" "$AQUI/local/docker-compose.produccion.yml")"
comprobar "el valor por defecto de DIR_RANURAS es ese directorio" 1 \
  "$(grep -cx 'DIR_RANURAS="${DIR_RANURAS:-/var/lib/talveg/caddy-ranuras}"' "$GUION")"
cierre="$(sed -n 's/^\tstream_close_delay \([0-9]*\)m$/\1/p; s/^\tstream_close_delay \([0-9]*\)h$/\1*60/p' "$CADDYFILE")"; cierre="$(( ${cierre:-0} ))"
max_prod="$(sed -n 's/^DRENAJE_MAX_PRODUCCION="${DRENAJE_MAX_PRODUCCION:-\([0-9]*\)}"$/\1/p' "$GUION")"
max_stg="$(sed -n 's/^DRENAJE_MAX_STAGING="${DRENAJE_MAX_STAGING:-\([0-9]*\)}"$/\1/p' "$GUION")"
comprobar "stream_close_delay es de al menos 12 h (cada recarga fuerza una reconexión al vencer): ${cierre:-?} min" si "$([ "$cierre" -ge 720 ] && echo si || echo no)"
comprobar "stream_close_delay (${cierre:-?} min) supera los drenajes máximos (${max_prod:-?} s, ${max_stg:-?} s)" si \
  "$([ -n "$cierre" ] && [ -n "$max_prod" ] && [ -n "$max_stg" ] && [ $(( cierre * 60 )) -gt "$max_prod" ] && [ $(( cierre * 60 )) -gt "$max_stg" ] && echo si || echo no)"
comprobar "el Caddyfile fija la afinidad por cookie con respaldo a la primera" 1 "$(grep -cx '	lb_policy cookie talveg_ranura {' "$CADDYFILE")"
comprobar "  y fallback first" 1 "$(grep -cx '		fallback first' "$CADDYFILE")"
for f in docker-compose.produccion.yml docker-compose.staging.yml; do
  pref=caemanager-app; [ "$f" = docker-compose.staging.yml ] && pref=caemanager-staging-app
  comprobar "$f: app-azul es $pref-azul, y las dos ranuras llevan perfil ranura" "1 2" \
    "$(grep -cx "    container_name: $pref-azul" "$AQUI/local/$f") $(grep -cx '    profiles: \["ranura"\]' "$AQUI/local/$f")"
  comprobar "$f: app-verde extiende app-azul como $pref-verde" 1 \
    "$(grep -A3 -x '  app-verde:' "$AQUI/local/$f" | grep -c "container_name: $pref-verde")"
  verde="$(sed -n '/^  app-verde:$/,/^  [a-z]/p' "$AQUI/local/$f")"
  comprobar "$f: app-verde repite perfil y espera al migrador (extends no los hereda en todo Compose)" "1 1" \
    "$(printf '%s\n' "$verde" | grep -c '    profiles: \["ranura"\]') $(printf '%s\n' "$verde" | grep -c 'condition: service_completed_successfully')"
  comprobar "$f: ya no queda el servicio app ni su contenedor único" "0 0" \
    "$(grep -cx '  app:' "$AQUI/local/$f") $(grep -cx "    container_name: $pref" "$AQUI/local/$f")"
done
comprobar "caddy ya no depende de la app" 0 "$(grep -A2 -x '    depends_on:' "$AQUI/local/docker-compose.produccion.yml" | grep -cx '      - app')"

echo "argumentos"
for args in "" "desplegar" "desplegar produccion" "desplegar pre $A" "desplegar produccion corto" "activa" "activa x" "recargar" "recargar x" "drenar produccion c i" "drenar produccion c i x" "otra"; do
  limpio; relevo -- $args
  comprobar "'$args' rechazado sin tocar nada" "2 0" "$codigo $(grep -c '^docker compose' "$LOG")"
done

echo "activa"
limpio; relevo -- activa produccion
comprobar "sin fichero ni contenedor: sale con 1" 1 "$codigo"
limpio; contenedor caemanager-app "$A"; relevo -- activa produccion
comprobar "sin fichero: el contenedor anterior a P1-F2 si corre" "0 caemanager-app" "$codigo $salida"
limpio; contenedor caemanager-app "$A" false; relevo -- activa produccion
comprobar "sin fichero y el anterior parado: sale con 1" 1 "$codigo"
limpio; fichero staging "to caemanager-staging-app-verde:8080 caemanager-staging-app-azul:8080"; relevo -- activa staging
comprobar "con fichero: la primera dirección, sin puerto" "0 caemanager-staging-app-verde" "$codigo $salida"

echo "desplegar: transición desde un solo contenedor (producción)"
limpio; contenedor caemanager-app "$C"; contenedor caemanager-staging-app "$C"
echo "/etc/caddy/Caddyfile /data" > "$ESTADO/c/caemanager-caddy/montajes"
relevo -- desplegar produccion "$A"
comprobar "sale con 0" 0 "$codigo"
comprobar "la nueva es azul y la anterior drena detrás" "to caemanager-app-azul:8080 caemanager-app:8080" "$(ranuras produccion)"
comprobar "crea el fichero de staging con lo que sirve staging" "to caemanager-staging-app:8080" "$(ranuras staging)"
comprobar "recrea Caddy con el montaje, desde el compose de producción" si \
  "$(contiene "docker compose -f docker-compose.produccion.yml up -d --no-deps --no-build caddy" "$(log)")"
l_caddy="$(grep -n 'up -d --no-deps --no-build caddy' "$LOG" | head -1 | cut -d: -f1)"
l_up="$(grep -n ' app-azul IMAGEN_TAG' "$LOG" | head -1 | cut -d: -f1)"
l_rec="$(grep -n '^RECARGA' "$LOG" | tail -1 | cut -d: -f1)"
comprobar "orden: Caddy con montaje < up de la ranura < recarga" si \
  "$([ -n "$l_caddy" ] && [ -n "$l_up" ] && [ -n "$l_rec" ] && [ "$l_caddy" -lt "$l_up" ] && [ "$l_up" -lt "$l_rec" ] && echo si || echo "no ($l_caddy/$l_up/$l_rec)")"
comprobar "up sin build, esperando a sano, de los servicios sin perfil y la ranura nueva" si \
  "$(contiene "docker compose -f docker-compose.produccion.yml up -d --wait --wait-timeout 180 --no-build migrador db caddy seq app-azul IMAGEN_TAG=$A" "$(log)")"
comprobar "nunca nombra la ranura activa ni el servicio app en el up" 0 "$(grep ' up ' "$LOG" | grep -cE ' app-verde| app( |$)')"
comprobar "Caddy recarga con la nueva primero" si \
  "$(contiene "RECARGA produccion=[to caemanager-app-azul:8080 caemanager-app:8080]" "$(log)")"
comprobar "lanza el drenaje del anterior como unidad de systemd" si \
  "$(contiene "systemd-run --unit talveg-drenaje-produccion --collect --quiet" "$(log)")"
comprobar "  con su id y el máximo de producción" si "$(contiene "drenar produccion caemanager-app id-caemanager-app 1800" "$(log)")"
comprobar "el anterior sigue en marcha (drena, no se corta)" true "$(en_marcha caemanager-app)"

echo "desplegar: relevo normal y ranura que aún drena"
limpio; contenedor caemanager-app-azul "$A"; fichero produccion "to caemanager-app-azul:8080"; fichero staging "to caemanager-staging-app-azul:8080"
relevo -- desplegar produccion "$B"
comprobar "azul activa: la nueva es verde" "0 to caemanager-app-verde:8080 caemanager-app-azul:8080" "$codigo $(ranuras produccion)"
comprobar "no recrea Caddy si ya tiene el montaje" 0 "$(grep -c 'no-deps --no-build caddy' "$LOG")"
comprobar "no toca el fichero del otro entorno" "to caemanager-staging-app-azul:8080" "$(ranuras staging)"
l_stop_unidad="$(grep -n 'systemctl stop talveg-drenaje-produccion' "$LOG" | tail -1 | cut -d: -f1)"; l_run="$(grep -n '^systemd-run' "$LOG" | tail -1 | cut -d: -f1)"
comprobar "para el drenaje anterior justo antes de lanzar el nuevo (no al empezar)" si \
  "$([ -n "$l_stop_unidad" ] && [ -n "$l_run" ] && [ "$l_stop_unidad" -lt "$l_run" ] && [ "$(grep -n ' up -d --wait' "$LOG" | head -1 | cut -d: -f1)" -lt "$l_stop_unidad" ] && echo si || echo no)"
contenedor caemanager-app-verde "$B"   # la siguiente: verde activa, azul aún drenando
relevo -- desplegar produccion "$C"
comprobar "la ranura que drenaba se saca de Caddy antes de recrearla" si \
  "$(contiene "RECARGA produccion=[to caemanager-app-verde:8080]" "$(log)")"
l_saca="$(grep -n 'RECARGA produccion=\[to caemanager-app-verde:8080\]' "$LOG" | tail -1 | cut -d: -f1)"
l_up="$(grep -n ' app-azul IMAGEN_TAG='"$C" "$LOG" | head -1 | cut -d: -f1)"
comprobar "  y eso va antes del up" si "$([ -n "$l_saca" ] && [ -n "$l_up" ] && [ "$l_saca" -lt "$l_up" ] && echo si || echo "no ($l_saca/$l_up)")"
comprobar "  y termina con azul activa y verde drenando" "0 to caemanager-app-azul:8080 caemanager-app-verde:8080" "$codigo $(ranuras produccion)"

echo "desplegar: relevo a la MISMA imagen (cambio de configuración, p. ej. P1-F3)"
limpio; contenedor caemanager-app-azul "$A"; fichero produccion "to caemanager-app-azul:8080"; fichero staging "to caemanager-staging-app-azul:8080"
relevo -- desplegar produccion "$A"
comprobar "arranca la otra ranura con la misma imagen y drena la anterior" \
  "0 to caemanager-app-verde:8080 caemanager-app-azul:8080 caemanager:$A" \
  "$codigo $(ranuras produccion) $(cat "$ESTADO/c/caemanager-app-verde/imagen")"
comprobar "  la anterior sigue en marcha, drenando" true "$(en_marcha caemanager-app-azul)"

echo "desplegar: saliente huérfana (su drenaje murió, p. ej. tras reiniciar el VPS)"
limpio; contenedor caemanager-app-azul "$A"; contenedor caemanager-app "$C"
fichero produccion "to caemanager-app-azul:8080 caemanager-app:8080"; fichero staging "to caemanager-staging-app-azul:8080"
relevo -- desplegar produccion "$B"
comprobar "se retira (y, por ser anterior a P1-F2, se borra)" "0 <no existe>" "$codigo $(en_marcha caemanager-app)"
comprobar "  y el fichero queda con la nueva y la que servía" "to caemanager-app-verde:8080 caemanager-app-azul:8080" "$(ranuras produccion)"
comprobar "  la que servía sigue en marcha, drenando" true "$(en_marcha caemanager-app-azul)"
limpio; contenedor caemanager-app-azul "$A" false; contenedor caemanager-app "$C"
fichero produccion "to caemanager-app-azul:8080 caemanager-app:8080"; fichero staging "to caemanager-staging-app-azul:8080"
relevo COMPOSE_FALLA=1 -- desplegar produccion "$B"
comprobar "con la primera muerta, la saliente viva pasa a activa y sigue sirviendo aunque la nueva falle" "1 true to caemanager-app:8080" \
  "$codigo $(en_marcha caemanager-app) $(ranuras produccion)"

echo "desplegar: la activa murió y la saliente sigue viva (Caddy ya sirve por ella)"
limpio; contenedor caemanager-app-verde "$B" false; contenedor caemanager-app-azul "$A"
fichero produccion "to caemanager-app-verde:8080 caemanager-app-azul:8080"; fichero staging "to caemanager-staging-app-azul:8080"
relevo -- desplegar produccion "$C"
comprobar "la nueva va en la ranura de la muerta y la viva queda detrás" "0 to caemanager-app-verde:8080 caemanager-app-azul:8080 caemanager:$C" \
  "$codigo $(ranuras produccion) $(cat "$ESTADO/c/caemanager-app-verde/imagen")"
comprobar "  la viva no se recrea ni se para" "true caemanager:$A" "$(en_marcha caemanager-app-azul) $(cat "$ESTADO/c/caemanager-app-azul/imagen")"
limpio; contenedor caemanager-app-verde "$B" false; contenedor caemanager-app-azul "$A"
fichero produccion "to caemanager-app-verde:8080 caemanager-app-azul:8080"; fichero staging "to caemanager-staging-app-azul:8080"
relevo COMPOSE_FALLA=1 -- desplegar produccion "$C"
comprobar "  y si la nueva no llega a sana, la viva sigue sirviendo" "1 true" "$codigo $(en_marcha caemanager-app-azul)"

echo "qué Caddyfile se recarga (Caddy es uno para los dos entornos)"
limpio; contenedor caemanager-app-azul "$A"; fichero produccion "to caemanager-app-azul:8080"; fichero staging "to caemanager-staging-app-azul:8080"
relevo -- desplegar produccion "$B"
comprobar "producción recarga el del checkout" "FUENTE MARCA_CHECKOUT" "$(grep '^FUENTE' "$LOG" | tail -1)"
comprobar "  y lo guarda como aprobado" 1 "$(grep -c 'MARCA_CHECKOUT' "$DIR_RANURAS/Caddyfile.aprobado" 2>/dev/null || echo 0)"
limpio; contenedor caemanager-staging-app-azul "$A"; fichero staging "to caemanager-staging-app-azul:8080"; fichero produccion "to caemanager-app-azul:8080"
printf '# MARCA_APROBADO\n' > "$DIR_RANURAS/Caddyfile.aprobado"
relevo -- desplegar staging "$B"
comprobar "staging recarga el aprobado, nunca el del checkout" "FUENTE MARCA_APROBADO 0" \
  "$(grep '^FUENTE' "$LOG" | sort -u | tr '\n' ' ' | sed 's/ $//') $(grep -c 'MARCA_CHECKOUT' "$DIR_RANURAS/Caddyfile.aprobado")"
limpio; contenedor caemanager-app-verde "$B"; contenedor caemanager-app-azul "$A" true 0 0
fichero produccion "to caemanager-app-verde:8080 caemanager-app-azul:8080"; fichero staging "to caemanager-staging-app-azul:8080"
printf '# MARCA_APROBADO\n' > "$DIR_RANURAS/Caddyfile.aprobado"
relevo -- drenar produccion caemanager-app-azul id-caemanager-app-azul 1800
comprobar "el fin de un drenaje recarga el aprobado (el checkout puede ser de staging)" "FUENTE MARCA_APROBADO" "$(grep '^FUENTE' "$LOG" | tail -1)"

echo "desplegar: la activa corre pero NO está sana y la saliente sí (revisión de Codex)"
limpio; contenedor caemanager-app-azul "$A"; echo unhealthy > "$ESTADO/c/caemanager-app-azul/salud"; contenedor caemanager-app "$C"
fichero produccion "to caemanager-app-azul:8080 caemanager-app:8080"; fichero staging "to caemanager-staging-app-azul:8080"
relevo COMPOSE_FALLA=1 -- desplegar produccion "$B"
comprobar "la sana se conserva como activa y, si la nueva falla, sigue sirviendo" "1 true to caemanager-app:8080" \
  "$codigo $(en_marcha caemanager-app) $(ranuras produccion)"
limpio; contenedor caemanager-app-azul "$A"; echo unhealthy > "$ESTADO/c/caemanager-app-azul/salud"; contenedor caemanager-app "$C"
fichero produccion "to caemanager-app-azul:8080 caemanager-app:8080"; fichero staging "to caemanager-staging-app-azul:8080"
relevo -- desplegar produccion "$B"
comprobar "  y si la nueva llega a sana, la nueva va en la ranura de la no sana y la sana drena" \
  "0 to caemanager-app-azul:8080 caemanager-app:8080 caemanager:$B" "$codigo $(ranuras produccion) $(cat "$ESTADO/c/caemanager-app-azul/imagen")"

echo "salientes anteriores: se paran DESPUÉS de conmutar, nunca antes"
limpio; contenedor caemanager-app-azul "$A"; contenedor caemanager-app "$C"
fichero produccion "to caemanager-app-azul:8080 caemanager-app:8080"; fichero staging "to caemanager-staging-app-azul:8080"
relevo COMPOSE_FALLA=1 -- desplegar produccion "$B"
comprobar "si la nueva no llega a sana, la saliente anterior sigue en marcha y en Caddy" "1 true to caemanager-app-azul:8080 caemanager-app:8080" \
  "$codigo $(en_marcha caemanager-app) $(ranuras produccion)"

limpio; contenedor caemanager-app-azul "$A"; contenedor caemanager-app "$C"
fichero produccion "to caemanager-app-azul:8080 caemanager-app:8080"; fichero staging "to caemanager-staging-app-azul:8080"
relevo RECARGA_FALLA=1 -- desplegar produccion "$B"
comprobar "si Caddy rechaza la conmutación, la saliente anterior sigue en marcha y el fichero vuelve a estar como antes" \
  "1 true to caemanager-app-azul:8080 caemanager-app:8080" "$codigo $(en_marcha caemanager-app) $(ranuras produccion)"

limpio; contenedor caemanager-app-verde "$B"; contenedor caemanager-app-azul "$A"
fichero produccion "to caemanager-app-verde:8080 caemanager-app-azul:8080"; fichero staging "to caemanager-staging-app-azul:8080"
printf '# MARCA_APROBADO\n' > "$DIR_RANURAS/Caddyfile.aprobado"
relevo COMPOSE_FALLA=1 -- desplegar produccion "$C"
comprobar "sacar la ranura libre antes del up recarga el APROBADO; si el up falla, el aprobado no cambia" \
  "1 FUENTE MARCA_APROBADO 0 1" \
  "$codigo $(grep '^FUENTE' "$LOG" | sort -u | tr '\n' ' ' | sed 's/ $//') $(grep -c 'MARCA_CHECKOUT' "$DIR_RANURAS/Caddyfile.aprobado") $(grep -c 'MARCA_APROBADO' "$DIR_RANURAS/Caddyfile.aprobado")"

limpio; contenedor caemanager-app-verde "$B"; contenedor caemanager-app-azul "$A"
fichero produccion "to caemanager-app-verde:8080 caemanager-app-azul:8080"; fichero staging "to caemanager-staging-app-azul:8080"
relevo COMPOSE_FALLA=1 -- desplegar produccion "$C"
comprobar "sin aprobado, la recarga previa al up usa el Caddyfile con el que corre Caddy y no crea aprobado" \
  "1 FUENTE CONTENEDOR no to caemanager-app-verde:8080" \
  "$codigo $(grep '^FUENTE' "$LOG" | sort -u | tr '\n' ' ' | sed 's/ $//') $([ -f "$DIR_RANURAS/Caddyfile.aprobado" ] && echo si || echo no) $(ranuras produccion)"

echo "fallos que antes acababan en éxito (errexit no actúa a la izquierda de || y &&)"
limpio; contenedor caemanager-app-azul "$A"; fichero produccion "to caemanager-app-azul:8080"; fichero staging "to caemanager-staging-app-azul:8080"
relevo SYSTEMD_FALLA=1 -- desplegar produccion "$B"
comprobar "systemd-run falla: se avisa de que no hay drenaje" si "$(contiene "no se pudo lanzar el drenaje de caemanager-app-azul" "$salida")"
comprobar "  y no dice «lanzado»" 0 "$(printf '%s\n' "$salida" | grep -c 'Drenaje de caemanager-app-azul lanzado')"
limpio; contenedor caemanager-app-verde "$B"; contenedor caemanager-app-azul "$A" true 0 0
fichero produccion "to caemanager-app-verde:8080 caemanager-app-azul:8080"; fichero staging "to caemanager-staging-app-azul:8080"
salida=$(STOP_FALLA=caemanager-app-azul timeout 3 bash "$GUION" drenar produccion caemanager-app-azul id-caemanager-app-azul 0 2>&1); codigo=$?
comprobar "docker stop falla al retirar: no dice «retirado» y sigue reintentando (timeout)" "124 0 true" \
  "$codigo $(printf '%s\n' "$salida" | grep -c 'retirado de produccion') $(en_marcha caemanager-app-azul)"

echo "Caddyfile aprobado: arranque, fallo de recarga y recargar"
limpio; contenedor caemanager-staging-app-azul "$A"; fichero staging "to caemanager-staging-app-azul:8080"; fichero produccion "to caemanager-app-azul:8080"
relevo -- desplegar staging "$B"
comprobar "sin aprobado (arranque): usa el del checkout, lo guarda como aprobado y avisa" "FUENTE MARCA_CHECKOUT 1 si" \
  "$(grep '^FUENTE' "$LOG" | tail -1) $(grep -c 'MARCA_CHECKOUT' "$DIR_RANURAS/Caddyfile.aprobado" 2>/dev/null || echo 0) $(contiene "no hay Caddyfile aprobado" "$salida")"
limpio; contenedor caemanager-app-azul "$A"; fichero produccion "to caemanager-app-azul:8080"; fichero staging "to caemanager-staging-app-azul:8080"
relevo RECARGA_FALLA=1 -- desplegar produccion "$B"
comprobar "si Caddy rechaza la recarga, el Caddyfile no queda como aprobado" "1 no" \
  "$codigo $([ -f "$DIR_RANURAS/Caddyfile.aprobado" ] && echo si || echo no)"
limpio; fichero produccion "to caemanager-app-verde:8080"; fichero staging "to caemanager-staging-app-azul:8080"
printf '# MARCA_APROBADO\n' > "$DIR_RANURAS/Caddyfile.aprobado"
relevo -- recargar produccion
comprobar "recargar: recarga con el aprobado" "0 FUENTE MARCA_APROBADO" "$codigo $(grep '^FUENTE' "$LOG" | tail -1)"

echo "transición: el Caddyfile se valida antes de recrear Caddy"
limpio; contenedor caemanager-app "$C"; echo "/etc/caddy/Caddyfile /data" > "$ESTADO/c/caemanager-caddy/montajes"
relevo VALIDA_FALLA=1 -- desplegar produccion "$A"
comprobar "no valida: no se recrea Caddy, no se arranca ranura y sigue el contenedor único" "0 0 true" \
  "$(grep -c 'no-deps --no-build caddy' "$LOG") $(grep -c ' app-azul IMAGEN_TAG' "$LOG") $(en_marcha caemanager-app)"
comprobar "  y el despliegue falla" si "$([ "$codigo" -ne 0 ] && echo si || echo no)"

echo "desplegar: staging"
limpio; contenedor caemanager-staging-app-verde "$A"; fichero staging "to caemanager-staging-app-verde:8080"; fichero produccion "to caemanager-app-azul:8080"
relevo -- desplegar staging "$B"
comprobar "usa su compose y su .env, y la ranura libre" si \
  "$(contiene "docker compose -f docker-compose.staging.yml --env-file .env.staging up -d --wait --wait-timeout 180 --no-build migrador db seq app-azul IMAGEN_TAG=$B" "$(log)")"
comprobar "fichero de staging con la nueva primero" "to caemanager-staging-app-azul:8080 caemanager-staging-app-verde:8080" "$(ranuras staging)"
comprobar "no toca producción" "to caemanager-app-azul:8080" "$(ranuras produccion)"
comprobar "drenaje con el máximo de staging" si "$(contiene "drenar staging caemanager-staging-app-verde id-caemanager-staging-app-verde 300" "$(log)")"

echo "desplegar: fallos"
limpio; contenedor caemanager-app-azul "$A"; fichero produccion "to caemanager-app-azul:8080"; fichero staging "to caemanager-staging-app-azul:8080"
relevo COMPOSE_FALLA=1 -- desplegar produccion "$B"
comprobar "la nueva no llega a sana: sale con 1" 1 "$codigo"
comprobar "  Caddy sigue en la de antes y no se recarga" "to caemanager-app-azul:8080 0" "$(ranuras produccion) $(grep -c '^RECARGA' "$LOG")"
comprobar "  sin drenaje" 0 "$(grep -c '^systemd-run' "$LOG")"
comprobar "  vuelca los logs de la nueva" si "$(contiene "docker logs --tail 300 caemanager-app-verde" "$(log)")"

limpio; contenedor caemanager-app-azul "$A"; fichero produccion "to caemanager-app-azul:8080"; fichero staging "to caemanager-staging-app-azul:8080"
relevo COMPOSE_NO_CAMBIA=1 -- desplegar produccion "$B"
comprobar "la ranura nueva no corre la imagen pedida: sale con 1 sin conmutar" "1 to caemanager-app-azul:8080" "$codigo $(ranuras produccion)"

limpio; contenedor caemanager-app-azul "$A"; fichero produccion "to caemanager-app-azul:8080"; fichero staging "to caemanager-staging-app-azul:8080"
relevo RECARGA_FALLA=1 -- desplegar produccion "$B"
comprobar "Caddy rechaza la conmutación: sale con 1" 1 "$codigo"
comprobar "  restaura el fichero con la de antes" "to caemanager-app-azul:8080" "$(ranuras produccion)"
comprobar "  para la nueva y deja la de antes en marcha" "false true" "$(en_marcha caemanager-app-verde) $(en_marcha caemanager-app-azul)"
comprobar "  sin drenaje" 0 "$(grep -c '^systemd-run' "$LOG")"

limpio; fichero staging "to caemanager-staging-app-azul:8080"
relevo -- desplegar produccion "$A"
comprobar "sin ninguna ranura en marcha: arranca azul sola" "0 to caemanager-app-azul:8080" "$codigo $(ranuras produccion)"
comprobar "  y no hay nada que drenar" 0 "$(grep -c '^systemd-run' "$LOG")"

echo "desplegar: guarda de envío de correo (C-04)"
# escenario_correo ENTORNO: azul activa en los dos entornos y Caddy SIN el
# montaje, para ver que abortar tampoco recrea Caddy.
escenario_correo() {
  limpio; contenedor caemanager-app-azul "$A"; contenedor caemanager-staging-app-azul "$A"
  fichero produccion "to caemanager-app-azul:8080"; fichero staging "to caemanager-staging-app-azul:8080"
  echo "/etc/caddy/Caddyfile /data" > "$ESTADO/c/caemanager-caddy/montajes"
}
sin_tocar_nada() { # ENTORNO: la huella de un relevo abortado antes de empezar
  local e=$1 pref=caemanager-app
  [ "$e" = staging ] && pref=caemanager-staging-app
  echo "$(grep -c ' up ' "$LOG") $(grep -c '^RECARGA' "$LOG") $(grep -c '^systemd-run' "$LOG") $(ranuras "$e") $(en_marcha $pref-azul)"
}
for caso in "ausente|" 'vacía|      EnvioCorreo__Modo: ""' 'solo espacios|      EnvioCorreo__Modo: "  "' "null|      EnvioCorreo__Modo: null" "otra clave con el mismo prefijo|      EnvioCorreo__ModoAntiguo: Abierto"; do
  escenario_correo; relevo "CORREO_VERDE=${caso#*|}" -- desplegar produccion "$B"
  comprobar "${caso%%|*}: aborta sin up, sin recarga, sin drenaje y sin tocar la activa" "1 0 0 0 to caemanager-app-azul:8080 true" "$codigo $(sin_tocar_nada produccion)"
  comprobar "  dice qué falta y dónde" si "$(contiene "falta EnvioCorreo__Modo, o no tiene valor, en el entorno efectivo de app-verde. Añádela a .env en el servidor" "$salida")"
  comprobar "  no imprime ningún valor" 0 "$(printf '%s\n' "$salida" | grep -c 'SECRETO_CENTINELA')"
done

escenario_correo; relevo CORREO_VERDE= -- desplegar produccion "$B"
comprobar "abortar no recrea Caddy" 0 "$(grep -c 'no-deps --no-build caddy' "$LOG")"
comprobar "la configuración se resuelve con la imagen nueva y las ranuras a la vista" si \
  "$(contiene "docker compose -f docker-compose.produccion.yml --profile ranura config IMAGEN_TAG=$B" "$(log)")"
limpio; contenedor caemanager-app-verde "$A"; fichero produccion "to caemanager-app-verde:8080"; fichero staging "to caemanager-staging-app-azul:8080"
relevo CORREO_VERDE= -- desplegar produccion "$B"
comprobar "mira la ranura que arranca, no la otra: sin clave en la activa verde, azul sí la lleva" "0 to caemanager-app-azul:8080 caemanager-app-verde:8080" "$codigo $(ranuras produccion)"
limpio; contenedor caemanager-app-azul "$A"; fichero produccion "to caemanager-app-azul:8080"; fichero staging "to caemanager-staging-app-azul:8080"
relevo CORREO_VERDE= -- desplegar produccion "$B"
comprobar "  y al revés: con la clave solo en la activa azul, verde aborta" "1 to caemanager-app-azul:8080" "$codigo $(ranuras produccion)"

escenario_correo; relevo CONFIG_FALLA=1 -- desplegar produccion "$B"
comprobar "config falla: aborta igual, sin tocar nada" "1 0 0 0 to caemanager-app-azul:8080 true" "$codigo $(sin_tocar_nada produccion)"
comprobar "  con su propio mensaje" si "$(contiene "no se pudo resolver la configuración efectiva de app-verde" "$salida")"
comprobar "  y sin repetir el error de Compose, que puede citar valores" 0 "$(printf '%s\n' "$salida" | grep -c 'SECRETO_CENTINELA')"

escenario_correo; relevo CORREO_VERDE= -- desplegar staging "$B"
comprobar "staging también aborta sin la clave" "1 0 0 0 to caemanager-staging-app-azul:8080 true" "$codigo $(sin_tocar_nada staging)"
comprobar "  nombra .env.staging" si "$(contiene "Añádela a .env.staging en el servidor" "$salida")"
comprobar "  y resuelve con su compose y su .env" si \
  "$(contiene "docker compose -f docker-compose.staging.yml --env-file .env.staging --profile ranura config IMAGEN_TAG=$B" "$(log)")"
limpio; contenedor caemanager-staging-app-azul "$A"; fichero staging "to caemanager-staging-app-azul:8080"; fichero produccion "to caemanager-app-azul:8080"
relevo "CORREO_VERDE=      EnvioCorreo__Modo: Cerrado" -- desplegar staging "$B"
comprobar "staging con Cerrado explícito conmuta" "0 to caemanager-staging-app-verde:8080 caemanager-staging-app-azul:8080" "$codigo $(ranuras staging)"
limpio; contenedor caemanager-staging-app-azul "$A"; fichero staging "to caemanager-staging-app-azul:8080"; fichero produccion "to caemanager-app-azul:8080"
relevo "CORREO_VERDE=      EnvioCorreo__Modo: \"Cerrado\"" -- desplegar staging "$B"
comprobar "  también entrecomillado" "0 to caemanager-staging-app-verde:8080 caemanager-staging-app-azul:8080" "$codigo $(ranuras staging)"
limpio; contenedor caemanager-app-azul "$A"; fichero produccion "to caemanager-app-azul:8080"; fichero staging "to caemanager-staging-app-azul:8080"
relevo "CORREO_VERDE=      EnvioCorreo__Modo: \"Abierto\"" -- desplegar produccion "$B"
comprobar "un valor entrecomillado cuenta" "0 to caemanager-app-verde:8080 caemanager-app-azul:8080" "$codigo $(ranuras produccion)"

for caso in "Abierto|      EnvioCorreo__Modo: Abierto" \
            'Abierto entrecomillado|      EnvioCorreo__Modo: "Abierto"' \
            "SoloPermitidos|      EnvioCorreo__Modo: SoloPermitidos" \
            "abierto en minúsculas|      EnvioCorreo__Modo: abierto" \
            "un modo que no existe|      EnvioCorreo__Modo: Entreabierto" \
            "Cerrado y otra grafía de la clave con Abierto|      EnvioCorreo__Modo: Cerrado
      ENVIOCORREO__MODO: Abierto"; do
  escenario_correo; relevo "CORREO_VERDE=${caso#*|}" -- desplegar staging "$B"
  comprobar "staging con ${caso%%|*}: aborta sin up, sin recarga, sin drenaje y sin tocar la activa" "1 0 0 0 to caemanager-staging-app-azul:8080 true" "$codigo $(sin_tocar_nada staging)"
  comprobar "  dice qué exige y dónde" si "$(contiene "EnvioCorreo__Modo no es Cerrado en el entorno efectivo de app-verde" "$salida")"
  comprobar "  nombra .env.staging" si "$(contiene "Corrígelo en .env.staging en el servidor" "$salida")"
  comprobar "  no imprime el valor ni ningún otro" 0 "$(printf '%s\n' "$salida" | grep -ciE 'SECRETO_CENTINELA|abierto|SoloPermitidos')"
done
limpio; contenedor caemanager-staging-app-azul "$A"; fichero staging "to caemanager-staging-app-azul:8080"; fichero produccion "to caemanager-app-azul:8080"
relevo "CORREO_AZUL=      EnvioCorreo__Modo: Abierto" -- desplegar staging "$B"
comprobar "staging mira la ranura que arranca: Abierto en la que no arranca no aborta" "0 to caemanager-staging-app-verde:8080 caemanager-staging-app-azul:8080" "$codigo $(ranuras staging)"
limpio; contenedor caemanager-app-azul "$A"; fichero produccion "to caemanager-app-azul:8080"; fichero staging "to caemanager-staging-app-azul:8080"
relevo "CORREO_VERDE=      EnvioCorreo__Modo: Abierto" -- desplegar produccion "$B"
comprobar "producción no exige Cerrado" "0 to caemanager-app-verde:8080 caemanager-app-azul:8080" "$codigo $(ranuras produccion)"
limpio; contenedor caemanager-app-azul "$A"; fichero produccion "to caemanager-app-azul:8080"; fichero staging "to caemanager-staging-app-azul:8080"
relevo "CORREO_VERDE=      EnvioCorreo__Modo: Cerrado
      ENVIOCORREO__MODO: Abierto" -- desplegar produccion "$B"
comprobar "  y otra grafía de la clave no le afecta" "0 to caemanager-app-verde:8080 caemanager-app-azul:8080" "$codigo $(ranuras produccion)"

echo "comprobar-guarda: la guarda sola, sobre la ranura de desplegar"
escenario_correo; relevo -- comprobar-guarda produccion "$B"
comprobar "cumplida: sale 0 sin tocar nada" "0 0 0 0 to caemanager-app-azul:8080 true" "$codigo $(sin_tocar_nada produccion)"
comprobar "  y sin recrear Caddy" 0 "$(grep -c 'no-deps --no-build caddy' "$LOG")"
comprobar "  resuelve con la imagen de destino" si \
  "$(contiene "docker compose -f docker-compose.produccion.yml --profile ranura config IMAGEN_TAG=$B" "$(log)")"
escenario_correo; relevo CORREO_VERDE= -- comprobar-guarda produccion "$B"
comprobar "sin la clave: sale 1 sin tocar nada" "1 0 0 0 to caemanager-app-azul:8080 true" "$codigo $(sin_tocar_nada produccion)"
comprobar "  y dice qué falta" si "$(contiene "falta EnvioCorreo__Modo, o no tiene valor, en el entorno efectivo de app-verde" "$salida")"
escenario_correo; relevo "CORREO_VERDE=      EnvioCorreo__Modo: Abierto" -- comprobar-guarda staging "$B"
comprobar "staging sin Cerrado: sale 1 sin tocar nada" "1 0 0 0 to caemanager-staging-app-azul:8080 true" "$codigo $(sin_tocar_nada staging)"
limpio; contenedor caemanager-app-verde "$A"; fichero produccion "to caemanager-app-verde:8080"; fichero staging "to caemanager-staging-app-azul:8080"
relevo CORREO_VERDE= -- comprobar-guarda produccion "$B"
comprobar "con verde activa mira azul, como desplegar: sin clave en verde pasa" 0 "$codigo"
relevo CORREO_AZUL= -- comprobar-guarda produccion "$B"
comprobar "  y sin clave en azul no pasa" 1 "$codigo"
limpio; contenedor caemanager-app-azul "$A"; contenedor caemanager-app-verde "$B"; echo unhealthy > "$ESTADO/c/caemanager-app-verde/salud"
fichero produccion "to caemanager-app-verde:8080 caemanager-app-azul:8080"; fichero staging "to caemanager-staging-app-azul:8080"
relevo CORREO_AZUL= -- comprobar-guarda produccion "$C"
comprobar "con la primera del fichero insana, la que sirve es la sana: arranca verde" 0 "$codigo"
relevo -- comprobar-guarda produccion latest
comprobar "SHA no válido: uso" 2 "$codigo"
relevo -- comprobar-guarda otro "$B"
comprobar "entorno no válido: uso" 2 "$codigo"

echo "imágenes sin la guarda de correo: ignoran el modo y se comportan como Abierto"
escenario_correo; sin_guarda "$B"; relevo -- desplegar staging "$B"
comprobar "staging no arranca una imagen sin la etiqueta aunque el modo sea Cerrado" "1 0 0 0 to caemanager-staging-app-azul:8080 true" "$codigo $(sin_tocar_nada staging)"
comprobar "  dice qué falta" si "$(contiene "caemanager:$B no declara es.talveg.guarda-correo" "$salida")"
comprobar "  no llega a resolver la configuración, y no imprime ningún valor" "0 0" \
  "$(grep -c 'profile ranura config' "$LOG") $(printf '%s\n' "$salida" | grep -c 'SECRETO_CENTINELA')"
escenario_correo; sin_guarda "$B"; relevo -- comprobar-guarda staging "$B"
comprobar "comprobar-guarda lo mismo: la vuelta atrás se detiene antes de restaurar nada" "1 0 0 0 to caemanager-staging-app-azul:8080 true" "$codigo $(sin_tocar_nada staging)"
escenario_correo; relevo INSPECT_GUARDA_FALLA=1 -- desplegar staging "$B"
comprobar "si no se puede leer la etiqueta, staging tampoco arranca" "1 0 0 0 to caemanager-staging-app-azul:8080 true" "$codigo $(sin_tocar_nada staging)"
escenario_correo; sin_guarda "$B"; relevo "CORREO_VERDE=      EnvioCorreo__Modo: Abierto" -- comprobar-guarda produccion "$B"
comprobar "producción con Abierto sí admite una imagen sin la etiqueta: no cambia nada" "0 0 0 0 to caemanager-app-azul:8080 true" "$codigo $(sin_tocar_nada produccion)"
for caso in "Cerrado|      EnvioCorreo__Modo: Cerrado" \
            "SoloPermitidos|      EnvioCorreo__Modo: SoloPermitidos" \
            "abierto en minúsculas|      EnvioCorreo__Modo: abierto" \
            "Abierto y otra grafía de la clave con Cerrado|      EnvioCorreo__Modo: Abierto
      ENVIOCORREO__MODO: Cerrado"; do
  escenario_correo; sin_guarda "$B"; relevo "CORREO_VERDE=${caso#*|}" -- desplegar produccion "$B"
  comprobar "producción, imagen sin la etiqueta y ${caso%%|*}: aborta sin tocar nada" "1 0 0 0 to caemanager-app-azul:8080 true" "$codigo $(sin_tocar_nada produccion)"
  comprobar "  dice por qué" si "$(contiene "esa imagen ignoraría el modo" "$salida")"
done
escenario_correo; relevo INSPECT_GUARDA_FALLA=1 "CORREO_VERDE=      EnvioCorreo__Modo: Cerrado" -- desplegar produccion "$B"
comprobar "producción con la etiqueta ilegible y Cerrado: aborta" "1 0 0 0 to caemanager-app-azul:8080 true" "$codigo $(sin_tocar_nada produccion)"
escenario_correo; sin_guarda "$B"; relevo CORREO_VERDE= -- desplegar produccion "$B"
comprobar "producción, imagen sin la etiqueta y sin la clave: aborta por la clave" "1 0" \
  "$codigo $(grep -c ' up ' "$LOG")"
comprobar "  y lo dice" si "$(contiene "falta EnvioCorreo__Modo, o no tiene valor" "$salida")"

echo "saliente sin la guarda: no drena salvo con Abierto"
# saliente_staging: staging sirve con azul (imagen A, sin la etiqueta) y releva a B.
saliente_staging() {
  limpio; contenedor caemanager-staging-app-azul "$A"; sin_guarda caemanager-staging-app-azul
  fichero staging "to caemanager-staging-app-azul:8080"; fichero produccion "to caemanager-app-azul:8080"
}
saliente_staging; relevo -- desplegar staging "$B"
comprobar "conmuta y la saliente sale de Caddy, sin drenaje" "0 to caemanager-staging-app-verde:8080 0" "$codigo $(ranuras staging) $(grep -c '^systemd-run' "$LOG")"
comprobar "  y queda parada" false "$(en_marcha caemanager-staging-app-azul)"
comprobar "  sin borrarla, que es una ranura" 0 "$(grep -c '^docker rm' "$LOG")"
comprobar "  Caddy deja de verla antes de pararla" si \
  "$(l_r="$(grep -n '^RECARGA.*staging=\[to caemanager-staging-app-verde:8080\]' "$LOG" | tail -1 | cut -d: -f1)"; l_s="$(grep -n 'docker stop -t 30 caemanager-staging-app-azul' "$LOG" | cut -d: -f1)"; [ -n "$l_r" ] && [ -n "$l_s" ] && [ "$l_r" -lt "$l_s" ] && echo si || echo "no ($l_r/$l_s)")"
kills_de() { grep -c "^docker kill $1 " "$LOG"; }
stops_de() { grep -c "^docker stop -t 30 $1 " "$LOG"; }
AZUL_STG=caemanager-staging-app-azul
saliente_staging; relevo "STOP_FALLA=$AZUL_STG" -- desplegar staging "$B"
comprobar "docker stop falla: se fuerza y el relevo es válido, sin drenaje" "0 false 1 0 to caemanager-staging-app-verde:8080" \
  "$codigo $(en_marcha "$AZUL_STG") $(kills_de "$AZUL_STG") $(grep -c '^systemd-run' "$LOG") $(ranuras staging)"
saliente_staging; relevo "STOP_NO_PARA=$AZUL_STG" -- desplegar staging "$B"
comprobar "docker stop sale con 0 pero sigue viva: se fuerza, que decide el contenedor" "0 false 1" "$codigo $(en_marcha "$AZUL_STG") $(kills_de "$AZUL_STG")"
saliente_staging; relevo RELEVO_TOPE_STOP=1 "STOP_CUELGA=$AZUL_STG" -- desplegar staging "$B"
comprobar "docker stop colgado: el tope lo corta y se fuerza" "0 false" "$codigo $(en_marcha "$AZUL_STG")"
saliente_staging; relevo "STOP_FALLA=$AZUL_STG" "KILL_FALLA=$AZUL_STG" -- desplegar staging "$B"
comprobar "stop y kill fallan: el relevo acaba en error, sin drenaje, fuera de Caddy" "1 true 0 to caemanager-staging-app-verde:8080" \
  "$codigo $(en_marcha "$AZUL_STG") $(grep -c '^systemd-run' "$LOG") $(ranuras staging)"
comprobar "  tras un número acotado de intentos" "3 3" "$(stops_de "$AZUL_STG") $(kills_de "$AZUL_STG")"
comprobar "  nombra la ranura que sigue viva" si "$(contiene "::error::no se pudo parar $AZUL_STG" "$salida")"
comprobar "  y da el relevo por incompleto" si "$(contiene "::error::Relevo de staging incompleto" "$salida")"
saliente_staging; relevo "STOP_PIERDE_ESTADO=$AZUL_STG" -- desplegar staging "$B"
comprobar "si su estado no se puede leer, no cuenta como parada" "1 0" "$codigo $(grep -c '^systemd-run' "$LOG")"
saliente_staging; relevo "STOP_ELIMINA=$AZUL_STG" -- desplegar staging "$B"
comprobar "si deja de existir al pararla, cuenta como parada" 0 "$codigo"
saliente_staging; relevo "REINICIA=$AZUL_STG" -- desplegar staging "$B"
comprobar "con política de reinicio: se le quita antes de pararla y no vuelve" "0 false" "$codigo $(en_marcha "$AZUL_STG")"
saliente_staging; relevo "REINICIA=$AZUL_STG" "UPDATE_FALLA=$AZUL_STG" -- desplegar staging "$B"
comprobar "si no se le puede quitar y vuelve tras pararla, el relevo acaba en error" "1 true" "$codigo $(en_marcha "$AZUL_STG")"
saliente_staging; relevo "INSPECT_SALUD_FALLA=caemanager-staging-app-verde" -- desplegar staging "$B"
comprobar "un fallo al leer la salud de la nueva no es «sin healthcheck»: no se para" "1 true 0 0" \
  "$codigo $(en_marcha "$AZUL_STG") $(stops_de "$AZUL_STG") $(grep -c '^systemd-run' "$LOG")"
saliente_staging; mkdir -p "$ESTADO/c/caemanager-staging-app-verde"; echo unhealthy > "$ESTADO/c/caemanager-staging-app-verde/salud"
relevo -- desplegar staging "$B"
comprobar "si la nueva no está sana, no se para, no drena y el relevo acaba en error" "1 true 0 0 to caemanager-staging-app-verde:8080 caemanager-staging-app-azul:8080" \
  "$codigo $(en_marcha "$AZUL_STG") $(stops_de "$AZUL_STG") $(grep -c '^systemd-run' "$LOG") $(ranuras staging)"
comprobar "  y dice por qué" si "$(contiene "no se para porque caemanager-staging-app-verde no está sana" "$salida")"
# Saliente de un relevo anterior: staging sirve con el contenedor anterior a P1-F2
# y verde sigue en el fichero. La nueva va a azul.
VERDE_STG=caemanager-staging-app-verde
saliente_previa() {
  limpio; contenedor caemanager-staging-app "$A"; contenedor "$VERDE_STG" "$A"; sin_guarda "$VERDE_STG"
  fichero staging "to caemanager-staging-app:8080 $VERDE_STG:8080"; fichero produccion "to caemanager-app-azul:8080"
}
saliente_previa; relevo "STOP_FALLA=$VERDE_STG" -- desplegar staging "$B"
comprobar "saliente previa sin la etiqueta: también se fuerza" "0 false 1" "$codigo $(en_marcha "$VERDE_STG") $(kills_de "$VERDE_STG")"
saliente_previa; relevo "STOP_FALLA=$VERDE_STG" "KILL_FALLA=$VERDE_STG" -- desplegar staging "$B"
comprobar "  y si sigue viva, el relevo acaba en error" "1 true" "$codigo $(en_marcha "$VERDE_STG")"
comprobar "  nombrándola" si "$(contiene "::error::no se pudo parar $VERDE_STG" "$salida")"
saliente_previa; mkdir -p "$ESTADO/c/$AZUL_STG"; echo unhealthy > "$ESTADO/c/$AZUL_STG/salud"
relevo -- desplegar staging "$B"
comprobar "  y con la nueva sin sanar no se para: acaba en error" "1 true 0" "$codigo $(en_marcha "$VERDE_STG") $(stops_de "$VERDE_STG")"
saliente_previa; : > "$ESTADO/c/$VERDE_STG/estado_perdido"
relevo "STOP_FALLA=$VERDE_STG" "KILL_FALLA=$VERDE_STG" -- desplegar staging "$B"
comprobar "saliente previa con el estado ilegible: no cuenta como parada, acaba en error" "1 3" "$codigo $(stops_de "$VERDE_STG")"
saliente_previa; rm -rf "$ESTADO/c/$VERDE_STG"; relevo -- desplegar staging "$B"
comprobar "saliente previa que ya no existe: no bloquea el relevo" 0 "$codigo"
# Un relevo que no pudo parar el contenedor anterior a las ranuras lo deja fuera del
# fichero y vivo: el siguiente tiene que encontrarlo igual.
LEGADO_STG=caemanager-staging-app
saliente_anterior() {
  limpio; contenedor "$LEGADO_STG" "$A"; sin_guarda "$LEGADO_STG"
  fichero staging "to $LEGADO_STG:8080"; fichero produccion "to caemanager-app-azul:8080"
}
saliente_anterior; relevo "STOP_FALLA=$LEGADO_STG" "KILL_FALLA=$LEGADO_STG" -- desplegar staging "$B"
comprobar "el contenedor anterior sin parar queda fuera del fichero, y el relevo en error" "1 true to caemanager-staging-app-azul:8080" \
  "$codigo $(en_marcha "$LEGADO_STG") $(ranuras staging)"
relevo "STOP_FALLA=$LEGADO_STG" "KILL_FALLA=$LEGADO_STG" -- desplegar staging "$C"
comprobar "  el reintento lo encuentra sin estar anotado: si sigue vivo, otra vez error" "1 true" "$codigo $(en_marcha "$LEGADO_STG")"
comprobar "  nombrándolo" si "$(contiene "::error::no se pudo parar $LEGADO_STG" "$salida")"
relevo -- desplegar staging "$A"
comprobar "  y en cuanto se puede parar, lo retira y el relevo vale" "0 <no existe>" "$codigo $(en_marcha "$LEGADO_STG")"
# retirar-sin-guarda: la usa volver-atras.sh cuando el destino ya sirve.
saliente_anterior; relevo "STOP_FALLA=$LEGADO_STG" "KILL_FALLA=$LEGADO_STG" -- desplegar staging "$B"
relevo "STOP_FALLA=$LEGADO_STG" "KILL_FALLA=$LEGADO_STG" -- retirar-sin-guarda staging "$B"
comprobar "retirar-sin-guarda: si sigue viva, sale con 1 y la nombra" "1 true si" \
  "$codigo $(en_marcha "$LEGADO_STG") $(contiene "::error::no se pudo parar $LEGADO_STG" "$salida")"
relevo -- retirar-sin-guarda staging "$B"
comprobar "  y en cuanto se puede, la retira" "0 <no existe> to caemanager-staging-app-azul:8080" "$codigo $(en_marcha "$LEGADO_STG") $(ranuras staging)"
relevo -- retirar-sin-guarda staging "$B"
comprobar "  sin nada pendiente no toca nada" "0 0" "$codigo $(grep -c '^docker stop.*caemanager-staging-app-azul' "$LOG")"
saliente_staging; mkdir -p "$ESTADO/c/caemanager-staging-app-verde"; echo unhealthy > "$ESTADO/c/caemanager-staging-app-verde/salud"
relevo -- desplegar staging "$B"
rm "$ESTADO/c/caemanager-staging-app-verde/salud"
relevo -- retirar-sin-guarda staging "$B"
comprobar "  una que siguiera anotada también sale de Caddy" "0 false to caemanager-staging-app-verde:8080" "$codigo $(en_marcha "$AZUL_STG") $(ranuras staging)"
ranura_sin_anotar() {
  limpio; contenedor "$LEGADO_STG" "$A"; contenedor "$VERDE_STG" "$A"; sin_guarda "$VERDE_STG"
  fichero staging "to $LEGADO_STG:8080"; fichero produccion "to caemanager-app-azul:8080"
}
ranura_sin_anotar; relevo "STOP_FALLA=$VERDE_STG" "KILL_FALLA=$VERDE_STG" -- desplegar staging "$B"
comprobar "una ranura sin anotar que ignora la guarda también se busca" "1 true" "$codigo $(en_marcha "$VERDE_STG")"
limpio; contenedor caemanager-staging-app "$A"; sin_guarda caemanager-staging-app
fichero staging "to caemanager-staging-app:8080"; fichero produccion "to caemanager-app-azul:8080"
relevo -- desplegar staging "$B"
comprobar "el contenedor anterior a P1-F2 se borra, que no tiene servicio" "0 <no existe>" "$codigo $(en_marcha caemanager-staging-app)"
limpio; contenedor caemanager-staging-app-azul "$A"; fichero staging "to caemanager-staging-app-azul:8080"; fichero produccion "to caemanager-app-azul:8080"
relevo -- desplegar staging "$B"
comprobar "saliente que declara la guarda: drena como siempre" "0 to caemanager-staging-app-verde:8080 caemanager-staging-app-azul:8080 1 true" \
  "$codigo $(ranuras staging) $(grep -c '^systemd-run' "$LOG") $(en_marcha caemanager-staging-app-azul)"
saliente_produccion() {
  limpio; contenedor caemanager-app-azul "$A"; sin_guarda caemanager-app-azul
  fichero produccion "to caemanager-app-azul:8080"; fichero staging "to caemanager-staging-app-azul:8080"
}
saliente_produccion; relevo -- desplegar produccion "$B"
comprobar "producción con Abierto: la saliente sin la etiqueta drena con normalidad" "0 to caemanager-app-verde:8080 caemanager-app-azul:8080 1 true" \
  "$codigo $(ranuras produccion) $(grep -c '^systemd-run' "$LOG") $(en_marcha caemanager-app-azul)"
saliente_produccion; relevo "CORREO_VERDE=      EnvioCorreo__Modo: Cerrado" -- desplegar produccion "$B"
comprobar "producción con otro modo: se retira sin drenar" "0 to caemanager-app-verde:8080 0 false" \
  "$codigo $(ranuras produccion) $(grep -c '^systemd-run' "$LOG") $(en_marcha caemanager-app-azul)"

echo "la etiqueta y el Dockerfile"
comprobar "el Dockerfile declara la etiqueta que lee el guion" 1 "$(tr -d '\r' < "$AQUI/../Dockerfile" | grep -cx 'LABEL es.talveg.guarda-correo="1"')"
comprobar "  en la etapa final, la que se despliega" si \
  "$(l_f="$(grep -n '^FROM ' "$AQUI/../Dockerfile" | tail -1 | cut -d: -f1)"; l_l="$(grep -n '^LABEL es.talveg.guarda-correo=' "$AQUI/../Dockerfile" | cut -d: -f1)"; [ -n "$l_f" ] && [ -n "$l_l" ] && [ "$l_l" -gt "$l_f" ] && echo si || echo "no ($l_f/$l_l)")"
comprobar "  y no en deploy.yml, que la pondría a cualquier commit que construya" 0 "$(grep -c 'es.talveg.guarda-correo' "$AQUI/../.github/workflows/deploy.yml")"
comprobar "el guion lee ese mismo nombre" 1 "$(grep -cx 'ETIQUETA_GUARDA_CORREO="es.talveg.guarda-correo"' "$GUION")"
DI_CORREO="$AQUI/../src/CaeManager.Infrastructure/DependencyInjection/InfrastructureServiceCollectionExtensions.cs"
comprobar "lo que la etiqueta declara: SMTP y Microsoft 365 se registran detrás de la guarda" "1 1" \
  "$(grep -c 'ActivatorUtilities.CreateInstance<GuardaEnvioEmailService>' "$DI_CORREO") $(grep -c 'ActivatorUtilities.CreateInstance<GuardaEnvioMicrosoft365GraphClient>' "$DI_CORREO")"

comprobar "ningún compose da valor a la clave: la decide cada .env" "0 0" \
  "$(grep -c 'EnvioCorreo__Modo' "$AQUI/local/docker-compose.produccion.yml") $(grep -c 'EnvioCorreo__Modo' "$AQUI/local/docker-compose.staging.yml")"
comprobar "los dos .env de ejemplo la llevan activa" "1 1" \
  "$(grep -c '^EnvioCorreo__Modo=[A-Za-z]' "$AQUI/local/.env.example") $(grep -c '^EnvioCorreo__Modo=[A-Za-z]' "$AQUI/local/.env.staging.example")"
comprobar "el ejemplo de staging lleva el único modo que el relevo admite allí" 1 \
  "$(tr -d '\r' < "$AQUI/local/.env.staging.example" | grep -cx 'EnvioCorreo__Modo=Cerrado')"

echo "conexiones_establecidas"
limpio; contenedor x "$A" true 3
# shellcheck source=deploy/relevo-app.sh
n="$(source "$GUION"; conexiones_establecidas x)"
comprobar "cuenta las ESTABLISHED al 8080 sin loopback ni TIME_WAIT" 3 "$n"
n="$(source "$GUION"; conexiones_establecidas no-existe)"
comprobar "si no se puede leer, nada (no un 0)" "" "$n"

echo "drenar"
limpio; contenedor caemanager-app-verde "$B"; contenedor caemanager-app-azul "$A" true 2 1 0 0
fichero produccion "to caemanager-app-verde:8080 caemanager-app-azul:8080"; fichero staging "to caemanager-staging-app-azul:8080"
relevo -- drenar produccion caemanager-app-azul id-caemanager-app-azul 1800
comprobar "espera a dos lecturas seguidas sin conexiones y retira" "0 4" "$codigo $(grep -c 'cat /proc/net/tcp' "$LOG")"
comprobar "  bajo el cerrojo de despliegue" si "$(contiene "flock -w 600 9" "$(log)")"
comprobar "  quita la saliente de Caddy antes de pararla" si "$(contiene "RECARGA produccion=[to caemanager-app-verde:8080]" "$(log)")"
l_rec="$(grep -n '^RECARGA' "$LOG" | tail -1 | cut -d: -f1)"; l_stop="$(grep -n 'docker stop -t 30 caemanager-app-azul' "$LOG" | cut -d: -f1)"
comprobar "  orden: recarga < stop" si "$([ -n "$l_rec" ] && [ -n "$l_stop" ] && [ "$l_rec" -lt "$l_stop" ] && echo si || echo "no ($l_rec/$l_stop)")"
comprobar "  la para sin borrarla (es una ranura)" "false 0" "$(en_marcha caemanager-app-azul) $(grep -c '^docker rm' "$LOG")"

limpio; contenedor caemanager-app-azul "$B"; contenedor caemanager-app "$A" true 5
fichero produccion "to caemanager-app-azul:8080 caemanager-app:8080"; fichero staging "to caemanager-staging-app:8080"
relevo -- drenar produccion caemanager-app id-caemanager-app 0
comprobar "vencido el máximo retira aunque haya conexiones" "0 to caemanager-app-azul:8080" "$codigo $(ranuras produccion)"
comprobar "  y el contenedor anterior a P1-F2 se borra" "<no existe>" "$(en_marcha caemanager-app)"

limpio; contenedor caemanager-app-azul "$A" true 0 0; fichero produccion "to caemanager-app-azul:8080"; fichero staging "to caemanager-staging-app-azul:8080"
relevo -- drenar produccion caemanager-app-azul otro-id 1800
comprobar "si el contenedor ya no es el que drenaba (se recreó), no lo toca" "0 true 0" "$codigo $(en_marcha caemanager-app-azul) $(grep -c '^docker stop' "$LOG")"

limpio; contenedor caemanager-app-azul "$A" true 0 0; fichero produccion "to caemanager-app-azul:8080"; fichero staging "to caemanager-staging-app-azul:8080"
relevo -- drenar produccion caemanager-app-azul id-caemanager-app-azul 1800
comprobar "nunca para la ranura activa" "true 0" "$(en_marcha caemanager-app-azul) $(grep -c '^docker stop' "$LOG")"

limpio; contenedor caemanager-app-azul "$A" false; fichero produccion "to caemanager-app-verde:8080 caemanager-app-azul:8080"
relevo -- drenar produccion caemanager-app-azul id-caemanager-app-azul 1800
comprobar "si ya está parada, termina sin más" "0 0" "$codigo $(grep -c '^docker stop' "$LOG")"

limpio; contenedor caemanager-app-verde "$B"; echo unhealthy > "$ESTADO/c/caemanager-app-verde/salud"; contenedor caemanager-app-azul "$A" true 0 0
fichero produccion "to caemanager-app-verde:8080 caemanager-app-azul:8080"; fichero staging "to caemanager-staging-app-azul:8080"
salida=$(timeout 5 bash "$GUION" drenar produccion caemanager-app-azul id-caemanager-app-azul 0 2>&1); codigo=$?
comprobar "con la activa no sana, la saliente no se retira y el drenaje sigue reintentando" "124 true to caemanager-app-verde:8080 caemanager-app-azul:8080" \
  "$codigo $(en_marcha caemanager-app-azul) $(ranuras produccion)"

echo "ci-deploy.sh y volver-atras.sh lo usan"
comprobar "ci-deploy.sh hace el relevo en vez de un up de todo el stack" "1 0" \
  "$(grep -c '^    if ! bash /opt/talveg/deploy/relevo-app.sh desplegar "\$ENTORNO" "\$SHA" < /dev/null; then$' "$AQUI/ci-deploy.sh") $(grep -cE '^[^#]*docker compose [^|]* up ' "$AQUI/ci-deploy.sh")"
comprobar "volver-atras.sh también" "1 0" \
  "$(grep -c 'bash "\$RELEVO_APP" desplegar "\$entorno" "\$sha"' "$AQUI/volver-atras.sh") $(grep -cE '^[^#]*docker compose [^|]* up ' "$AQUI/volver-atras.sh")"
comprobar "volver-atras.sh pide la guarda con la orden que este guion atiende" 1 \
  "$(grep -c 'bash "\$RELEVO_APP" comprobar-guarda "\$entorno" "\$sha" < /dev/null' "$AQUI/volver-atras.sh")"

# Mutaciones: cada una devuelve un defecto concreto y su caso tiene que verlo.
if [ -z "${RELEVO_GUION:-}" ]; then
  echo "mutaciones"
  mutar() { # NOMBRE SED ESCENARIO COMPROBACIÓN
    local nombre=$1 expr=$2 esc=$3 comp=$4
    local m="$TMP/mutante.sh"
    sed "$expr" "$GUION" > "$m"
    if cmp -s "$GUION" "$m"; then comprobar "mutación '$nombre' cambia el guion" si no; return; fi
    "$esc" "$m"
    comprobar "mutación '$nombre' la caza su caso" si "$("$comp")"
  }
  esc_recarga() { limpio; contenedor caemanager-app-azul "$A"; fichero produccion "to caemanager-app-azul:8080"; fichero staging "to caemanager-staging-app-azul:8080"
                  salida=$(RECARGA_FALLA=1 bash "$1" desplegar produccion "$B" 2>&1); codigo=$?; }
  comp_recarga() { [ "$(ranuras produccion)" != "to caemanager-app-azul:8080" ] && echo si || echo no; }
  mutar "no restaura el fichero si Caddy rechaza la conmutación" \
    's#^            escribir_ranuras "\$entorno" "\${antes\[0\]}" "\${antes\[1\]:-}"$#            :#' esc_recarga comp_recarga

  esc_activa() { limpio; contenedor caemanager-app-azul "$A" true 0 0; fichero produccion "to caemanager-app-azul:8080"; fichero staging "to caemanager-staging-app-azul:8080"
                 timeout 20 bash "$1" drenar produccion caemanager-app-azul id-caemanager-app-azul 1800 > /dev/null 2>&1; }
  comp_activa() { [ "$(en_marcha caemanager-app-azul)" = false ] && echo si || echo no; }
  mutar "para la ranura activa" 's#^    if \[ "\$a" = "\$contenedor" \]; then$#    if false; then#' esc_activa comp_activa

  esc_id() { limpio; contenedor caemanager-app-verde "$B"; contenedor caemanager-app-azul "$A" true 0 0; fichero produccion "to caemanager-app-verde:8080 caemanager-app-azul:8080"; fichero staging "to caemanager-staging-app-azul:8080"
             timeout 20 bash "$1" drenar produccion caemanager-app-azul otro-id 1800 > /dev/null 2>&1; }
  comp_id() { [ "$(en_marcha caemanager-app-azul)" = false ] && echo si || echo no; }
  mutar "no comprueba que el contenedor sea el mismo" \
    's#^    if \[ "\$(id_de "\$contenedor")" != "\$id" \]; then$#    if false; then#; s#^        if \[ "\$(id_de "\$contenedor")" != "\$id" \] || ! en_marcha "\$contenedor"; then$#        if ! en_marcha "$contenedor"; then#' esc_id comp_id

  esc_drena2() { limpio; contenedor caemanager-app-azul "$A"; fichero produccion "to caemanager-app-azul:8080"; fichero staging "to caemanager-staging-app-azul:8080"
                 salida=$(bash "$1" desplegar produccion "$B" 2>&1); }
  comp_drena() { [ "$(ranuras produccion)" != "to caemanager-app-verde:8080 caemanager-app-azul:8080" ] && echo si || echo no; }
  mutar "la anterior no queda detrás para drenar (corte inmediato)" \
    's#^    escribir_ranuras "\$entorno" "\$cont_nueva" "\$activa_"$#    escribir_ranuras "$entorno" "$cont_nueva"#' esc_drena2 comp_drena

  esc_sana() { limpio; contenedor caemanager-app-verde "$B" false; contenedor caemanager-app-azul "$A" true 0 0
               fichero produccion "to caemanager-app-verde:8080 caemanager-app-azul:8080"; fichero staging "to caemanager-staging-app-azul:8080"
               timeout 5 bash "$1" drenar produccion caemanager-app-azul id-caemanager-app-azul 0 > /dev/null 2>&1; }
  comp_sana() { [ "$(en_marcha caemanager-app-azul)" = false ] && echo si || echo no; }
  mutar "retira la saliente aunque la activa no esté sana" \
    's#^    if \[ -z "\$a" \] || ! sana "\$a"; then$#    if false; then#' esc_sana comp_sana

  esc_cero() { limpio; contenedor caemanager-app-verde "$B"; contenedor caemanager-app-azul "$A" true 3 3 3 3 3 3
               fichero produccion "to caemanager-app-verde:8080 caemanager-app-azul:8080"; fichero staging "to caemanager-staging-app-azul:8080"
               timeout 10 bash "$1" drenar produccion caemanager-app-azul id-caemanager-app-azul 0 > /dev/null 2>&1; }
  comp_cero() { [ "$(en_marcha caemanager-app-azul)" = true ] && echo si || echo no; }
  mutar "el máximo de drenaje no se respeta" \
    's#^        if \[ "\$vacias" -ge 2 \] || \[ \$(( SECONDS - inicio )) -ge "\$max" \]; then$#        if [ "$vacias" -ge 2 ]; then#' esc_cero comp_cero

  esc_correo() { limpio; contenedor caemanager-app-azul "$A"; fichero produccion "to caemanager-app-azul:8080"; fichero staging "to caemanager-staging-app-azul:8080"
                 CORREO_VERDE="${CORREO_MUTANTE-}" bash "$1" desplegar produccion "$B" > /dev/null 2>&1; }
  comp_correo() { [ "$(ranuras produccion)" != "to caemanager-app-azul:8080" ] && echo si || echo no; }
  mutar "el relevo no exige la guarda de correo" \
    's#^    exigir_guarda_de_correo "\$entorno" "\$sha" "app-\$nueva" || return 1$#    :#' esc_correo comp_correo
  mutar "basta con que cualquier servicio lleve la clave" \
    's#actual = (\$0 == "  " servicio ":"); if (actual) visto = 1;#actual = 1; visto = 1;#' esc_correo comp_correo
  mutar "una sección que no es services cuenta como servicio" \
    's#en_servicios = (\$0 == "services:");#en_servicios = 1;#' esc_correo comp_correo
  CORREO_MUTANTE='      EnvioCorreo__Modo: ""'
  mutar "un valor vacío cuenta como presente" \
    's#presente = (valor != "" \&\& valor != "null" \&\& valor != "~")#presente = 1#' esc_correo comp_correo
  unset CORREO_MUTANTE

  esc_staging() { limpio; contenedor caemanager-staging-app-azul "$A"; fichero staging "to caemanager-staging-app-azul:8080"; fichero produccion "to caemanager-app-azul:8080"
                  CORREO_VERDE="$CORREO_MUTANTE" bash "$1" desplegar staging "$B" > /dev/null 2>&1; }
  comp_staging() { [ "$(ranuras staging)" != "to caemanager-staging-app-azul:8080" ] && echo si || echo no; }
  CORREO_MUTANTE='      EnvioCorreo__Modo: Abierto'
  mutar "staging no exige Cerrado" \
    's#^    \[ "\$entorno" = staging \] \&\& modo_exigido="\$MODO_CORREO_STAGING"$#    :#' esc_staging comp_staging
  mutar "un modo distinto del exigido no aborta" \
    's#exit discrepa ? 3 : 0#exit 0#' esc_staging comp_staging
  mutar "la comparación con el modo exigido va al revés" \
    's#if (valor != exigido) discrepa = 1#if (valor == exigido) discrepa = 1#' esc_staging comp_staging
  CORREO_MUTANTE='      EnvioCorreo__Modo: Cerrado
      ENVIOCORREO__MODO: Abierto'
  mutar "otra grafía de la clave no cuenta" \
    's#index(tolower(\$0), "      " tolower(clave) ":") == 1#index($0, "      " clave ":") == 1#' esc_staging comp_staging
  unset CORREO_MUTANTE

  esc_prod_abierto() { limpio; contenedor caemanager-app-azul "$A"; fichero produccion "to caemanager-app-azul:8080"; fichero staging "to caemanager-staging-app-azul:8080"
                       bash "$1" desplegar produccion "$B" > /dev/null 2>&1; }
  comp_prod_abierto() { [ "$(ranuras produccion)" = "to caemanager-app-azul:8080" ] && echo si || echo no; }
  mutar "producción también exige Cerrado" \
    's#^    \[ "\$entorno" = staging \] \&\& modo_exigido="\$MODO_CORREO_STAGING"$#    modo_exigido="$MODO_CORREO_STAGING"#' esc_prod_abierto comp_prod_abierto

  esc_orden() { limpio; contenedor caemanager-app-azul "$A"; fichero produccion "to caemanager-app-azul:8080"; fichero staging "to caemanager-staging-app-azul:8080"
                CORREO_VERDE= bash "$1" comprobar-guarda produccion "$B" > /dev/null 2>&1; codigo=$?; }
  comp_orden() { [ "$codigo" = 0 ] && echo si || echo no; }
  mutar "la orden comprobar-guarda no comprueba nada" \
    's#^    exigir_guarda_de_correo "\$entorno" "\$sha" "app-\$nueva"$#    :#' esc_orden comp_orden

  esc_img_staging() { limpio; contenedor caemanager-staging-app-azul "$A"; sin_guarda "$B"; fichero staging "to caemanager-staging-app-azul:8080"; fichero produccion "to caemanager-app-azul:8080"
                      bash "$1" desplegar staging "$B" > /dev/null 2>&1; }
  comp_img_staging() { [ "$(ranuras staging)" != "to caemanager-staging-app-azul:8080" ] && echo si || echo no; }
  mutar "no se mira la etiqueta de la imagen de destino" \
    's#^    if ! declara_guarda_de_correo "caemanager:\$sha"; then$#    if false; then#' esc_img_staging comp_img_staging
  esc_img_inspect() { limpio; contenedor caemanager-staging-app-azul "$A"; fichero staging "to caemanager-staging-app-azul:8080"; fichero produccion "to caemanager-app-azul:8080"
                      INSPECT_GUARDA_FALLA=1 bash "$1" desplegar staging "$B" > /dev/null 2>&1; }
  mutar "un fallo de inspect cuenta como que la declara" \
    's#"\$1" 2>/dev/null || true)" = 1 \]$#"$1" 2>/dev/null || echo 1)" = 1 ]#' esc_img_inspect comp_img_staging

  esc_img_prod() { limpio; contenedor caemanager-app-azul "$A"; sin_guarda "$B"; fichero produccion "to caemanager-app-azul:8080"; fichero staging "to caemanager-staging-app-azul:8080"
                   CORREO_VERDE='      EnvioCorreo__Modo: Cerrado' bash "$1" desplegar produccion "$B" > /dev/null 2>&1; }
  comp_img_prod() { [ "$(ranuras produccion)" != "to caemanager-app-azul:8080" ] && echo si || echo no; }
  mutar "producción no exige Abierto a una imagen sin la etiqueta" \
    's#^        modo_exigido="\$MODO_CORREO_SIN_GUARDA"$#        :#' esc_img_prod comp_img_prod

  esc_sal() { limpio; contenedor caemanager-staging-app-azul "$A"; sin_guarda caemanager-staging-app-azul; fichero staging "to caemanager-staging-app-azul:8080"; fichero produccion "to caemanager-app-azul:8080"
              bash "$1" desplegar staging "$B" > /dev/null 2>&1; }
  comp_sal() { [ "$(en_marcha caemanager-staging-app-azul)" = true ] && echo si || echo no; }
  mutar "la saliente sin la etiqueta drena igual" \
    's#^        if saliente_ignora_la_guarda "\$entorno" "\$sha" "app-\$nueva" "\$activa_"; then$#        if false; then#' esc_sal comp_sal

  esc_sal_insana() { limpio; contenedor caemanager-staging-app-azul "$A"; sin_guarda caemanager-staging-app-azul; fichero staging "to caemanager-staging-app-azul:8080"; fichero produccion "to caemanager-app-azul:8080"
                     mkdir -p "$ESTADO/c/caemanager-staging-app-verde"; echo unhealthy > "$ESTADO/c/caemanager-staging-app-verde/salud"
                     bash "$1" desplegar staging "$B" > /dev/null 2>&1; }
  comp_sal_insana() { [ "$(en_marcha caemanager-staging-app-azul)" = false ] && echo si || echo no; }
  mutar "se retira la saliente aunque la nueva no esté sana" \
    's#^    if ! sana "\$nueva"; then$#    if false; then#' esc_sal_insana comp_sal_insana

  esc_parada() { saliente_staging; STOP_FALLA="$AZUL_STG" KILL_FALLA="$AZUL_STG" bash "$1" desplegar staging "$B" > /dev/null 2>&1; codigo=$?; }
  comp_parada() { [ "$codigo" = 0 ] && echo si || echo no; }
  mutar "la parada se da por buena sin mirar el contenedor" \
    's#^parada_confirmada() { ! puede_estar_en_marcha "\$1"; }$#parada_confirmada() { true; }#' esc_parada comp_parada
  mutar "el relevo no falla si la imagen sin guarda sigue viva" \
    's#^    if \[ "\$sin_guarda_viva" != 0 \]; then$#    if false; then#' esc_parada comp_parada

  esc_forzar() { saliente_staging; STOP_FALLA="$AZUL_STG" bash "$1" desplegar staging "$B" > /dev/null 2>&1; }
  comp_forzar() { [ "$(en_marcha "$AZUL_STG")" = true ] && echo si || echo no; }
  mutar "no se fuerza la parada" \
    's#^        timeout "\$RELEVO_TOPE_KILL" docker kill "\$contenedor" > /dev/null 2>&1 || true$#        :#' esc_forzar comp_forzar

  esc_previa() { saliente_previa; STOP_FALLA="$VERDE_STG" KILL_FALLA="$VERDE_STG" bash "$1" desplegar staging "$B" > /dev/null 2>&1; codigo=$?; }
  mutar "una saliente previa sin la etiqueta queda en un aviso" \
    's#^        if puede_estar_en_marcha "\$previa" \&\& saliente_ignora_la_guarda "\$entorno" "\$sha" "app-\$nueva" "\$previa"; then$#        if false; then#' esc_previa comp_parada
  esc_previa_ilegible() { saliente_previa; : > "$ESTADO/c/$VERDE_STG/estado_perdido"; STOP_FALLA="$VERDE_STG" KILL_FALLA="$VERDE_STG" bash "$1" desplegar staging "$B" > /dev/null 2>&1; codigo=$?; }
  mutar "el estado ilegible de una saliente previa cuenta como parada" \
    's#^        if puede_estar_en_marcha "\$previa" \&\& #        if en_marcha "$previa" \&\& #' esc_previa_ilegible comp_parada

  esc_reintento() { saliente_anterior; STOP_FALLA="$LEGADO_STG" KILL_FALLA="$LEGADO_STG" bash "$1" desplegar staging "$B" > /dev/null 2>&1
                    STOP_FALLA="$LEGADO_STG" KILL_FALLA="$LEGADO_STG" bash "$1" desplegar staging "$C" > /dev/null 2>&1; codigo=$?; }
  mutar "el reintento solo mira lo anotado en el fichero" \
    's#^        if puede_estar_en_marcha "\$c" \&\& saliente_ignora_la_guarda "\$entorno" "\$sha" "\$servicio" "\$c"; then$#        if false; then#' esc_reintento comp_parada

  esc_ausente() { saliente_previa; rm -rf "$ESTADO/c/$VERDE_STG"; bash "$1" desplegar staging "$B" > /dev/null 2>&1; codigo=$?; }
  comp_ausente() { [ "$codigo" != 0 ] && echo si || echo no; }
  mutar "un contenedor inexistente cuenta como vivo" \
    's#^        \*\[Nn\]"o such object"\*|\*\[Nn\]"o such container"\*) return 1 ;;$#        nunca) return 1 ;;#' esc_ausente comp_ausente

  esc_pendientes() { saliente_anterior; STOP_FALLA="$LEGADO_STG" KILL_FALLA="$LEGADO_STG" bash "$1" desplegar staging "$B" > /dev/null 2>&1
                     STOP_FALLA="$LEGADO_STG" KILL_FALLA="$LEGADO_STG" bash "$1" retirar-sin-guarda staging "$B" > /dev/null 2>&1; codigo=$?; }
  mutar "retirar-sin-guarda sale bien con la imagen viva" \
    's#^    return "\$fallo"$#    return 0#' esc_pendientes comp_parada

  esc_reinicio() { saliente_staging; REINICIA="$AZUL_STG" bash "$1" desplegar staging "$B" > /dev/null 2>&1; codigo=$?; }
  mutar "no se quita la política de reinicio" \
    's#^        timeout "\$RELEVO_TOPE_KILL" docker update --restart=no "\$contenedor" > /dev/null 2>&1 || true$#        :#' esc_reinicio comp_ausente

  esc_reinicio_sin_update() { saliente_staging; REINICIA="$AZUL_STG" UPDATE_FALLA="$AZUL_STG" bash "$1" desplegar staging "$B" > /dev/null 2>&1; codigo=$?; }
  mutar "la parada no se vuelve a mirar tras la pausa" \
    's#^    sleep "\$RELEVO_ESPERA_PARADA"$#    return 0#' esc_reinicio_sin_update comp_parada

  esc_ranura_sin_anotar() { ranura_sin_anotar; STOP_FALLA="$VERDE_STG" KILL_FALLA="$VERDE_STG" bash "$1" desplegar staging "$B" > /dev/null 2>&1; codigo=$?; }
  mutar "solo se busca sin anotar el contenedor anterior a las ranuras" \
    's#^    for c in "\$base" "\$base-azul" "\$base-verde"; do$#    for c in "$base"; do#' esc_ranura_sin_anotar comp_parada

  esc_salud() { saliente_staging; INSPECT_SALUD_FALLA=caemanager-staging-app-verde bash "$1" desplegar staging "$B" > /dev/null 2>&1; }
  mutar "un fallo al leer la salud cuenta como sin healthcheck" \
    's#^    salud="\$(docker inspect -f .{{if .State.Health}}{{.State.Health.Status}}{{end}}. "\$1" 2>/dev/null)" || return 1$#    salud=""#' esc_salud comp_sal_insana

  esc_sal_ok() { limpio; contenedor caemanager-staging-app-azul "$A"; fichero staging "to caemanager-staging-app-azul:8080"; fichero produccion "to caemanager-app-azul:8080"
                 bash "$1" desplegar staging "$B" > /dev/null 2>&1; }
  comp_sal_ok() { [ "$(en_marcha caemanager-staging-app-azul)" = false ] && echo si || echo no; }
  mutar "se corta también a la saliente que declara la guarda" \
    's#^    declara_guarda_de_correo "\$saliente" \&\& return 1$#    :#' esc_sal_ok comp_sal_ok

  esc_sal_prod() { limpio; contenedor caemanager-app-azul "$A"; sin_guarda caemanager-app-azul; fichero produccion "to caemanager-app-azul:8080"; fichero staging "to caemanager-staging-app-azul:8080"
                   bash "$1" desplegar produccion "$B" > /dev/null 2>&1; }
  comp_sal_prod() { [ "$(en_marcha caemanager-app-azul)" = false ] && echo si || echo no; }
  mutar "producción corta la saliente sin la etiqueta aunque el modo sea Abierto" \
    's#^    ! medir_guarda_de_correo "\$entorno" "\$sha" "\$servicio" "\$MODO_CORREO_SIN_GUARDA"$#    true#' esc_sal_prod comp_sal_prod
fi

if [ "$fallos" -gt 0 ]; then
  echo "$fallos comprobación(es) fallaron."
  exit 1
fi
echo "Todas las comprobaciones pasaron."
