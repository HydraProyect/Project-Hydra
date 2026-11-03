#!/bin/bash
# Techo de memoria parametrizado de db, caddy y seq (REC-198/REC-199, valor
# pendiente de REC-196) en docker-compose.produccion.yml y .staging.yml.
#
# Propiedades que fija, todas sobre la configuración EFECTIVA que resuelve
# `docker compose config` (no arranca ningún contenedor ni necesita daemon):
#   1. Sin variables, los cinco servicios quedan SIN techo y con el MISMO hash
#      de configuración que el mismo fichero sin la línea `mem_limit`: fusionar
#      y desplegar este cambio no recrea la base, ni Caddy, ni Seq.
#   2. Con la variable en el fichero de entorno, el techo llega al servicio que
#      nombra y a ningún otro, y el hash cambia (el `up` siguiente lo recrea).
#   3. El techo de la app (LIMITE_MEMORIA_APP) no se ve afectado.
#   4. Un valor que Docker no entiende hace fallar `config`: el despliegue se
#      detiene antes de tocar nada en vez de arrancar sin techo en silencio.
#   5. La guarda de correo de deploy/relevo-app.sh ve EnvioCorreo__Modo en la
#      salida real de `config`, ve su ausencia y distingue un valor del exigido.
#
# No mide que el kernel aplique el techo ni que el valor sea el adecuado: eso
# se comprueba en el servidor (`memory.max` del contenedor) tras fijarlo.
set -euo pipefail

DIR_GUION="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DIR="$(mktemp -d)"
trap 'rm -rf "$DIR"' EXIT

# <compose> <fichero de entorno> <servicios con techo parametrizado>
CASOS=(
  "docker-compose.produccion.yml .env db:DB caddy:CADDY seq:SEQ"
  "docker-compose.staging.yml .env.staging db:DB seq:SEQ"
)

bloque_de_servicio_en() {  # <fichero> <servicio>
  awk -v s="$2" '$0 == "  " s ":" { f = 1; next } f && /^  [A-Za-z0-9_-]+:/ { f = 0 } f' "$1"
}

echo "=== Caso 1 (texto): cada servicio lleva su variable, con 0 por defecto ==="
for caso in "${CASOS[@]}"; do
  set -- $caso
  compose="$1"; shift 2
  # Copia sin CR: en un checkout de Windows el fichero llega con CRLF.
  tr -d '\r' < "$DIR_GUION/local/$compose" > "$DIR/$compose"
  for par in "$@"; do
    servicio="${par%%:*}"; sufijo="${par##*:}"
    esperada="    mem_limit: \${LIMITE_MEMORIA_${sufijo}:-0}"
    # El bloque se guarda antes de buscar en él, sin tubería hacia `grep -q`:
    # grep -q sale en la primera coincidencia y, con `pipefail`, si awk aún
    # tenía que escribir (el bloque de caddy pasa de 4 KiB, así que awk lo
    # vuelca en dos escrituras) muere por SIGPIPE y la tubería da fallo aunque
    # la línea esté. Era una carrera: fallaba a veces en la cola de fusión.
    bloque="$(bloque_de_servicio_en "$DIR/$compose" "$servicio")" \
      || { echo "FALLO: $compose: no se pudo extraer el bloque del servicio $servicio" >&2; exit 1; }
    grep -qxF "$esperada" <<< "$bloque" \
      || { echo "FALLO: $compose: el servicio $servicio no lleva la línea '${esperada#    }'" >&2; exit 1; }
  done
done
echo "OK (texto): db, caddy y seq llevan mem_limit parametrizado y sin techo por defecto"

if ! docker compose version >/dev/null 2>&1; then
  if [ "${CI:-}" = "true" ]; then
    echo "FALLO: en CI estos casos exigen docker compose para medir la configuración efectiva" >&2
    exit 1
  fi
  echo "OMITIDO (efectiva): sin docker compose en esta máquina — solo se midió el texto; en CI es obligatorio"
  echo "TODAS LAS PRUEBAS PASARON"
  exit 0
fi

# Valores sintéticos: ninguna credencial real pasa por aquí.
escribir_entorno() {  # <fichero> [línea extra...]
  local fichero="$1"; shift
  printf '%s\n' 'POSTGRES_PASSWORD=SINTETICA' 'DOMINIO=ejemplo.invalid' 'ACME_EMAIL=ops@ejemplo.invalid' "$@" > "$fichero"
}
efectiva() {  # <compose> <fichero de entorno>
  ( cd "$DIR" && docker compose -f "$1" --env-file "$2" --profile ranura config )
}
hash_de() {  # <compose> <fichero de entorno> <servicio>
  ( cd "$DIR" && docker compose -f "$1" --env-file "$2" --profile ranura config --hash "$3" ) | awk '{ print $2 }'
}
techo_de() {  # <fichero con la efectiva> <servicio>
  bloque_de_servicio_en "$1" "$2" | sed -n 's/^    mem_limit: "\{0,1\}\([0-9]*\)"\{0,1\}$/\1/p'
}

BYTES_TECHO=268435456   # 256m: valor de prueba, no una recomendación.
BYTES_APP=3221225472    # el 3g por defecto de LIMITE_MEMORIA_APP

for caso in "${CASOS[@]}"; do
  set -- $caso
  compose="$1"; entorno="$2"; shift 2

  echo "=== Caso 2 ($compose): sin variables no hay techo ni se recrea nada ==="
  escribir_entorno "$DIR/$entorno"
  efectiva "$compose" "$entorno" > "$DIR/efectiva-sin" \
    || { echo "FALLO: docker compose config no resolvió $compose" >&2; exit 1; }
  # El mismo fichero sin las líneas nuevas: lo que había antes de este cambio.
  grep -v '^    mem_limit: \${LIMITE_MEMORIA_\(DB\|CADDY\|SEQ\):-0}$' "$DIR/$compose" > "$DIR/anterior-$compose"
  [ "$(wc -l < "$DIR/anterior-$compose")" -eq $(( $(wc -l < "$DIR/$compose") - $# )) ] \
    || { echo "FALLO: no se quitaron exactamente $# líneas de $compose — la comparación no mide lo que dice" >&2; exit 1; }
  for par in "$@"; do
    servicio="${par%%:*}"
    techo="$(techo_de "$DIR/efectiva-sin" "$servicio")"
    [ -z "$techo" ] || [ "$techo" = "0" ] \
      || { echo "FALLO: $compose: $servicio tiene techo ($techo) sin que nadie lo haya pedido" >&2; exit 1; }
    con="$(hash_de "$compose" "$entorno" "$servicio")"
    sin="$(hash_de "anterior-$compose" "$entorno" "$servicio")"
    [ -n "$con" ] || { echo "FALLO: $compose: no se obtuvo el hash de $servicio" >&2; exit 1; }
    [ "$con" = "$sin" ] \
      || { echo "FALLO: $compose: el hash de $servicio cambia sin variables — desplegar esto recrearía el contenedor" >&2; exit 1; }
  done
  for ranura in app-azul app-verde; do
    [ "$(techo_de "$DIR/efectiva-sin" "$ranura")" = "$BYTES_APP" ] \
      || { echo "FALLO: $compose: $ranura ya no lleva su techo por defecto de 3g" >&2; exit 1; }
  done
  echo "OK: sin variables, misma configuración efectiva que antes"

  echo "=== Caso 3 ($compose): cada variable pone techo a su servicio y solo a él ==="
  for par in "$@"; do
    servicio="${par%%:*}"; sufijo="${par##*:}"
    escribir_entorno "$DIR/$entorno" "LIMITE_MEMORIA_${sufijo}=256m"
    efectiva "$compose" "$entorno" > "$DIR/efectiva-con"
    [ "$(techo_de "$DIR/efectiva-con" "$servicio")" = "$BYTES_TECHO" ] \
      || { echo "FALLO: $compose: LIMITE_MEMORIA_${sufijo}=256m no llega a $servicio" >&2; exit 1; }
    for otro in "$@"; do
      otro="${otro%%:*}"
      [ "$otro" = "$servicio" ] && continue
      techo="$(techo_de "$DIR/efectiva-con" "$otro")"
      [ -z "$techo" ] || [ "$techo" = "0" ] \
        || { echo "FALLO: $compose: LIMITE_MEMORIA_${sufijo} también pone techo a $otro" >&2; exit 1; }
    done
    for ranura in app-azul app-verde; do
      [ "$(techo_de "$DIR/efectiva-con" "$ranura")" = "$BYTES_APP" ] \
        || { echo "FALLO: $compose: LIMITE_MEMORIA_${sufijo} cambia el techo de $ranura" >&2; exit 1; }
    done
    [ "$(hash_de "$compose" "$entorno" "$servicio")" != "$(hash_de "anterior-$compose" "$entorno" "$servicio")" ] \
      || { echo "FALLO: $compose: con techo, el hash de $servicio no cambia — el up no lo aplicaría" >&2; exit 1; }
  done
  echo "OK: el techo llega al servicio nombrado, a ningún otro, y cambia su hash"

  echo "=== Caso 4 ($compose): un valor que Docker no entiende detiene config ==="
  escribir_entorno "$DIR/$entorno" "LIMITE_MEMORIA_DB=mucho"
  if efectiva "$compose" "$entorno" > /dev/null 2>&1; then
    echo "FALLO: $compose: LIMITE_MEMORIA_DB=mucho se acepta — el despliegue seguiría sin techo y sin avisar" >&2
    exit 1
  fi
  echo "OK: config falla con un valor ilegible"

  echo "=== Caso 5 ($compose): la guarda de correo del relevo lee la configuración efectiva real ==="
  # El relevo-app.tests.sh usa un doble de `docker`: aquí se fija que su función
  # entiende lo que Compose imprime de verdad, con el .env entrando por env_file.
  clave_en() {  # <servicio>
    ( source "$DIR_GUION/relevo-app.sh"; efectiva "$compose" "$entorno" | clave_con_valor_en_servicio "$1" EnvioCorreo__Modo )
  }
  medir_clave() {  # <salida esperada> <descripción>
    local ranura obtenido
    for ranura in app-azul app-verde; do
      obtenido=0; clave_en "$ranura" || obtenido=$?
      [ "$obtenido" = "$1" ] \
        || { echo "FALLO: $compose: $ranura con $2 da $obtenido, se esperaba $1" >&2; exit 1; }
    done
  }
  escribir_entorno "$DIR/$entorno"
  medir_clave 1 "la clave ausente"
  escribir_entorno "$DIR/$entorno" "EnvioCorreo__Modo="
  medir_clave 1 "la clave vacía"
  escribir_entorno "$DIR/$entorno" "#EnvioCorreo__Modo=Abierto"
  medir_clave 1 "la clave comentada"
  escribir_entorno "$DIR/$entorno" "EnvioCorreo__Modo=Cerrado"
  medir_clave 0 "la clave en el fichero de entorno"
  obtenido=0; clave_en servicio-que-no-existe || obtenido=$?
  [ "$obtenido" = 2 ] || { echo "FALLO: $compose: un servicio que no existe da $obtenido, se esperaba 2" >&2; exit 1; }
  echo "OK: la clave del fichero de entorno llega a las dos ranuras y su ausencia se ve"

  # Con modo exigido, el mismo uso que hace el relevo en staging.
  medir_modo() {  # <salida esperada> <descripción>
    local ranura obtenido
    for ranura in app-azul app-verde; do
      obtenido=0
      ( source "$DIR_GUION/relevo-app.sh"; efectiva "$compose" "$entorno" | clave_con_valor_en_servicio "$ranura" EnvioCorreo__Modo Cerrado ) || obtenido=$?
      [ "$obtenido" = "$1" ] \
        || { echo "FALLO: $compose: $ranura con $2 da $obtenido, se esperaba $1" >&2; exit 1; }
    done
  }
  escribir_entorno "$DIR/$entorno" "EnvioCorreo__Modo=Cerrado"
  medir_modo 0 "el modo exigido"
  escribir_entorno "$DIR/$entorno" "EnvioCorreo__Modo=Abierto"
  medir_modo 3 "Abierto"
  escribir_entorno "$DIR/$entorno" "EnvioCorreo__Modo=SoloPermitidos"
  medir_modo 3 "SoloPermitidos"
  escribir_entorno "$DIR/$entorno" "EnvioCorreo__Modo=Cerrado" "ENVIOCORREO__MODO=Abierto"
  medir_modo 3 "otra grafía de la clave con otro valor"
  escribir_entorno "$DIR/$entorno"
  medir_modo 1 "la clave ausente"
  echo "OK: el modo exigido se distingue en la salida real de config"
done

echo "TODAS LAS PRUEBAS PASARON"
