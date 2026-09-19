#!/bin/bash
# Pruebas del modo "muestreo-memoria" de ci-deploy.sh (REC-196/P33), definido y
# ejecutado TAL CUAL (mismo criterio que ci-deploy-diagnostico-memoria.tests.sh:
# `source`, sin copiar la lógica). `docker`, `free`, `sleep` y `timeout` se
# sustituyen por funciones de shell o por ejecutables falsos en el PATH; este
# entorno no tiene el daemon. Necesita `flock` (util-linux): en CI (ubuntu) y en
# WSL sí está.
#
# Lo que estas pruebas fijan, y las mutaciones que las ponen en rojo:
#   - la validación es sobre la orden ENTERA y con rangos duros (420 s, 2 s);
#   - el muestreo NO toma el cerrojo de despliegue y no arranca (ni sigue) si
#     hay un despliegue en curso;
#   - nunca hay más muestras que duración/intervalo + 1, con independencia del
#     reloj;
#   - solo se invocan subcomandos de docker de lectura;
#   - el modo se despacha ANTES del cerrojo y de cualquier otra cosa de main().
set -euo pipefail

DIR_GUION="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
FICHERO_FUENTE="$DIR_GUION/ci-deploy.sh"
command -v flock >/dev/null || { echo "FALLO: este test necesita flock (util-linux)" >&2; exit 1; }
source "$FICHERO_FUENTE"

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
export FICHERO_CERROJO_DESPLIEGUE="$TMP/.ci-deploy.lock"

fallo() { echo "FALLO: $*" >&2; exit 1; }

echo "=== Caso 1: la orden válida se acepta con sus rangos ==="
[ "$(validar_orden_muestreo 'muestreo-memoria 300 3')" = "300 3" ] || fallo "no acepta 'muestreo-memoria 300 3'"
[ "$(validar_orden_muestreo 'muestreo-memoria 420 2')" = "420 2" ] || fallo "no acepta el máximo 420/2"
[ "$(validar_orden_muestreo 'muestreo-memoria 10 10')" = "10 10" ] || fallo "no acepta el mínimo 10 s con intervalo = duración"
[ "$(validar_orden_muestreo 'muestreo-memoria 030 08')" = "30 8" ] || fallo "'030 08' debe leerse en decimal, no octal"
echo "OK: órdenes válidas aceptadas"

echo "=== Caso 2: todo lo demás se rechaza ==="
while IFS= read -r orden; do
    if validar_orden_muestreo "$orden" >/dev/null 2>&1; then
        fallo "se aceptó una orden que debía rechazarse: [$orden]"
    fi
done <<'EOF'
muestreo-memoria 421 2
muestreo-memoria 9 2
muestreo-memoria 300 1
muestreo-memoria 300 61
muestreo-memoria 5 10
muestreo-memoria 30 60
muestreo-memoria 60 2 extra
muestreo-memoria 60 2; rm -rf /
muestreo-memoria 60 2 && id
muestreo-memoria $(id) 2
muestreo-memoria 60 -2
muestreo-memoria -60 2
muestreo-memoria 6a 2
muestreo-memoria  60 2
muestreo-memoria 60  2
muestreo-memoria 0300 2
muestreo-memoria 60
muestreo-memoria
staging 60 2
secretos
EOF
# Los que solo se distinguen por un espacio (el editor podría recortarlos en el
# heredoc de arriba) se construyen a mano.
validar_orden_muestreo " muestreo-memoria 60 2" >/dev/null 2>&1 && fallo "se aceptó una orden con espacio al principio"
validar_orden_muestreo "muestreo-memoria 60 2 " >/dev/null 2>&1 && fallo "se aceptó una orden con espacio al final"
# Con salto de línea al final y con un segundo comando en otra línea: el bucle
# de arriba no puede llevarlos (leería línea a línea).
validar_orden_muestreo $'muestreo-memoria 60 2\n' >/dev/null 2>&1 && fallo "se aceptó una orden con salto de línea final"
validar_orden_muestreo $'muestreo-memoria 60 2\nrm -rf /' >/dev/null 2>&1 && fallo "se aceptó una orden con un segundo comando en otra línea"
validar_orden_muestreo "" >/dev/null 2>&1 && fallo "se aceptó la orden vacía"
echo "OK: tokens extra, metacaracteres, saltos de línea, signos, ceros de más y rangos rechazados"

echo "=== Caso 3: despliegue_en_curso con un cerrojo real ==="
rm -f "$FICHERO_CERROJO_DESPLIEGUE"
despliegue_en_curso && fallo "sin fichero de cerrojo nunca ha habido despliegue"
: > "$FICHERO_CERROJO_DESPLIEGUE"
despliegue_en_curso && fallo "con el cerrojo libre se declaró un despliegue en curso"
( exec 9>"$FICHERO_CERROJO_DESPLIEGUE"; flock -x 9; sleep 4 ) &
PID_CERROJO=$!
for _ in 1 2 3 4 5 6 7 8 9 10; do
    flock -n -s "$FICHERO_CERROJO_DESPLIEGUE" true 2>/dev/null || break
    sleep 0.2
done
despliegue_en_curso || fallo "con el cerrojo tomado en exclusiva no se detectó el despliegue en curso"
kill "$PID_CERROJO" 2>/dev/null || true; wait "$PID_CERROJO" 2>/dev/null || true
: > "$TMP/no-legible"; chmod 000 "$TMP/no-legible"
if [ ! -r "$TMP/no-legible" ]; then
    FICHERO_CERROJO_DESPLIEGUE="$TMP/no-legible" despliegue_en_curso || fallo "un cerrojo ilegible debe fallar cerrado (despliegue en curso)"
fi
echo "OK: libre, tomado y no legible (falla cerrado)"

# --- Mocks para los casos siguientes ------------------------------------------
: > "$FICHERO_CERROJO_DESPLIEGUE"
LLAMADAS_DOCKER="$TMP/llamadas-docker"; : > "$LLAMADAS_DOCKER"
CONTADOR_STATS="$TMP/contador-stats"; echo 0 > "$CONTADOR_STATS"
FLAG_CERROJO="$TMP/tenia-cerrojo"; rm -f "$FLAG_CERROJO"
timeout() { shift; "$@"; }
sleep() { :; }
free() { printf '%s\n' "              total  used  free" "Mem:           3800  1400  1400" "Swap:             0     0     0"; }
docker() {
    echo "$1" >> "$LLAMADAS_DOCKER"
    case "$1" in
        stats)
            local n; n="$(cat "$CONTADOR_STATS")"; n=$((n + 1)); echo "$n" > "$CONTADOR_STATS"
            # Salvaguarda del propio test: sin techo de iteraciones el bucle no acabaría nunca.
            [ "$n" -gt 1000 ] && { echo "SIN TECHO: más de 1000 muestras" >&2; kill -9 $$; }
            # Mientras "se muestrea", el cerrojo tiene que poder tomarse en exclusiva.
            flock -n -x "$FICHERO_CERROJO_DESPLIEGUE" true || echo tomado >> "$FLAG_CERROJO"
            echo "caemanager-app mem=200MiB / 3GiB 6% cpu=1%" ;;
        ps) printf '%s\n' caemanager-app caemanager-db otro-contenedor ;;
        inspect) echo "iniciado=2026-09-19T00:00:00Z reinicios=0 oom_docker=false" ;;
        exec) echo "memory.peak: 123456 " ;;
        *) echo "SUBCOMANDO NO PERMITIDO: $*" >&2; return 1 ;;
    esac
}
RAIZ_PROC="$TMP/proc"; mkdir -p "$RAIZ_PROC/pressure"
printf '%s\n' "some avg10=0.00 avg60=0.10 avg300=0.20 total=4242" "full avg10=0.00 avg60=0.00 avg300=0.00 total=1" > "$RAIZ_PROC/pressure/memory"
printf '%s\n' "nr_free_pages 1" "oom_kill 7" > "$RAIZ_PROC/vmstat"

echo "=== Caso 4: el techo de iteraciones vale duración/intervalo + 1, con independencia del reloj ==="
echo 0 > "$CONTADOR_STATS"
SALIDA4="$(muestreo_memoria 20 2 2>&1)"
[ "$(cat "$CONTADOR_STATS")" = "11" ] || fallo "20 s a 2 s debían dar 11 muestras, dieron $(cat "$CONTADOR_STATS")"
echo "$SALIDA4" | grep -q "Fin del muestreo: muestras=11 stats_ok=11 t_primera=0s volcado_inicial=2/2 volcado_final=2/2" || fallo "falta el cierre con los hechos (muestras, stats_ok, t_primera, volcados): $(echo "$SALIDA4" | grep -a "Fin del muestreo")"
echo "$SALIDA4" | grep -q "psi_some_total_us=4242 oom_kill=7" || fallo "faltan los contadores del host por muestra"
echo "$SALIDA4" | grep -q "memory.peak: 123456" || fallo "falta la lectura de cgroup (estado inicial/final)"
echo "$SALIDA4" | grep -q "caemanager-db" || fallo "falta el contenedor caemanager-db"
if echo "$SALIDA4" | grep -q "otro-contenedor"; then fallo "se leyó un contenedor que no es caemanager-*"; fi
echo 0 > "$CONTADOR_STATS"
muestreo_memoria 420 2 >/dev/null 2>&1
[ "$(cat "$CONTADOR_STATS")" = "211" ] || fallo "420 s a 2 s debían dar 211 muestras, dieron $(cat "$CONTADOR_STATS")"
echo "OK: 11 y 211 muestras, sin pasar de ahí aunque el reloj no avance"

echo "=== Caso 4b: el reloj también acota: con un reloj rápido corta antes que el techo de iteraciones ==="
echo 0 > "$CONTADOR_STATS"
sleep() { SECONDS=$((SECONDS + 10)); }
muestreo_memoria 20 2 >/dev/null 2>&1
[ "$(cat "$CONTADOR_STATS")" = "2" ] || fallo "con 10 s de reloj por iteración, 20 s debían dar 2 muestras, dieron $(cat "$CONTADOR_STATS")"
sleep() { :; }
echo "OK: el reloj corta la ventana a la duración pedida"

echo "=== Caso 5: no toma el cerrojo mientras muestrea ==="
[ ! -e "$FLAG_CERROJO" ] || fallo "el cerrojo de despliegue no se pudo tomar en exclusiva mientras se muestreaba: el muestreo lo tenía"
echo "OK: el cerrojo estuvo libre en cada muestra"

echo "=== Caso 6: solo subcomandos de docker de lectura ==="
PERMITIDOS=" stats ps inspect exec "
while IFS= read -r sub; do
    case "$PERMITIDOS" in *" $sub "*) ;; *) fallo "docker $sub no es un subcomando de lectura permitido" ;; esac
done < <(sort -u "$LLAMADAS_DOCKER")
echo "OK: solo $(sort -u "$LLAMADAS_DOCKER" | tr '\n' ' ')"

echo "=== Caso 7: con un despliegue en curso al empezar no muestrea ==="
echo 0 > "$CONTADOR_STATS"; : > "$LLAMADAS_DOCKER"
despliegue_en_curso() { return 0; }
set +e; SALIDA7="$(muestreo_memoria 20 2 2>&1)"; ESTADO7=$?; set -e
[ "$ESTADO7" -eq 3 ] || fallo "debía devolver 3 con un despliegue en curso, devolvió $ESTADO7"
[ "$(cat "$CONTADOR_STATS")" = "0" ] || fallo "muestreó pese al despliegue en curso"
[ ! -s "$LLAMADAS_DOCKER" ] || fallo "llamó a docker pese al despliegue en curso"
echo "$SALIDA7" | grep -q "despliegue en curso" || fallo "no explica por qué no muestrea"
echo "OK: devuelve 3 y no toca docker"

echo "=== Caso 8: si empieza un despliegue durante el muestreo, corta ==="
echo 0 > "$CONTADOR_STATS"
LLAMADAS_PROBE=0
despliegue_en_curso() { LLAMADAS_PROBE=$((LLAMADAS_PROBE + 1)); [ "$LLAMADAS_PROBE" -gt 4 ]; }
ESTADO8=0
SALIDA8="$(muestreo_memoria 60 2 2>&1)" || ESTADO8=$?
[ "$ESTADO8" -eq 4 ] || fallo "un muestreo cortado por un despliegue debe devolver 4, devolvió $ESTADO8"
echo "$SALIDA8" | grep -q "Muestreo INTERRUMPIDO tras 3 muestras" || fallo "no avisó del corte"
echo "$SALIDA8" | grep -q "Fin del muestreo" && fallo "un muestreo cortado no puede cerrar con «Fin del muestreo»"
[ "$(cat "$CONTADOR_STATS")" = "3" ] || fallo "debía cortar tras 3 muestras (la sonda 5 ya ve el despliegue), hizo $(cat "$CONTADOR_STATS")"
echo "$SALIDA8" | grep -q "Estado final de los contenedores" || fallo "tras el corte sigue faltando la lectura final"
echo "OK: corta tras 3 muestras, devuelve 4, no cierra como completo y deja la lectura final"

echo "=== Caso 9: main() despacha el modo antes del cerrojo y rechaza lo no válido sin tocar nada ==="
L_DESPACHO="$(grep -n '^if \[ "\$ENTORNO" = "muestreo-memoria" \]; then$' "$FICHERO_FUENTE" | head -1 | cut -d: -f1)"
L_CERROJO="$(grep -n '^if ! flock -w 600 9; then$' "$FICHERO_FUENTE" | head -1 | cut -d: -f1)"
L_RESOLVE="$(grep -n 'resolve-deploy-sha.sh /opt/talveg' "$FICHERO_FUENTE" | head -1 | cut -d: -f1)"
L_CASE="$(grep -n '^case "\$ENTORNO" in$' "$FICHERO_FUENTE" | head -1 | cut -d: -f1)"
[ -n "$L_DESPACHO" ] && [ -n "$L_CERROJO" ] && [ -n "$L_RESOLVE" ] && [ -n "$L_CASE" ] \
    || fallo "no se localizó el despacho/cerrojo/resolve/case (despacho=$L_DESPACHO cerrojo=$L_CERROJO resolve=$L_RESOLVE case=$L_CASE)"
[ "$L_DESPACHO" -lt "$L_CASE" ] && [ "$L_DESPACHO" -lt "$L_CERROJO" ] && [ "$L_DESPACHO" -lt "$L_RESOLVE" ] \
    || fallo "el despacho de muestreo-memoria (línea $L_DESPACHO) debe ir antes del case ($L_CASE), del cerrojo ($L_CERROJO) y del checkout ($L_RESOLVE)"
# Ejecución real del guion completo, con la orden hostil en SSH_ORIGINAL_COMMAND.
# Un guion falso de docker en el PATH detecta cualquier llamada que se le cuele.
BIN_FALSO="$TMP/bin"; mkdir -p "$BIN_FALSO"
printf '#!/bin/sh\necho "$@" >> "%s/docker-real.log"\nexit 1\n' "$TMP" > "$BIN_FALSO/docker"; chmod +x "$BIN_FALSO/docker"
# Las variantes solo de espacios (doble espacio, tabulador) prueban que main() valida
# la orden ENTERA y no lo que `read` reconstruye tras colapsar los blancos.
for hostil in "muestreo-memoria 60 2 extra" "muestreo-memoria 999 2" "muestreo-memoria 60 2; touch $TMP/pwned" "muestreo-memoria  60 2" $'muestreo-memoria	60 2'; do
    set +e
    SALIDA9="$(PATH="$BIN_FALSO:$PATH" SSH_ORIGINAL_COMMAND="$hostil" bash "$FICHERO_FUENTE" 2>&1)"; ESTADO9=$?
    set -e
    [ "$ESTADO9" -eq 1 ] || fallo "[$hostil] debía salir con 1, salió con $ESTADO9"
    echo "$SALIDA9" | grep -q "Orden de muestreo no válida" || fallo "[$hostil] no dio el mensaje de orden no válida: $SALIDA9"
done
[ ! -e "$TMP/pwned" ] || fallo "se ejecutó un comando inyectado"
[ ! -e "$TMP/docker-real.log" ] || fallo "se llamó a docker con una orden no válida"
echo "OK: despacho anterior al cerrojo; las órdenes hostiles salen con 1 sin tocar docker ni ejecutar nada"

echo "=== Caso 10: la duración pedida acota el comando entero, aunque docker sea lento en las lecturas de cgroup ==="
eval "$(declare -f docker | sed '1s/^docker/docker_base/')"
INSPECCIONES="$TMP/inspecciones"; : > "$INSPECCIONES"
docker() {
    # Cada `inspect` "tarda" 100 s de reloj: mucho más que los 60 s pedidos.
    if [ "$1" = inspect ]; then echo x >> "$INSPECCIONES"; SECONDS=$((SECONDS + 100)); fi
    docker_base "$@"
}
echo 0 > "$CONTADOR_STATS"
ESTADO10=0
SALIDA10="$(muestreo_memoria 60 2 2>&1)" || ESTADO10=$?
# El subshell de $(...) no propaga SECONDS al padre, pero dentro de él la cuenta es coherente.
[ "$(wc -l < "$INSPECCIONES")" -eq 1 ] || fallo "con el plazo agotado tras el primer inspect no debía haber más: hubo $(wc -l < "$INSPECCIONES")"
echo "$SALIDA10" | grep -q "volcado cortado: agotado el plazo" || fallo "el volcado inicial no se cortó al agotarse el plazo: $SALIDA10"
echo "$SALIDA10" | grep -q "volcado no iniciado: agotado el plazo" || fallo "el volcado final no respetó el plazo: $SALIDA10"
[ "$(cat "$CONTADOR_STATS")" = "0" ] || fallo "con el plazo agotado no debía muestrear, hizo $(cat "$CONTADOR_STATS")"
[ "$ESTADO10" -eq 5 ] || fallo "un muestreo sin ninguna muestra debía devolver 5, devolvió $ESTADO10"
echo "$SALIDA10" | grep -q "Fin del muestreo" && fallo "un muestreo sin muestras no puede cerrar con «Fin del muestreo»"
echo "OK: los volcados de cgroup respetan el plazo total"

echo "=== Caso 11: el bucle deja el tramo final (duración/10, tope 15 s) para la lectura final ==="
docker() { docker_base "$@"; }
despliegue_en_curso() { return 1; }   # el caso 8 la dejó redefinida
sleep() { SECONDS=$((SECONDS + $1)); }   # ahora el reloj avanza de verdad con cada intervalo
echo 0 > "$CONTADOR_STATS"
SALIDA11="$(muestreo_memoria 100 5 2>&1)"
[ "$(cat "$CONTADOR_STATS")" = "18" ] || fallo "100 s a 5 s con 10 s de reserva debían dar 18 muestras (t=0..85), dieron $(cat "$CONTADOR_STATS")"
echo "$SALIDA11" | grep -q "Fin del muestreo: muestras=18 stats_ok=18" || fallo "falta el cierre con 18 muestras y 18 lecturas"
sleep() { :; }
echo "OK: 18 muestras; el tramo final queda para la lectura final"

echo "=== Caso 12: la última muestra y su espera no rebasan el fin del bucle ==="
LIMITES="$TMP/limites-stats"; ESPERAS="$TMP/esperas"; : > "$LIMITES"; : > "$ESPERAS"
timeout() { if [ "$2" = docker ] && [ "$3" = stats ]; then echo "$1" >> "$LIMITES"; fi; shift; "$@"; }
sleep() { echo "$1" >> "$ESPERAS"; SECONDS=$((SECONDS + $1)); }
echo 0 > "$CONTADOR_STATS"
muestreo_memoria 60 5 >/dev/null 2>&1   # reserva 6 s: el bucle acaba en t=54
[ "$(sort -n "$LIMITES" | head -1)" -le 4 ] || fallo "el docker stats de la última muestra debía tener techo <= 4 s (quedan 4 s de bucle), sus techos: $(tr '\n' ' ' < "$LIMITES")"
[ "$(tail -1 "$LIMITES")" -le 4 ] || fallo "el techo del último docker stats debía acotarse al tiempo que queda: $(tail -1 "$LIMITES")"
[ "$(tail -1 "$ESPERAS")" -le 4 ] || fallo "la última espera debía acotarse al tiempo que queda de bucle: $(tail -1 "$ESPERAS")"
[ "$(head -1 "$LIMITES")" = "15" ] || fallo "con tiempo de sobra el techo de docker stats es 15 s: $(head -1 "$LIMITES")"
timeout() { shift; "$@"; }
sleep() { :; }
echo "OK: docker stats y la espera respetan el fin del bucle"

echo "=== Caso 13: «muestras» cuenta vueltas; lo medido es stats_ok (docker stats que devolvió filas con mem=) ==="
despliegue_en_curso() { return 1; }
# a) docker stats siempre falla: N vueltas, 0 lecturas -> NO hay cierre válido (estado 5).
docker() { if [ "$1" = stats ]; then return 1; fi; docker_base "$@"; }
ESTADO13=0; SALIDA13="$(muestreo_memoria 20 2 2>&1)" || ESTADO13=$?
[ "$ESTADO13" -eq 5 ] || fallo "con docker stats siempre fallando debía devolver 5, devolvió $ESTADO13"
echo "$SALIDA13" | grep -q "Muestreo SIN lecturas de docker stats (muestras=11 stats_ok=0" || fallo "debía decir muestras=11 stats_ok=0: $(echo "$SALIDA13" | grep -a "Muestreo SIN")"
echo "$SALIDA13" | grep -q "Fin del muestreo" && fallo "0 lecturas útiles no pueden cerrar con «Fin del muestreo»"
# b) docker stats sale con 0 pero SIN filas (contenedores parados, lectura vacía): tampoco cuenta.
docker() { if [ "$1" = stats ]; then return 0; fi; docker_base "$@"; }
ESTADO13=0; SALIDA13="$(muestreo_memoria 20 2 2>&1)" || ESTADO13=$?
[ "$ESTADO13" -eq 5 ] || fallo "una lectura vacía no es una lectura: debía devolver 5, devolvió $ESTADO13"
# c) solo responde una de cada dos: stats_ok cuenta las buenas, no las vueltas.
echo 0 > "$TMP/n13"
docker() {
    if [ "$1" = stats ]; then
        local n; n=$(( $(cat "$TMP/n13") + 1 )); echo "$n" > "$TMP/n13"
        [ $(( n % 2 )) -eq 1 ] && echo "caemanager-app mem=200MiB / 3GiB 6% cpu=1%"
        return 0
    fi
    docker_base "$@"
}
SALIDA13="$(muestreo_memoria 20 2 2>&1)"
echo "$SALIDA13" | grep -q "Fin del muestreo: muestras=11 stats_ok=6 " || fallo "11 vueltas con 6 buenas debían cerrar con muestras=11 stats_ok=6: $(echo "$SALIDA13" | grep -a "Fin del muestreo")"
docker() { docker_base "$@"; }
echo "OK: el cierre distingue vueltas de lecturas y no da por bueno lo vacío"

echo "=== Caso 14: los volcados lentos se reflejan en el cierre (final incompleto, y el inicial no se come la línea base) ==="
docker() {
    case "$1" in
        ps) printf '%s\n' caemanager-a caemanager-b caemanager-c caemanager-d caemanager-e caemanager-f caemanager-g ;;
        exec) SECONDS=$((SECONDS + 2)); docker_base "$@" ;;   # cada lectura de cgroup "tarda" 2 s
        *) docker_base "$@" ;;
    esac
}
sleep() { SECONDS=$((SECONDS + $1)); }
SALIDA14="$(muestreo_memoria 100 5 2>&1)"
CIERRE14="$(echo "$SALIDA14" | grep -a "Fin del muestreo")"
[ -n "$CIERRE14" ] || fallo "debía cerrar (hay lecturas de docker stats): $(echo "$SALIDA14" | tail -3)"
echo "$CIERRE14" | grep -q "volcado_final=[0-6]/7" || fallo "con 7 contenedores y lecturas lentas el volcado final debía quedar incompleto (k/7 con k<7): $CIERRE14"
echo "$CIERRE14" | grep -q "volcado_inicial=[0-6]/7" || fallo "el volcado inicial lento debía quedar incompleto y acotado a su presupuesto: $CIERRE14"
T_PRIMERA14="$(echo "$CIERRE14" | sed -n 's/.* t_primera=\([0-9]*\)s.*/\1/p')"
[ -n "$T_PRIMERA14" ] && [ "$T_PRIMERA14" -le 12 ] || fallo "la primera muestra debía caer dentro del presupuesto del volcado inicial (<= 12 s), cayó en t=${T_PRIMERA14}s: $CIERRE14"
sleep() { :; }
docker() { docker_base "$@"; }
echo "OK: volcado_final incompleto visible; primera muestra en t=${T_PRIMERA14}s"

echo "=== Caso 15: la cadencia descuenta lo que tarda la lectura de docker stats ==="
CLK="$TMP/reloj"; echo 0 > "$CLK"
reloj() { cat "$CLK"; }
sleep() { echo $(( $(cat "$CLK") + $1 )) > "$CLK"; }
docker() {
    if [ "$1" = stats ]; then echo $(( $(cat "$CLK") + 2 )) > "$CLK"; echo "caemanager-app mem=200MiB / 3GiB 6% cpu=1%"; return 0; fi   # docker stats tarda 2 s
    docker_base "$@"
}
SALIDA15="$(muestreo_memoria 100 5 2>&1)"
echo "$SALIDA15" | grep -q "Fin del muestreo: muestras=18 stats_ok=18 " || fallo "100 s a 5 s con docker stats de 2 s debían dar 18 vueltas (t=0..85: 5 s cada una, no 7): $(echo "$SALIDA15" | grep -a "Fin del muestreo")"
unset -f reloj; reloj() { echo "$SECONDS"; }
sleep() { :; }
docker() { docker_base "$@"; }
echo "OK: 18 vueltas; el intervalo es de reloj, no de espera"

echo "=== Caso 16: si docker ps no responde, el total es «?» y no «0» (0/? no es un volcado completo) ==="
docker() { if [ "$1" = ps ]; then return 1; fi; docker_base "$@"; }
SALIDA16="$(muestreo_memoria 20 2 2>&1)"
echo "$SALIDA16" | grep -q "volcado_inicial=0/? volcado_final=0/?" || fallo "sin docker ps debía verse 0/? en ambos volcados: $(echo "$SALIDA16" | grep -a "Fin del muestreo")"
docker() { docker_base "$@"; }
echo "OK"

echo "=== Caso 17: en la ventana larga la reserva del volcado final (tope 30 s) alcanza para 7 contenedores lentos ==="
docker() {
    case "$1" in
        ps) printf '%s\n' caemanager-a caemanager-b caemanager-c caemanager-d caemanager-e caemanager-f caemanager-g ;;
        exec) SECONDS=$((SECONDS + 3)); docker_base "$@" ;;   # 7 x 3 s = 21 s de volcado final
        *) docker_base "$@" ;;
    esac
}
sleep() { SECONDS=$((SECONDS + $1)); }
SALIDA17="$(muestreo_memoria 420 60 2>&1)"
echo "$SALIDA17" | grep -q "Fin del muestreo: .*volcado_final=7/7 " || fallo "con 21 s de volcado final y 30 s de reserva debían leerse los 7: $(echo "$SALIDA17" | grep -a "Fin del muestreo")"
sleep() { :; }
docker() { docker_base "$@"; }
echo "OK: 7/7 en el volcado final"

echo "=== Caso 18: un docker exec que falla no cuenta como contenedor leído ==="
docker() { if [ "$1" = exec ]; then return 1; fi; docker_base "$@"; }
SALIDA18="$(muestreo_memoria 20 2 2>&1)"
echo "$SALIDA18" | grep -q "volcado_inicial=0/2 volcado_final=0/2" || fallo "con docker exec fallando debía verse 0/2 en ambos volcados: $(echo "$SALIDA18" | grep -a "Fin del muestreo")"
echo "$SALIDA18" | grep -q "sin lectura de cgroup en caemanager-app" || fallo "debía avisar de la lectura perdida"
docker() { docker_base "$@"; }
echo "OK"

echo "TODAS LAS PRUEBAS PASARON"
