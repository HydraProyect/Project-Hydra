#!/usr/bin/env bash
# Reparte las clases de un proyecto de test en N bloques y escribe el filtro
# de `dotnet test` del bloque pedido.
#
# El reparto se calcula a partir de la lista real de tests, no de una lista
# escrita a mano: una clase nueva cae en su bloque sola. Como los N bloques
# ordenan la misma lista y usan el mismo criterio de posición, la partición
# cubre todas las clases y no solapa ninguna.
#
# Uso:   repartir-clases-de-test.sh <proyecto> <total-bloques> <bloque>
# Sale:  el filtro por stdout; 1 si el bloque quedaría vacío.
#
# Por qué falla cuando el bloque queda vacío: `dotnet test --filter` con un
# patrón que no casa con nada TERMINA EN VERDE sin ejecutar un solo test. Un
# bloque mudo se leería como un bloque que pasa.

set -euo pipefail

proyecto=${1:?falta el proyecto de test}
total=${2:?falta el numero total de bloques}
bloque=${3:?falta el numero de bloque}

if ! [[ "$total" =~ ^[0-9]+$ ]] || [ "$total" -lt 1 ]; then
  echo "El total de bloques debe ser un entero >= 1 (recibido: '$total')." >&2
  exit 2
fi
if ! [[ "$bloque" =~ ^[0-9]+$ ]] || [ "$bloque" -lt 1 ] || [ "$bloque" -gt "$total" ]; then
  echo "El bloque debe estar entre 1 y $total (recibido: '$bloque')." >&2
  exit 2
fi

listado=${LISTADO_DE_TESTS:-}
if [ -z "$listado" ]; then
  listado=$(mktemp)
  trap 'rm -f "$listado"' EXIT
  dotnet test "$proyecto" --no-build --list-tests > "$listado"
fi

# De "  Espacio.De.Nombres.MiClaseTests.MiMetodo(caso: 1)" queda
# "Espacio.De.Nombres.MiClaseTests": se recorta el ultimo segmento, que es el
# metodo, y lo que venga detras si es un Theory con parametros.
clases=$(sed -n 's/^[[:space:]]*\([A-Za-z0-9_][A-Za-z0-9_.]*\)\.[A-Za-z0-9_]\{1,\}.*$/\1/p' \
           "$listado" | sort -u)

if [ -z "$clases" ]; then
  echo "No se reconocio ninguna clase de test en el listado." >&2
  exit 1
fi

mias=$(printf '%s\n' "$clases" | awk -v n="$total" -v k="$bloque" 'NR % n == (k % n)')

if [ -z "$mias" ]; then
  echo "El reparto dejo el bloque $bloque de $total sin ninguna clase." >&2
  echo "Un filtro que no casa con nada haria pasar el bloque en verde sin" >&2
  echo "ejecutar un solo test, asi que se falla aqui a proposito." >&2
  exit 1
fi

echo "Clases en total: $(printf '%s\n' "$clases" | wc -l | tr -d ' ')" >&2
echo "En el bloque $bloque de $total: $(printf '%s\n' "$mias" | wc -l | tr -d ' ')" >&2

# REPARTO_SALIDA: ademas del filtro, deja la lista de clases de este bloque,
# una por linea. Sirve para comprobar despues que la union de los bloques es
# la suite entera — que cada bloque ejecute algo no implica que entre todos
# ejecuten TODO, y una clase que se cayera del reparto no fallaria: no
# correria en ninguna parte, con todos los bloques en verde.
if [ -n "${REPARTO_SALIDA:-}" ]; then
  printf '%s\n' "$mias" > "$REPARTO_SALIDA"
fi

# El punto final importa: con `~` (contiene) y sin el, una clase cuyo nombre
# es prefijo de otra (MisTests / MisTestsExtra) arrastraria tambien a la
# segunda, que se ejecutaria dos veces mientras su bloque la cuenta como suya.
printf '%s\n' "$mias" | sed 's/$/./; s/^/FullyQualifiedName~/' | paste -sd'|' -
