#!/bin/bash
# Tests de deploy/volver-atras.sh, deploy/imagenes-retenidas.sh y
# deploy/listar-migraciones-ef.sh (P1-F1), y de cómo los enganchan ci-deploy.sh
# y deploy.yml.
#
# `docker` y `flock` son dobles: el estado del VPS (qué imagen corre cada
# contenedor, qué imágenes hay cargadas con qué etiquetas, qué migraciones tiene
# la base, si /salud responde) vive en ficheros bajo $TMP/estado, y cada
# llamada a docker se anota en $DOCKER_LOG. Corren en CI sin VPS ni Docker.
#
# Al final, prueba de sensibilidad dentro del propio test: se aplican
# mutaciones a una COPIA de volver-atras.sh (la comprobación de /salud, la de la
# imagen en marcha y la del esquema), se comprueba que cada mutación cambió el
# fichero y que el caso que la debe cazar pasa a dar el resultado equivocado.

set -uo pipefail

AQUI="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
GUION="${VOLVER_ATRAS_GUION:-$AQUI/volver-atras.sh}"
RETENIDAS="$AQUI/imagenes-retenidas.sh"
LISTAR="$AQUI/listar-migraciones-ef.sh"

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
mkdir -p "$TMP/bin"

A=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
B=bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb
C=cccccccccccccccccccccccccccccccccccccccc
D=dddddddddddddddddddddddddddddddddddddddd
MIGS="20260101000000_Inicial,20260201000000_Señal"

cat > "$TMP/bin/docker" <<'EOF'
#!/bin/bash
# Doble de docker sobre el estado de $ESTADO.
echo "$* IMAGEN_TAG=${IMAGEN_TAG:-}" >> "$DOCKER_LOG"
case "$1 ${2:-}" in
  "inspect "*)
    f="$ESTADO/contenedores/$2"; [ -f "$f" ] || exit 1; cat "$f" ;;
  "image inspect")
    sha="${3#caemanager:}"; d="$ESTADO/imagenes/$sha"; [ -d "$d" ] || exit 1
    case "$5" in
      *revision*) cat "$d/revision" 2>/dev/null || echo "$sha" ;;
      *migraciones-ef*) cat "$d/migraciones" 2>/dev/null || echo "<no value>" ;;  # lo que imprime Go con una etiqueta ausente
    esac ;;
  "image ls") ls "$ESTADO/imagenes" ;;
  "image rm") rm -rf "$ESTADO/imagenes/${3#caemanager:}" ;;
  "ps -a") cat "$ESTADO/contenedores/"* 2>/dev/null; true ;;
  "exec "*)
    case "$*" in
      *psql*) [ "${PSQL_FALLA:-0}" = 1 ] && exit 2; tr ',' '\n' < "$ESTADO/base" ;;
      *curl*)
        n=$(( $(cat "$ESTADO/salud_llamadas" 2>/dev/null || echo 0) + 1 )); echo "$n" > "$ESTADO/salud_llamadas"
        [ "$n" -gt "${SALUD_FALLA_PRIMERAS:-0}" ] || exit 7 ;;
    esac ;;
  "compose "*)
    case "$*" in
      *" up "*)
        [ "${COMPOSE_FALLA:-0}" = 1 ] && exit 1
        [ "${COMPOSE_NO_CAMBIA:-0}" = 1 ] || echo "caemanager:$IMAGEN_TAG" > "$ESTADO/contenedores/$APP" ;;
    esac ;;
esac
exit 0
EOF
cat > "$TMP/bin/flock" <<'EOF'
#!/bin/bash
[ "${FLOCK_FALLA:-0}" = 1 ] && exit 1
exit 0
EOF
chmod +x "$TMP/bin/docker" "$TMP/bin/flock"
export PATH="$TMP/bin:$PATH" DOCKER_LOG="$TMP/docker.log" ESTADO="$TMP/estado"
export DIR_HISTORIAL_DESPLIEGUES="$TMP/historial" CONFIG_DESPLIEGUE="$TMP/despliegue.conf"
export RAIZ_DESPLIEGUE="$TMP/raiz" VOLVER_ATRAS_ESPERA_SALUD=0 VOLVER_ATRAS_INTENTOS_SALUD=3
mkdir -p "$RAIZ_DESPLIEGUE/deploy/local"

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

# escenario ENTORNO EN_MARCHA -> estado limpio: el contenedor app corre
# caemanager:EN_MARCHA, la base tiene $MIGS, historial vacío, sin imágenes.
escenario() {
  rm -rf "$ESTADO" "$DIR_HISTORIAL_DESPLIEGUES" "$CONFIG_DESPLIEGUE"
  mkdir -p "$ESTADO/contenedores" "$ESTADO/imagenes"
  APP=caemanager-app; [ "$1" = staging ] && APP=caemanager-staging-app
  export APP
  echo "caemanager:$2" > "$ESTADO/contenedores/$APP"
  printf '%s' "$MIGS" > "$ESTADO/base"
  : > "$DOCKER_LOG"
}
# imagen SHA [MIGRACIONES|-sin] [REVISION]
imagen() {
  mkdir -p "$ESTADO/imagenes/$1"
  [ "${2:-}" = "-sin" ] || printf '%s' "${2:-$MIGS}" > "$ESTADO/imagenes/$1/migraciones"
  [ -z "${3:-}" ] || echo "$3" > "$ESTADO/imagenes/$1/revision"
}
historial() { local e=$1; shift; mkdir -p "$DIR_HISTORIAL_DESPLIEGUES"; for s in "$@"; do echo "2026-09-26T00:00:00Z $s" >> "$DIR_HISTORIAL_DESPLIEGUES/$e"; done; }
# volver [VAR=valor ...] -- ARGS -> $codigo, $salida
volver() {
  local vars=()
  while [ "$1" != "--" ]; do vars+=("$1"); shift; done; shift
  salida=$(env "${vars[@]}" bash "$GUION" "$@" 2>&1); codigo=$?
}
llamadas_up() { grep -c " up " "$DOCKER_LOG" || true; }

echo "listar-migraciones-ef.sh"
ids="$(bash "$LISTAR" "$AQUI/..")"; codigo=$?
comprobar "sale con 0 sobre el repositorio" 0 "$codigo"
esperado="$(grep -rhoE '\[Migration\("[^"]*"\)\]' --include='*.cs' "$AQUI/../src/CaeManager.Migrations.PostgreSQL/Migrations" | wc -l)"
comprobar "una entrada por [Migration] del proyecto" "$esperado" "$(printf '%s\n' "$ids" | tr ',' '\n' | wc -l)"
comprobar "ordenadas y sin repetir" "$(printf '%s' "$ids" | tr ',' '\n' | LC_ALL=C sort -u | paste -sd, -)" "$ids"
mkdir -p "$TMP/repo-vacio/src/CaeManager.Migrations.PostgreSQL/Migrations"
bash "$LISTAR" "$TMP/repo-vacio" > /dev/null 2>&1; comprobar "sin migraciones falla" 1 "$?"
d="$TMP/repo-doble/src/CaeManager.Migrations.PostgreSQL/Migrations"; mkdir -p "$d"
echo '[Migration("20260101000000_X")]' > "$d/a.Designer.cs"; echo '[Migration("20260101000000_X")]' > "$d/b.Designer.cs"
bash "$LISTAR" "$TMP/repo-doble" > /dev/null 2>&1; comprobar "un identificador repetido falla" 1 "$?"

echo "imagenes-retenidas.sh"
escenario produccion "$C"
bash "$RETENIDAS" registrar produccion "$A" > /dev/null
bash "$RETENIDAS" registrar produccion "$B" > /dev/null
comprobar "registrar añade <fecha> <sha>" "$A $B" "$(awk '{print $2}' "$DIR_HISTORIAL_DESPLIEGUES/produccion" | paste -sd' ')"
bash "$RETENIDAS" registrar otro "$A" > /dev/null 2>&1; comprobar "registrar rechaza un entorno desconocido" 1 "$?"
bash "$RETENIDAS" registrar staging latest > /dev/null 2>&1; comprobar "registrar rechaza lo que no es un SHA" 1 "$?"

escenario produccion "$C"; historial produccion "$A" "$B" "$C" "$C"
comprobar "anterior de C es B (saltando repeticiones)" "$B" "$(bash "$RETENIDAS" anterior produccion "$C")"
comprobar "anterior de B es A" "$A" "$(bash "$RETENIDAS" anterior produccion "$B")"
bash "$RETENIDAS" anterior produccion "$A" > /dev/null 2>&1; comprobar "antes de A no hay nada" 1 "$?"
bash "$RETENIDAS" anterior produccion "$D" > /dev/null 2>&1; comprobar "un SHA fuera del historial no tiene anterior" 1 "$?"
escenario produccion "$C"; historial produccion "$A" "$B" "$A"
comprobar "cuenta la ÚLTIMA aparición del SHA actual" "$B" "$(bash "$RETENIDAS" anterior produccion "$A")"

n() { env "$@" bash -c "source '$RETENIDAS'; imagenes_retenidas_n" 2>/dev/null; }
escenario produccion "$C"
comprobar "N por defecto es 5" 5 "$(n)"
comprobar "N desde IMAGENES_RETENIDAS" 8 "$(n IMAGENES_RETENIDAS=8)"
printf 'OTRA=1\nIMAGENES_RETENIDAS=3\n' > "$CONFIG_DESPLIEGUE"
comprobar "N desde el fichero de configuración" 3 "$(n)"
comprobar "la variable manda sobre el fichero" 7 "$(n IMAGENES_RETENIDAS=7)"
for malo in 1 51 abc '$(touch /tmp/x)'; do
  n IMAGENES_RETENIDAS="$malo" > /dev/null; comprobar "N='$malo' se rechaza" 1 "$?"
done

# Retención: 7 despliegues de staging (S1..S7), 3 de producción (P1..P3, P1=S2),
# una imagen que solo usa un contenedor parado (U) y una etiqueta que no es SHA.
sha() { printf '%040d' "$1"; }
escenario_retencion() {
  escenario produccion "$(sha 23)"
  for i in 1 2 3 4 5 6 7; do historial staging "$(sha "1$i")"; imagen "$(sha "1$i")"; done
  historial produccion "$(sha 12)" "$(sha 22)" "$(sha 23)"; imagen "$(sha 22)"; imagen "$(sha 23)"
  imagen "$(sha 99)"; echo "caemanager:$(sha 99)" > "$ESTADO/contenedores/parado"
  imagen "$(sha 88)"; imagen sin-etiqueta
}
escenario_retencion
IMAGENES_RETENIDAS=5 bash "$RETENIDAS" retener > /dev/null
comprobar "retener quita solo lo que excede N por entorno" \
  "$(printf '%s\n' "$(sha 11)" "$(sha 88)" | sort | paste -sd' ')" \
  "$(sed -n 's/^image rm caemanager:\([^ ]*\) .*/\1/p' "$DOCKER_LOG" | sort | paste -sd' ')"
comprobar "quedan 5 de staging, producción, la usada y la no-SHA" 10 "$(ls "$ESTADO/imagenes" | wc -l)"
comprobar "retener poda las colgantes de despliegue (sin -a)" 1 \
  "$(grep -cx 'image prune -f --filter label=es.talveg.despliegue IMAGEN_TAG=' "$DOCKER_LOG")"
# Historial largo (hallazgo de Codex): 20000 SHAs distintos superan el búfer de
# la tubería; con `| head -n N` el SIGPIPE abortaba retener sin borrar nada.
escenario produccion "$(sha 23)"
mkdir -p "$DIR_HISTORIAL_DESPLIEGUES"
awk 'BEGIN { for (i = 1; i <= 20000; i++) printf "2026-09-26T00:00:00Z %040d\n", 100000 + i }' > "$DIR_HISTORIAL_DESPLIEGUES/produccion"
historial staging "$(printf '%040d' 120000)"
imagen "$(printf '%040d' 100001)"; imagen "$(printf '%040d' 120000)"
IMAGENES_RETENIDAS=5 bash "$RETENIDAS" retener > /dev/null 2>&1
comprobar "historial de 20000 entradas: retener termina con 0" 0 "$?"
comprobar "  y retira la antigua conservando la última" "$(printf '%040d' 120000)" "$(ls "$ESTADO/imagenes" | paste -sd' ')"
: > "$DOCKER_LOG"
IMAGENES_RETENIDAS=1 bash "$RETENIDAS" retener > /dev/null 2>&1; comprobar "con N inválido retener falla" 1 "$?"
comprobar "con N inválido no retira nada" 0 "$(grep -c '^image rm' "$DOCKER_LOG")"

# Falla cerrado (hallazgo de Codex posterior a #922): sin historial legible, o
# si el registro del despliegue en curso no llegó a él, la lista de retenidas
# se reducía a las que usa un contenedor y se borraban las demás, también las
# que guarda volver-atras.sh. Ahora no se retira ninguna :<sha>, se avisa, se
# sale con 0 (el despliegue no falla) y la poda de colgantes sí se hace.
# retener_sin_retirar CASO [ARGS...] -> sobre el escenario ya perturbado.
retener_sin_retirar() {
  local caso=$1; shift
  local antes err c
  antes="$(ls "$ESTADO/imagenes" | sort | paste -sd' ')"
  : > "$DOCKER_LOG"
  err="$(IMAGENES_RETENIDAS=5 bash "$RETENIDAS" retener "$@" 2>&1 > /dev/null)"; c=$?
  comprobar "$caso: retener sale con 0" 0 "$c"
  comprobar "$caso: no retira ninguna imagen" 0 "$(grep -c '^image rm' "$DOCKER_LOG")"
  comprobar "$caso: las retenidas siguen cargadas" "$antes" "$(ls "$ESTADO/imagenes" | sort | paste -sd' ')"
  comprobar "$caso: lo avisa" si "$(contiene '::warning::retención de imágenes omitida' "$err")"
  comprobar "$caso: poda igualmente las colgantes (sin -a)" 1 \
    "$(grep -cx 'image prune -f --filter label=es.talveg.despliegue IMAGEN_TAG=' "$DOCKER_LOG")"
}
escenario_retencion; rm "$DIR_HISTORIAL_DESPLIEGUES/produccion"
retener_sin_retirar "sin historial de producción"
escenario_retencion; rm -rf "$DIR_HISTORIAL_DESPLIEGUES"
retener_sin_retirar "sin directorio de historial"
escenario_retencion; rm "$DIR_HISTORIAL_DESPLIEGUES/staging"; mkdir "$DIR_HISTORIAL_DESPLIEGUES/staging"
retener_sin_retirar "historial de staging que no es un fichero"
escenario_retencion; printf 'basura\n2026-09-26T00:00:00Z latest\n' > "$DIR_HISTORIAL_DESPLIEGUES/produccion"
retener_sin_retirar "historial de producción sin entradas válidas"
# Solo donde chmod de verdad quita la lectura: root lee igual y Git Bash en
# Windows ignora el modo; ahí lo cubre el caso del directorio de arriba.
escenario_retencion; chmod 000 "$DIR_HISTORIAL_DESPLIEGUES/produccion"
if [ ! -r "$DIR_HISTORIAL_DESPLIEGUES/produccion" ]; then
  retener_sin_retirar "historial de producción sin permiso de lectura"
else
  echo "  --   historial sin permiso de lectura: omitido (chmod 000 no quita la lectura aquí)"
fi
chmod 644 "$DIR_HISTORIAL_DESPLIEGUES/produccion"
# Registro fallido: el despliegue de $(sha 24) no llegó al historial, cuya
# última entrada sigue siendo $(sha 23).
escenario_retencion
retener_sin_retirar "registro del despliegue en curso fallido" produccion "$(sha 24)"
# Registro fallido de verdad: `registrar` no puede escribir y `retener` con el
# mismo SHA no retira nada.
escenario_retencion; rm "$DIR_HISTORIAL_DESPLIEGUES/produccion"; mkdir "$DIR_HISTORIAL_DESPLIEGUES/produccion"
bash "$RETENIDAS" registrar produccion "$(sha 24)" > /dev/null 2>&1
comprobar "registrar falla si no puede escribir el historial" 1 "$?"
retener_sin_retirar "tras un registrar fallido" produccion "$(sha 24)"
# Control positivo: con el SHA en curso como última entrada, sí retira.
escenario_retencion; : > "$DOCKER_LOG"
IMAGENES_RETENIDAS=5 bash "$RETENIDAS" retener produccion "$(sha 23)" > /dev/null 2>&1
comprobar "con el registro en su sitio, retener <entorno> <sha> sí retira el exceso" 2 "$(grep -c '^image rm' "$DOCKER_LOG")"
IMAGENES_RETENIDAS=5 bash "$RETENIDAS" retener produccion latest > /dev/null 2>&1
comprobar "retener rechaza lo que no es un SHA" 1 "$?"

echo "volver-atras.sh"
escenario produccion "$B"; historial produccion "$A" "$B"; imagen "$A"; imagen "$B"
volver -- produccion anterior
comprobar "anterior: sale con 0" 0 "$codigo"
comprobar "anterior: completa" si "$(contiene "VUELTA ATRÁS COMPLETADA: produccion corre caemanager:$A" "$salida")"
comprobar "anterior: up sin build del stack de producción con IMAGEN_TAG=A" si \
  "$(contiene "compose -f docker-compose.produccion.yml up -d --wait --wait-timeout 180 --no-build IMAGEN_TAG=$A" "$(cat "$DOCKER_LOG")")"
comprobar "anterior: nunca build, load ni pull" 0 "$(grep -cE '(^| )(build|load|pull)( |$)' "$DOCKER_LOG")"
comprobar "anterior: consulta /salud" si "$(contiene "exec caemanager-app curl -fsS --max-time 5 http://localhost:8080/salud" "$(cat "$DOCKER_LOG")")"

escenario staging "$B"; imagen "$A"
volver -- staging "$A"
comprobar "staging con SHA explícito: sale con 0" 0 "$codigo"
comprobar "staging: usa su compose y su .env" si \
  "$(contiene "compose -f docker-compose.staging.yml --env-file .env.staging up" "$(cat "$DOCKER_LOG")")"
comprobar "staging: lee la base de staging" si "$(contiene "exec caemanager-staging-db psql" "$(cat "$DOCKER_LOG")")"

escenario produccion "$B"; imagen "$A"; printf '%s,20260301000000_Nueva' "$MIGS" > "$ESTADO/base"
volver -- produccion "$A"
comprobar "base con migración que la imagen no conoce: se detiene" 1 "$codigo"
comprobar "  y dice que nunca hace downgrade" si "$(contiene "nunca hace un downgrade" "$salida")"
comprobar "  y nombra la migración" si "$(contiene "20260301000000_Nueva" "$salida")"
comprobar "  y no arranca nada" 0 "$(llamadas_up)"

escenario produccion "$B"; imagen "$A" "$MIGS,20260301000000_Nueva"
volver -- produccion "$A"
comprobar "imagen con migración que la base no tiene: se detiene" 1 "$codigo"
comprobar "  explicando que sería un despliegue" si "$(contiene "eso es un despliegue, no una vuelta atrás" "$salida")"
comprobar "  y no arranca nada" 0 "$(llamadas_up)"

escenario produccion "$B"; imagen "$A" -sin
volver -- produccion "$A"
comprobar "imagen sin inventario de migraciones: se detiene" 1 "$codigo"
comprobar "  y lo explica" si "$(contiene "no lleva la etiqueta es.talveg.migraciones-ef" "$salida")"
comprobar "  y no arranca nada" 0 "$(llamadas_up)"

escenario produccion "$B"; imagen "$A"
volver PSQL_FALLA=1 -- produccion "$A"
comprobar "base ilegible: se detiene sin arrancar" "1 0" "$codigo $(llamadas_up)"

escenario produccion "$B"; printf '' > "$ESTADO/base"; imagen "$A"
volver -- produccion "$A"
comprobar "historial de migraciones vacío: se detiene sin arrancar" "1 0" "$codigo $(llamadas_up)"

escenario produccion "$B"
volver -- produccion "$A"
comprobar "imagen que no está en el VPS: se detiene sin reconstruir" "1 0" "$codigo $(llamadas_up)"
comprobar "  y lo dice" si "$(contiene "no está en el VPS" "$salida")"

escenario produccion "$B"; imagen "$A" "$MIGS" "$D"
volver -- produccion "$A"
comprobar "revisión OCI distinta del SHA: se detiene" "1 0" "$codigo $(llamadas_up)"

escenario produccion "$B"; imagen "$A"
volver SALUD_FALLA_PRIMERAS=99 -- produccion "$A"
comprobar "/salud no responde: no la da por buena" 1 "$codigo"
comprobar "  y lo dice" si "$(contiene "no está sano" "$salida")"
comprobar "  tras agotar los intentos" 3 "$(cat "$ESTADO/salud_llamadas")"

escenario produccion "$B"; imagen "$A"
volver SALUD_FALLA_PRIMERAS=2 -- produccion "$A"
comprobar "/salud responde al tercer intento: la da por buena" 0 "$codigo"

escenario produccion "$B"; imagen "$A"
volver COMPOSE_NO_CAMBIA=1 -- produccion "$A"
comprobar "el contenedor sigue en otra imagen tras el up: no la da por buena" 1 "$codigo"

escenario produccion "$B"; imagen "$A"
volver COMPOSE_FALLA=1 -- produccion "$A"
comprobar "up que no llega a sano: se detiene" 1 "$codigo"

escenario produccion "$A"; imagen "$A"
volver -- produccion "$A"
comprobar "destino igual al actual y sano: nada que hacer" "0 0" "$codigo $(llamadas_up)"
comprobar "  tras comprobar /salud" 1 "$(cat "$ESTADO/salud_llamadas")"

escenario produccion "$A"; imagen "$A"
volver SALUD_FALLA_PRIMERAS=99 -- produccion "$A"
comprobar "destino igual al actual pero insano: no devuelve éxito" "1 0" "$codigo $(llamadas_up)"

escenario produccion "$B"; historial produccion "$B"
volver -- produccion anterior
comprobar "anterior sin historial previo: se detiene" "1 0" "$codigo $(llamadas_up)"

escenario produccion "$B"; imagen "$A"
volver FLOCK_FALLA=1 -- produccion "$A"
comprobar "cerrojo ocupado: se detiene sin tocar nada" "1 0" "$codigo $(llamadas_up)"

for args in "produccion" "otro $A" "produccion $A extra" "produccion latest"; do
  escenario produccion "$B"; imagen "$A"
  # shellcheck disable=SC2086
  volver -- $args
  comprobar "argumentos '$args' rechazados sin arrancar" "si 0" "$([ "$codigo" -ne 0 ] && echo si || echo no) $(llamadas_up)"
done

echo "ci-deploy.sh y deploy.yml"
FUENTE="$AQUI/ci-deploy.sh"
linea() { grep -n -x -- "$1" "$FUENTE" | head -1 | cut -d: -f1 || true; }
L_UP="$(linea '    if ! docker compose "\${args\[@\]}" up -d --wait --wait-timeout 180 --no-build; then')"
L_REG="$(linea '    bash /opt/talveg/deploy/imagenes-retenidas.sh registrar "\$ENTORNO" "\$SHA" < /dev/null \\')"
L_RET="$(linea '    bash /opt/talveg/deploy/imagenes-retenidas.sh retener "\$ENTORNO" "\$SHA" < /dev/null \\')"
comprobar "ci-deploy registra y retiene tras un up sano, en ese orden" si \
  "$([ -n "$L_UP" ] && [ -n "$L_REG" ] && [ -n "$L_RET" ] && [ "$L_UP" -lt "$L_REG" ] && [ "$L_REG" -lt "$L_RET" ] && echo si || echo "no ($L_UP/$L_REG/$L_RET)")"
L_RET_PREVIA="$(linea 'bash /opt/talveg/deploy/imagenes-retenidas.sh retener < /dev/null \\' )"
L_LIBERAR="$(linea 'bash /opt/talveg/deploy/liberar-disco.sh < /dev/null')"
comprobar "ci-deploy retiene también antes de liberar disco y recibir la imagen" si \
  "$([ -n "$L_RET_PREVIA" ] && [ -n "$L_LIBERAR" ] && [ "$L_RET_PREVIA" -lt "$L_LIBERAR" ] && echo si || echo "no ($L_RET_PREVIA/$L_LIBERAR)")"
comprobar "ci-deploy llama a retener dos veces (antes y tras un up sano)" 2 \
  "$(grep -c 'bash /opt/talveg/deploy/imagenes-retenidas.sh retener' "$FUENTE")"
WF="$AQUI/../.github/workflows/deploy.yml"
comprobar "deploy.yml etiqueta la imagen para la retención" 1 "$(grep -c -- '--label "es.talveg.despliegue=caemanager"' "$WF")"
comprobar "deploy.yml graba el inventario de migraciones" 1 "$(grep -c -- '--label "es.talveg.migraciones-ef=\$MIGRACIONES_EF"' "$WF")"
comprobar "deploy.yml lo obtiene de listar-migraciones-ef.sh" 1 "$(grep -c 'MIGRACIONES_EF="\$(bash deploy/listar-migraciones-ef.sh .)"' "$WF")"
comprobar "deploy.yml etiqueta solo por SHA, nunca latest" "1 0" \
  "$(grep -c -- '-t "caemanager:\$SHA_DESPLEGADO"' "$WF") $(grep -cE 'caemanager:latest|-t caemanager( |$)' "$WF")"

# Prueba de sensibilidad: cada mutación tiene que cambiar el fichero y hacer
# que su caso dé el resultado equivocado (0 donde se esperaba 1).
if [ -z "${VOLVER_ATRAS_GUION:-}" ]; then
  echo "mutaciones"
  mutar() { # NOMBRE SED ESCENARIO...
    local nombre=$1 expr=$2; shift 2
    local dir="$TMP/mutante"; rm -rf "$dir"; mkdir -p "$dir"
    cp "$RETENIDAS" "$dir/"; sed "$expr" "$GUION" > "$dir/volver-atras.sh"
    if cmp -s "$GUION" "$dir/volver-atras.sh"; then
      comprobar "mutación '$nombre' cambia el guion" si no; return
    fi
    "$@"
    local c
    salida=$(env "${MUT_VARS[@]}" bash "$dir/volver-atras.sh" produccion "$A" 2>&1); c=$?
    comprobar "mutación '$nombre' la caza su caso (sale 0 donde se exige 1)" 0 "$c"
  }
  preparar() { escenario produccion "$B"; imagen "$A"; }
  MUT_VARS=(SALUD_FALLA_PRIMERAS=99)
  mutar "no se consulta /salud" 's#if docker exec "\$contenedor" curl -fsS --max-time 5 http://localhost:8080/salud > /dev/null; then#if true; then#' preparar
  MUT_VARS=(COMPOSE_NO_CAMBIA=1)
  mutar "no se mira qué imagen corre" 's#if \[ "\$imagen" != "\${REPOSITORIO_IMAGEN_DESPLIEGUE}:\${sha}" \]; then#if false; then#' preparar
  preparar_esquema() { preparar; printf '%s,20260301000000_Nueva' "$MIGS" > "$ESTADO/base"; }
  MUT_VARS=(X=1)
  mutar "no se mira si sobran migraciones" 's#if \[ -n "\$sobran" \]; then#if false; then#' preparar_esquema

  # Mutaciones de imagenes-retenidas.sh que devuelven el comportamiento previo
  # (seguir retirando sin historial legible o con el registro fallido): su
  # caso tiene que pasar a retirar imágenes.
  mutar_retenidas() { # NOMBRE SED ESCENARIO ARGS...
    local nombre=$1 expr=$2 prep=$3; shift 3
    local dir="$TMP/mutante-retenidas"; rm -rf "$dir"; mkdir -p "$dir"
    sed "$expr" "$RETENIDAS" > "$dir/imagenes-retenidas.sh"
    if cmp -s "$RETENIDAS" "$dir/imagenes-retenidas.sh"; then
      comprobar "mutación '$nombre' cambia el guion" si no; return
    fi
    "$prep"; : > "$DOCKER_LOG"
    IMAGENES_RETENIDAS=5 bash "$dir/imagenes-retenidas.sh" retener "$@" > /dev/null 2>&1
    comprobar "mutación '$nombre' la caza su caso (retira imágenes)" si \
      "$([ "$(grep -c '^image rm' "$DOCKER_LOG")" -gt 0 ] && echo si || echo no)"
  }
  sin_historial_produccion() { escenario_retencion; rm "$DIR_HISTORIAL_DESPLIEGUES/produccion"; }
  historial_ilegible() { escenario_retencion; rm "$DIR_HISTORIAL_DESPLIEGUES/staging"; mkdir "$DIR_HISTORIAL_DESPLIEGUES/staging"; }
  # Vuelta al comportamiento de #922: el motivo nunca corta y un historial que
  # no se puede leer cuenta como vacío.
  M_PREVIO='s#^        \[ -z "\$motivo" \] || break$#        motivo=""#; s#^    \[ -f "\$fichero" \] && \[ -r "\$fichero" \] || return 1$#    [ -r "$fichero" ] || return 0#'
  mutar_retenidas "sin historial se sigue retirando" "$M_PREVIO" sin_historial_produccion
  mutar_retenidas "historial ilegible se sigue retirando" "$M_PREVIO" historial_ilegible
  mutar_retenidas "no se comprueba el registro en curso" \
    's#^    if \[ -z "\$motivo" \] && \[ -n "\$entorno_actual" \] \\$#    if false \\#' escenario_retencion produccion "$(sha 24)"
fi

if [ "$fallos" -gt 0 ]; then
  echo "$fallos comprobación(es) fallaron."
  exit 1
fi
echo "Todas las comprobaciones pasaron."
