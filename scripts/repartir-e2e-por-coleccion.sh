#!/usr/bin/env bash
# Reparte las clases de la suite E2E en N bloques POR COLECCIÓN de xUnit y
# escribe el filtro de `dotnet test` del bloque pedido.
#
# Por qué por colección y no por clase (como scripts/repartir-clases-de-test.sh
# hace con integración): en E2E cada colección comparte una fixture que arranca
# la aplicación real contra su base. Partir una colección entre dos bloques
# arrancaría esa aplicación dos veces y no ahorraría reloj; una colección entera
# va siempre a un solo bloque.
#
# Uso:   LISTADO_DE_TESTS=<fichero> repartir-e2e-por-coleccion.sh <total-bloques> <bloque>
#        repartir-e2e-por-coleccion.sh --mapa      (solo imprime clase<TAB>colección)
# Sale:  el filtro por stdout; el plan del reparto por stderr.
#        1 si el bloque quedaría vacío, si una clase aparece en dos colecciones
#        o si el descubrimiento está por debajo del suelo; 2 si los argumentos
#        no valen.
#
# DE DÓNDE SALE CADA COSA
#   - Las clases que existen: del listado de `dotnet test --list-tests`
#     (LISTADO_DE_TESTS), no de los fuentes. Una clase sin tests no cuenta.
#   - La colección de cada clase: de los fuentes (FUENTES_E2E), leyendo el
#     atributo `[Collection("Nombre")]` en sus propios corchetes y la
#     declaración de clase que le sigue. Es la única forma que admite la guarda
#     de arquitectura ColeccionesDeE2ECongeladasTests, así que aquí no se
#     interpreta ninguna otra. La clase se identifica por su NOMBRE COMPLETO
#     (espacio de nombres + nombre): un fichero puede declarar dos clases en
#     colecciones distintas, y una clase `partial` puede repartirse en varios
#     ficheros con el atributo en una sola de sus partes.
#   - Una clase del listado sin colección en los fuentes es su propia unidad
#     (xUnit le da una colección implícita por clase).
#   - El peso de cada colección: de PESOS_E2E (segundos de reloj medidos en
#     CI). Una colección que no esté en la tabla NO se queda fuera: entra con
#     PESO_POR_DEFECTO y el guion lo dice por stderr.
#
# CÓMO REPARTE
#   Ordena las unidades de mayor a menor peso (a igual peso, por nombre) y
#   asigna cada una al bloque que menos carga lleve (a igual carga, el de
#   número más bajo). Es determinista: los N bloques calculan el mismo plan
#   por separado. La colección más pesada queda sola mientras pese más que el
#   resto repartido entre los demás bloques.
#
#   Añadir una colección o cambiar un peso puede mover otras colecciones de
#   bloque. En integración eso se evitó a propósito (reparto por hash) porque
#   las clases comparten clúster; aquí cada colección levanta su propia
#   aplicación y su propia base, y lo que se busca es equilibrar el reloj.

set -euo pipefail
export LC_ALL=C

fuentes=${FUENTES_E2E:-tests/CaeManager.E2ETests}
pesos=${PESOS_E2E:-$(dirname "${BASH_SOURCE[0]}")/e2e-pesos-por-coleccion.txt}
peso_por_defecto=${PESO_POR_DEFECTO:-60}
peso_clase_suelta=${PESO_CLASE_SIN_COLECCION:-5}

# clase<TAB>colección, una línea por atributo encontrado.
mapa_de_fuentes() {
  find "$fuentes" -name '*.cs' -not -path '*/bin/*' -not -path '*/obj/*' -print0 \
    | sort -z \
    | xargs -0 awk '
        FNR == 1 { ns = ""; pendiente = "" }
        {
          linea = $0
          sub(/\r$/, "", linea)
          if (match(linea, /^[ \t]*namespace[ \t]+[A-Za-z0-9_.]+/)) {
            s = substr(linea, RSTART, RLENGTH)
            sub(/^[ \t]*namespace[ \t]+/, "", s)
            ns = s
            next
          }
          if (linea ~ /^[ \t]*\[Collection\("[^"]+"\)\][ \t]*$/) {
            s = linea
            sub(/^[ \t]*\[Collection\("/, "", s)
            sub(/"\)\][ \t]*$/, "", s)
            pendiente = s
            next
          }
          if (match(linea, /^[ \t]*((public|internal|sealed|abstract|static|partial|file)[ \t]+)*class[ \t]+[A-Za-z_][A-Za-z0-9_]*/)) {
            s = substr(linea, RSTART, RLENGTH)
            sub(/^.*class[ \t]+/, "", s)
            if (pendiente != "") print (ns == "" ? s : ns "." s) "\t" pendiente
            pendiente = ""
            next
          }
          # Entre el atributo y la clase solo caben líneas en blanco,
          # comentarios y otros atributos. Cualquier otra cosa lo desliga.
          if (linea ~ /^[ \t]*$/ || linea ~ /^[ \t]*\/\// || linea ~ /^[ \t]*\[/) next
          pendiente = ""
        }'
}

if [ "${1:-}" = "--mapa" ]; then
  mapa_de_fuentes
  exit 0
fi

total=${1:?falta el numero total de bloques}
bloque=${2:?falta el numero de bloque}

if ! [[ "$total" =~ ^[0-9]+$ ]] || [ "$total" -lt 1 ]; then
  echo "El total de bloques debe ser un entero >= 1 (recibido: '$total')." >&2
  exit 2
fi
if ! [[ "$bloque" =~ ^[0-9]+$ ]] || [ "$bloque" -lt 1 ] || [ "$bloque" -gt "$total" ]; then
  echo "El bloque debe estar entre 1 y $total (recibido: '$bloque')." >&2
  exit 2
fi

listado=${LISTADO_DE_TESTS:?falta LISTADO_DE_TESTS: el fichero con la salida de dotnet test --list-tests}
if [ ! -f "$listado" ]; then
  echo "No existe el listado de tests '$listado'." >&2
  exit 2
fi
if [ ! -f "$pesos" ]; then
  echo "No existe la tabla de pesos '$pesos'." >&2
  exit 2
fi

# De "    Espacio.De.Nombres.MiClaseTests.MiMetodo(caso: 1)" queda
# "Espacio.De.Nombres.MiClaseTests". Solo cuentan las líneas sangradas: son
# las de los tests; las cabeceras de `dotnet test` empiezan en la columna 0.
clases=$(sed -n 's/^[[:space:]]\{1,\}\([A-Za-z0-9_][A-Za-z0-9_.]*\)\.[A-Za-z0-9_]\{1,\}.*$/\1/p' \
           "$listado" | tr -d '\r' | sort -u)

if [ -z "$clases" ]; then
  echo "No se reconocio ninguna clase de test en el listado." >&2
  exit 1
fi

# Suelo sobre el universo descubierto: mismo motivo que en
# repartir-clases-de-test.sh. Un descubrimiento truncado repartiría una suite
# incompleta y la comprobación de cobertura del reparto no lo vería, porque
# sus dos lados salen de este mismo listado.
numero=$(printf '%s\n' "$clases" | wc -l | tr -d ' ')
if [ -n "${MINIMO_CLASES:-}" ] && [ "$numero" -lt "$MINIMO_CLASES" ]; then
  echo "Se descubrieron $numero clases de test E2E, por debajo del suelo de $MINIMO_CLASES." >&2
  echo "Esto no es un reparto malo: es un descubrimiento averiado." >&2
  exit 1
fi

declare -A coleccion_de
while IFS=$'\t' read -r clase coleccion; do
  [ -z "$clase" ] && continue
  if [ -n "${coleccion_de[$clase]:-}" ] && [ "${coleccion_de[$clase]}" != "$coleccion" ]; then
    echo "La clase $clase aparece en dos colecciones: '${coleccion_de[$clase]}' y '$coleccion'." >&2
    echo "Una clase solo puede pertenecer a una; no se reparte a ciegas." >&2
    exit 1
  fi
  coleccion_de[$clase]=$coleccion
done < <(mapa_de_fuentes)

declare -A peso_en_tabla
while read -r nombre peso _; do
  case "$nombre" in ''|\#*) continue ;; esac
  if ! [[ "$peso" =~ ^[0-9]+$ ]]; then
    echo "Peso no entero para '$nombre' en $pesos: '$peso'." >&2
    exit 2
  fi
  peso_en_tabla[$nombre]=$peso
done < <(tr -d '\r' < "$pesos")

# Unidad de reparto de cada clase: su colección, o ella misma con el prefijo
# «~» si no tiene (el prefijo no puede chocar con un nombre de colección).
declare -A unidad_de peso_de
while IFS= read -r clase; do
  if [ -n "${coleccion_de[$clase]:-}" ]; then
    unidad=${coleccion_de[$clase]}
    if [ -z "${peso_de[$unidad]:-}" ]; then
      if [ -n "${peso_en_tabla[$unidad]:-}" ]; then
        peso_de[$unidad]=${peso_en_tabla[$unidad]}
      else
        peso_de[$unidad]=$peso_por_defecto
        echo "AVISO: la coleccion '$unidad' no tiene peso en $(basename "$pesos"); entra con el peso por defecto ($peso_por_defecto s). Anadela a la tabla." >&2
      fi
    fi
  else
    unidad="~$clase"
    peso_de[$unidad]=$peso_clase_suelta
  fi
  unidad_de[$clase]=$unidad
done <<< "$clases"

ordenadas=$(for unidad in "${!peso_de[@]}"; do
              printf '%s\t%s\n' "${peso_de[$unidad]}" "$unidad"
            done | sort -t$'\t' -k1,1nr -k2,2)

declare -a carga
declare -A bloque_de
for (( i = 1; i <= total; i++ )); do carga[$i]=0; done
while IFS=$'\t' read -r peso unidad; do
  mejor=1
  for (( i = 2; i <= total; i++ )); do
    if [ "${carga[$i]}" -lt "${carga[$mejor]}" ]; then mejor=$i; fi
  done
  bloque_de[$unidad]=$mejor
  carga[$mejor]=$(( ${carga[$mejor]} + peso ))
done <<< "$ordenadas"

mias=$(while IFS= read -r clase; do
         if [ "${bloque_de[${unidad_de[$clase]}]}" -eq "$bloque" ]; then
           printf '%s\n' "$clase"
         fi
       done <<< "$clases")

{
  echo "Clases en total: $numero"
  for (( i = 1; i <= total; i++ )); do
    en_bloque=$(while IFS=$'\t' read -r peso unidad; do
                  if [ "${bloque_de[$unidad]}" -eq "$i" ]; then
                    case "$unidad" in "~"*) printf 'sin coleccion: %s (%s s)\n' "${unidad#\~}" "$peso" ;;
                                      *)    printf '%s (%s s)\n' "$unidad" "$peso" ;; esac
                  fi
                done <<< "$ordenadas" | paste -sd',' - | sed 's/,/, /g')
    echo "Bloque $i de $total, ${carga[$i]} s previstos: ${en_bloque:-vacio}"
  done
} >&2

if [ -z "$mias" ]; then
  echo "El reparto dejo el bloque $bloque de $total sin ninguna clase." >&2
  echo "Un filtro que no casa con nada haria pasar el bloque en verde sin" >&2
  echo "ejecutar un solo test, asi que se falla aqui a proposito." >&2
  exit 1
fi

echo "En el bloque $bloque de $total: $(printf '%s\n' "$mias" | wc -l | tr -d ' ') clases" >&2

# REPARTO_SALIDA: la lista de clases de este bloque, una por línea, para que el
# agregador compruebe que la unión de los bloques es la suite entera.
if [ -n "${REPARTO_SALIDA:-}" ]; then
  printf '%s\n' "$mias" > "$REPARTO_SALIDA"
fi

# El punto final importa: con `~` (contiene) y sin él, una clase cuyo nombre es
# prefijo de otra arrastraría también a la segunda.
printf '%s\n' "$mias" | sed 's/$/./; s/^/FullyQualifiedName~/' | paste -sd'|' -
