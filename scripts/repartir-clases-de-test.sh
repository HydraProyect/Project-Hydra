#!/usr/bin/env bash
# Reparte las clases de un proyecto de test en N bloques y escribe el filtro
# de `dotnet test` del bloque pedido.
#
# El reparto se calcula a partir de la lista real de tests, no de una lista
# escrita a mano: una clase nueva cae en su bloque sola. Como los N bloques
# leen la misma lista y aplican el mismo criterio (hash del nombre, salvo las
# clases fijadas), la partición cubre todas las clases y no solapa ninguna.
#
# Uso:   repartir-clases-de-test.sh <proyecto> <total-bloques> <bloque>
# Sale:  el filtro por stdout; 1 si el bloque quedaría vacío.
#
# REPARTO_FIJADAS (opcional): fichero con líneas "<Clase.Completa> <bloque>".
# Las clases que nombra van al bloque que dice en vez de al de su hash. Sirve
# para separar las pocas clases muy pesadas que el hash juntó por azar; el
# resto sigue por hash. Sin la variable, todo va por hash.
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

# SUELO ABSOLUTO sobre el universo descubierto.
#
# Sin esto, un descubrimiento averiado se cuela en VERDE: si `--list-tests`
# devolviera 3 clases en vez de 145, el reparto daria 3, la comprobacion de
# cobertura del reparto compararia 3 contra 3, coincidirian, y 142 clases no se
# ejecutarian en ninguna parte sin que nada fallara. Que los dos lados de esa
# igualdad salgan del mismo codigo protege de la deriva entre ellos y expone al
# colapso conjunto — el primer modo de fallo de la auditoria de ratchets.
#
# La igualdad no distingue "coinciden porque esta bien" de "coinciden porque
# los dos estan vacios". El suelo si.
#
# Es un ratchet: se sube cuando el numero real crezca de forma sostenida,
# nunca se baja para que pase un PR concreto.
numero=$(printf '%s\n' "$clases" | wc -l | tr -d ' ')
if [ -n "${MINIMO_CLASES:-}" ] && [ "$numero" -lt "$MINIMO_CLASES" ]; then
  echo "Se descubrieron $numero clases de test, por debajo del suelo de $MINIMO_CLASES." >&2
  echo "Esto no es un reparto malo: es un descubrimiento averiado. Repartir" >&2
  echo "sobre una lista truncada dejaria clases sin ejecutar en ningun bloque," >&2
  echo "y la comprobacion de cobertura del reparto no lo veria, porque sus dos" >&2
  echo "lados saldrian de esta misma lista." >&2
  exit 1
fi

# El reparto es por HASH ESTABLE del nombre de la clase (CRC32 vía `cksum`),
# no por posicion en la lista ordenada.
#
# Con `NR % n` (version anterior), insertar una clase nueva desplaza la
# posicion de TODA clase que la siga en el orden alfabetico, y con eso su
# bloque. Medido el 2026-09-06 (PR #487): una sola clase nueva cuyo nombre
# empieza cerca del principio del alfabeto movio las 199 clases existentes de
# bloque en el mismo commit — el bloque 4 broto con clases que nunca habian
# corrido juntas y goteo un fallo intermitente (NullReferenceException en
# NpgsqlMigrator.MigrateAsync, corregido en un reintento sobre el mismo commit,
# sin tocar una linea) que no tenia nada que ver con la clase nueva en si.
#
# El hash de una clase depende solo de su propio nombre: anadir, quitar o
# renombrar OTRA clase nunca lo cambia, asi que una clase nueva cae en su
# bloque sola y el resto del reparto queda exactamente como estaba.
#
# CLASES FIJADAS. El hash reparte bien el número de clases, pero no su peso:
# medido el 2026-10-10 (run 38038470777), las tres clases más pesadas de la
# suite cayeron juntas en el bloque 1 y lo dejaron como camino crítico del
# grupo de fusión. Una clase nombrada en REPARTO_FIJADAS va al bloque que el
# fichero dice. Conserva la propiedad de arriba: fijar una clase no mueve a
# ninguna otra, y una clase nueva sigue cayendo sola por su hash.
#
# Lo que se rechaza, porque repartiría mal sin avisar:
#   - un bloque que no es un entero entre 1 y el total (fichero escrito para
#     otro número de bloques: con menos bloques la clase no correría en
#     ninguno);
#   - la misma clase fijada dos veces.
# Una clase fijada que ya no existe en el listado solo avisa: es una línea
# caducada tras un renombrado, y la clase nueva corre por su hash.
declare -A fijadas=()
if [ -n "${REPARTO_FIJADAS:-}" ]; then
  if [ ! -f "$REPARTO_FIJADAS" ]; then
    echo "No existe el fichero de clases fijadas: $REPARTO_FIJADAS" >&2
    exit 2
  fi
  while read -r nombre destino resto || [ -n "${nombre:-}" ]; do
    nombre=${nombre%$'\r'}; destino=${destino%$'\r'}; resto=${resto%$'\r'}
    case "$nombre" in ''|'#'*) continue ;; esac
    if [ -n "$resto" ] || ! [[ "$destino" =~ ^[0-9]+$ ]] || [ "$destino" -lt 1 ] || [ "$destino" -gt "$total" ]; then
      echo "Línea inválida en $REPARTO_FIJADAS: '$nombre $destino $resto'." >&2
      echo "Se espera '<Clase.Completa> <bloque>' con el bloque entre 1 y $total." >&2
      exit 2
    fi
    if [ -n "${fijadas[$nombre]:-}" ]; then
      echo "La clase $nombre está fijada dos veces en $REPARTO_FIJADAS." >&2
      exit 2
    fi
    fijadas[$nombre]=$destino
  done < "$REPARTO_FIJADAS"
  for nombre in "${!fijadas[@]}"; do
    if ! printf '%s\n' "$clases" | grep -qxF "$nombre"; then
      echo "Aviso: la clase fijada $nombre no está en el listado; su línea de $REPARTO_FIJADAS ha caducado." >&2
    fi
  done
fi

mias=$(printf '%s\n' "$clases" | while IFS= read -r clase; do
  if [ -n "${fijadas[$clase]:-}" ]; then
    destino=${fijadas[$clase]}
  else
    # Bloques numerados 1..total; el bucket 0..total-1 es bloque bucket+1.
    destino=$(( $(printf '%s' "$clase" | cksum | cut -d' ' -f1) % total + 1 ))
  fi
  if [ "$destino" -eq "$bloque" ]; then
    printf '%s\n' "$clase"
  fi
done)

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
