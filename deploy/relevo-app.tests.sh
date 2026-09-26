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
case "$1" in
  inspect)
    if [ "$2" = "-f" ]; then fmt=$3; nombre=$4; else fmt=""; nombre=$2; fi
    [ -d "$(c "$nombre")" ] || exit 1
    case "$fmt" in
      *State.Running*) cat "$(c "$nombre")/en_marcha" ;;
      *Health*) cat "$(c "$nombre")/salud" 2>/dev/null || echo healthy ;;
      *.Id*) cat "$(c "$nombre")/id" ;;
      *Config.Image*) cat "$(c "$nombre")/imagen" ;;
      *Mounts*) cat "$(c "$nombre")/montajes" 2>/dev/null; echo ;;
      "") echo "{}" ;;
    esac ;;
  exec)
    shift; [ "$1" = "-i" ] && shift
    nombre=$1; shift
    case "$*" in
      "caddy reload"*)
        marca="$(grep -o 'MARCA_[A-Z]*' | head -1)"; echo "FUENTE ${marca:-?}" >> "$LOG"
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
      *" up "*" caddy")   # transición: recrear Caddy con el montaje
        mkdir -p "$(c caemanager-caddy)"; echo "/etc/caddy/Caddyfile /etc/caddy/ranuras /data" > "$(c caemanager-caddy)/montajes" ;;
      *" up "*)
        [ "${COMPOSE_FALLA:-0}" = 1 ] && exit 1
        todo="$*"; ranura="${todo##* app-}"; pref=caemanager-app; case "$*" in *staging*) pref=caemanager-staging-app ;; esac
        d="$(c "$pref-$ranura")"; mkdir -p "$d"; echo true > "$d/en_marcha"; nuevo_id > "$d/id"
        if [ "${COMPOSE_NO_CAMBIA:-0}" = 1 ]; then echo "caemanager:otra" > "$d/imagen"; else echo "caemanager:$IMAGEN_TAG" > "$d/imagen"; fi ;;
    esac ;;
  stop) nombre="${*: -1}"; [ -d "$(c "$nombre")" ] && echo false > "$(c "$nombre")/en_marcha" ;;
  rm) rm -rf "$(c "$2")" ;;
  logs) ;;
esac
exit 0
EOF
cat > "$TMP/bin/systemd-run" <<'EOF'
#!/bin/bash
echo "systemd-run $*" >> "$LOG"
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
export RAIZ_DESPLIEGUE="$TMP/raiz" DIR_RANURAS="$TMP/ranuras" DRENAJE_INTERVALO=0
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
comprobar "Caddy crea al arrancar los ficheros que falten, con los contenedores anteriores a P1-F2" "1 1" \
  "$(grep -c "\[ -f /etc/caddy/ranuras/produccion.caddy \] || echo 'to caemanager-app:8080' > /etc/caddy/ranuras/produccion.caddy;" "$AQUI/local/docker-compose.produccion.yml") $(grep -c "\[ -f /etc/caddy/ranuras/staging.caddy \] || echo 'to caemanager-staging-app:8080' > /etc/caddy/ranuras/staging.caddy;" "$AQUI/local/docker-compose.produccion.yml")"
comprobar "el valor por defecto de DIR_RANURAS es ese directorio" 1 \
  "$(grep -cx 'DIR_RANURAS="${DIR_RANURAS:-/var/lib/talveg/caddy-ranuras}"' "$GUION")"
cierre="$(sed -n 's/^\tstream_close_delay \([0-9]*\)m$/\1/p; s/^\tstream_close_delay \([0-9]*\)h$/\1*60/p' "$CADDYFILE")"; cierre="$(( ${cierre:-0} ))"
max_prod="$(sed -n 's/^DRENAJE_MAX_PRODUCCION="${DRENAJE_MAX_PRODUCCION:-\([0-9]*\)}"$/\1/p' "$GUION")"
max_stg="$(sed -n 's/^DRENAJE_MAX_STAGING="${DRENAJE_MAX_STAGING:-\([0-9]*\)}"$/\1/p' "$GUION")"
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
for args in "" "desplegar" "desplegar produccion" "desplegar pre $A" "desplegar produccion corto" "activa" "activa x" "drenar produccion c i" "drenar produccion c i x" "otra"; do
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
comprobar "para el drenaje anterior del entorno antes de empezar" si "$(contiene "systemctl stop talveg-drenaje-produccion" "$(log)")"
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
salida=$(timeout 5 bash "$GUION" drenar produccion caemanager-app-azul id-caemanager-app-azul 0 2>&1)
comprobar "con la activa no sana, la saliente no se retira (puede ser lo único que sirve)" "true to caemanager-app-verde:8080 caemanager-app-azul:8080" \
  "$(en_marcha caemanager-app-azul) $(ranuras produccion)"

echo "ci-deploy.sh y volver-atras.sh lo usan"
comprobar "ci-deploy.sh hace el relevo en vez de un up de todo el stack" "1 0" \
  "$(grep -c '^    if ! bash /opt/talveg/deploy/relevo-app.sh desplegar "\$ENTORNO" "\$SHA" < /dev/null; then$' "$AQUI/ci-deploy.sh") $(grep -cE '^[^#]*docker compose [^|]* up ' "$AQUI/ci-deploy.sh")"
comprobar "volver-atras.sh también" "1 0" \
  "$(grep -c 'bash "\$RELEVO_APP" desplegar "\$entorno" "\$sha"' "$AQUI/volver-atras.sh") $(grep -cE '^[^#]*docker compose [^|]* up ' "$AQUI/volver-atras.sh")"

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
    's#^            escribir_ranuras "\$entorno" "\$activa_"$#            :#' esc_recarga comp_recarga

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
fi

if [ "$fallos" -gt 0 ]; then
  echo "$fallos comprobación(es) fallaron."
  exit 1
fi
echo "Todas las comprobaciones pasaron."
