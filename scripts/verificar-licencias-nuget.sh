#!/bin/bash
# Verifica la licencia de cada dependencia NuGet de primer nivel.
#
# Por qué existe: en agosto de 2026 tres herramientas cambiaron sus condiciones
# económicas sin que nosotros tocáramos una línea, y dos de ellas llegaron
# disfrazadas de actualización rutinaria de dependabot:
#
#   FluentAssertions  7.x Apache-2.0  ->  8.x licencia comercial (Xceed)
#   MediatR           12.x Apache-2.0 ->  13.x RPL-1.5 o suscripción de pago
#
# En ambos casos el paquete pasó de declarar la licencia como EXPRESIÓN SPDX
# (`<license type="expression">Apache-2.0</license>`) a declararla como FICHERO
# (`<license type="file">LICENSE.md</license>`) — porque una licencia comercial
# propia no tiene identificador SPDX que ponerle. Ese cambio de `type` es la
# señal más fiable que hemos encontrado, y es la que este guion vigila.
#
# `requireLicenseAcceptance` NO se usa como criterio de fallo, aunque MediatR
# 13.0.0 lo activara. Al calibrar contra las 50 dependencias reales resultó que
# lo ponen a true 13 paquetes de Microsoft y 5 de OpenTelemetry, todos con MIT o
# Apache-2.0: como señal de alarma marcaba 17 falsos positivos de 22. Se informa
# entre corchetes, no bloquea.
#
# Qué NO hace: no interpreta el contenido de una licencia ni decide si es
# aceptable. Marca lo que hay que mirar. La decisión es de una persona.
#
# Uso:
#   scripts/verificar-licencias-nuget.sh <fichero>
#
# donde <fichero> tiene una línea por paquete, "id version":
#   MediatR 12.5.0
#   Serilog 4.4.0
#
# Variable de entorno NUSPEC_DIR: si está definida, los .nuspec se leen de ese
# directorio (`<id-en-minúsculas>.<version>.nuspec`) en vez de descargarse.
# Es lo que permite probar este guion sin red — ver verificar-licencias-nuget.tests.sh.
set -euo pipefail

# Nunca `productor | grep -q` ni `| head`: con pipefail, el consumidor que corta
# al primer acierto deja al productor escribiendo en una tubería cerrada (SIGPIPE
# o «write error: Broken pipe») y la tubería entera da fallo aunque el patrón
# estuviera. Así se perdió una vez la marca «[exige aceptar términos]» en CI. La
# entrada se materializa antes en una variable, comprobando el estado de quien la
# produce, y se busca con `aparece`. Tampoco `grep <<<`: Bash puede volcar el
# here-string a un fichero temporal y, si no lo crea, grep no corre y su 1 se lee
# como «no aparece».

# 0 = el patrón aparece en el texto, 1 = no aparece; cualquier otro estado de
# grep aborta la verificación (no decide nada). Sin -q, grep lee la entrada
# entera y el printf nunca escribe en una tubería cerrada.
aparece() {
  local texto="$1" estado=0
  shift
  printf '%s\n' "$texto" | grep "$@" >/dev/null || estado=$?
  if (( estado > 1 )); then
    echo "ERROR: la búsqueda 'grep $*' falló con estado $estado; no se decide nada" >&2
    exit 2
  fi
  return "$estado"
}

# Licencias libres aceptadas sin revisión. Deliberadamente corta: ampliarla es
# una decisión consciente, no un parche para silenciar un rojo.
# `PostgreSQL` (la de Npgsql) es la licencia del propio PostgreSQL: permisiva
# tipo BSD, aprobada por la OSI. Añadida tras verificarla, no por conveniencia.
PERMITIDAS=(
  MIT MIT-0 Apache-2.0 BSD-2-Clause BSD-3-Clause
  ISC 0BSD Unlicense MS-PL PostgreSQL
)

LISTA="${1:-}"
if [[ -z "$LISTA" || ! -f "$LISTA" ]]; then
  echo "Uso: $0 <fichero con líneas 'id version'>" >&2
  exit 2
fi

# Excepciones revisadas por una persona: paquetes que declaran la licencia como
# fichero y cuya licencia real se ha leído y aceptado. Se anclan a la VERSIÓN
# exacta a propósito — que una actualización obligue a revisar otra vez es
# justamente el objetivo de este guion, no un efecto colateral molesto.
EXCEPCIONES="${EXCEPCIONES:-$(dirname "${BASH_SOURCE[0]}")/licencias-revisadas.txt}"

esta_exceptuado() {
  local id="$1" version="$2"
  [[ -f "$EXCEPCIONES" ]] || return 1
  local contenido
  # Se llama dentro de un `if`, donde `set -e` no actúa: sin esta comprobación,
  # un `tr` que fallara tras escribir media lista dejaría buscar en la salida
  # parcial y aceptar la excepción. Si no se puede leer la lista entera, la
  # verificación no decide nada y falla.
  if ! contenido="$(tr -d '\r' <"$EXCEPCIONES")"; then
    echo "ERROR: no se pudo leer entera la lista de excepciones $EXCEPCIONES; no se decide si $id $version está revisado" >&2
    exit 2
  fi
  aparece "$contenido" -E -e "^[[:space:]]*${id}[[:space:]]+${version}([[:space:]]|$)"
}

obtener_nuspec() {
  local id_min="$1" version="$2"
  if [[ -n "${NUSPEC_DIR:-}" ]]; then
    local ruta="$NUSPEC_DIR/${id_min}.${version}.nuspec"
    [[ -f "$ruta" ]] || return 1
    cat "$ruta"
  else
    curl -fsSL --max-time 30 \
      "https://api.nuget.org/v3-flatcontainer/${id_min}/${version}/${id_min}.nuspec"
  fi
}

# Extrae el valor de <license type="X">Y</license> como "X|Y".
# Tolera atributos en cualquier orden y espacios; si no hay <license>, "|".
leer_licencia() {
  local nuspec="${1//$'\r'/}"
  local linea
  # Sin coincidencia (o si grep falla) queda vacío y sale "|": PROBLEMA, nunca OK.
  linea="$(printf '%s\n' "$nuspec" | grep -o '<license[^>]*>[^<]*</license>' || true)"
  linea="${linea%%$'\n'*}"
  [[ -n "$linea" ]] || { printf '|'; return; }
  local tipo valor
  tipo="$(printf '%s' "$linea" | sed -n 's/.*type="\([^"]*\)".*/\1/p')"
  valor="$(printf '%s' "$linea" | sed -n 's/.*<license[^>]*>\([^<]*\)<\/license>.*/\1/p')"
  printf '%s|%s' "$tipo" "$valor"
}

exige_aceptacion() {
  aparece "${1//$'\r'/}" -i -e '<requireLicenseAcceptance>[[:space:]]*true[[:space:]]*</requireLicenseAcceptance>'
}

es_permitida() {
  local expr="$1" p
  for p in "${PERMITIDAS[@]}"; do
    [[ "$expr" == "$p" ]] && return 0
  done
  return 1
}

PROBLEMAS=0
REVISADOS=0

# La lista se lee entera ANTES del bucle y comprobando el estado de `tr`: con
# `done < <(tr …)` el estado se perdía, y un `tr` que fallara a medias dejaba
# revisar solo parte de los paquetes y salir con 0. El bucle lee después de un
# `printf` del propio Bash sobre la variable ya validada, sin fichero temporal.
# La lista puede venir de una herramienta que escriba finales CRLF (visto con
# `dotnet list package` procesado en Windows): un '\r' pegado a la versión
# produce una URL que no existe, y el guion habría marcado los 50 paquetes
# como problema por un motivo que no era el suyo.
if ! PAQUETES="$(tr -d '\r' <"$LISTA")"; then
  echo "ERROR: no se pudo leer entera la lista de paquetes $LISTA; no se decide nada" >&2
  exit 2
fi

while read -r id version _resto; do
  # Salta líneas vacías y comentarios.
  [[ -z "${id:-}" || "$id" == \#* ]] && continue
  [[ -n "${version:-}" ]] || { echo "AVISO: '$id' sin versión, se omite" >&2; continue; }

  REVISADOS=$((REVISADOS + 1))
  id_min="$(printf '%s' "$id" | tr '[:upper:]' '[:lower:]')"

  if ! nuspec="$(obtener_nuspec "$id_min" "$version" 2>/dev/null)"; then
    echo "PROBLEMA  $id $version — no se pudo obtener el .nuspec"
    PROBLEMAS=$((PROBLEMAS + 1))
    continue
  fi

  licencia="$(leer_licencia "$nuspec")"
  tipo="${licencia%%|*}"
  valor="${licencia#*|}"
  aceptacion=""
  exige_aceptacion "$nuspec" && aceptacion=" [exige aceptar términos]"

  case "$tipo" in
    expression)
      if es_permitida "$valor"; then
        echo "OK        $id $version — $valor$aceptacion"
      else
        echo "PROBLEMA  $id $version — licencia no permitida: '$valor'$aceptacion"
        PROBLEMAS=$((PROBLEMAS + 1))
      fi
      ;;
    file)
      # El caso de FluentAssertions 8.x y MediatR 13.x: licencia propia, sin
      # identificador SPDX. No siempre es de pago, pero siempre hay que leerla.
      if esta_exceptuado "$id" "$version"; then
        echo "REVISADO  $id $version — licencia en fichero ('$valor'), aceptada por una persona"
      else
        echo "PROBLEMA  $id $version — licencia como fichero ('$valor'), requiere lectura humana$aceptacion"
        PROBLEMAS=$((PROBLEMAS + 1))
      fi
      ;;
    "")
      echo "PROBLEMA  $id $version — el paquete no declara <license>$aceptacion"
      PROBLEMAS=$((PROBLEMAS + 1))
      ;;
    *)
      echo "PROBLEMA  $id $version — tipo de licencia desconocido: '$tipo'$aceptacion"
      PROBLEMAS=$((PROBLEMAS + 1))
      ;;
  esac
done < <(printf '%s\n' "$PAQUETES")

echo
echo "Revisados: $REVISADOS · Problemas: $PROBLEMAS"

if (( PROBLEMAS > 0 )); then
  echo
  echo "Un PROBLEMA no significa 'licencia prohibida': significa 'nadie ha mirado esto'." >&2
  echo "Si la licencia es aceptable, añádela a PERMITIDAS en este guion — como" >&2
  echo "decisión consciente y con el porqué en el commit, no para tapar un rojo." >&2
  exit 1
fi
