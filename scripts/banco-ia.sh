#!/usr/bin/env bash
# Banco de modelos de IA: mide con modelo real las siete rutas Anthropic del
# producto (tests/CaeManager.Web.Tests/BancoModelosIa). Un solo lanzador para
# el equipo local y para CI (.github/workflows/integraciones-con-clave.yml).
#
# Uso:
#   bash scripts/banco-ia.sh [--modelo M] [--esfuerzo E] [--rutas R1,R2]
#                            [--max-tokens N] [--salida DIR]
#                            [--precio-entrada N --precio-salida N]
#                            [--solo-estimar | --anotar informe.json] [--sin-compilar]
#
#   --modelo        modelo a medir (por defecto, el del producto).
#   --esfuerzo      low | medium | high | xhigh | max (por defecto, el del producto).
#   --rutas         nombres de RutaIa separados por comas (por defecto, las siete).
#   --max-tokens    tope de tokens de salida (por defecto, el del producto). El
#                   razonamiento cuenta contra él: súbelo al comparar esfuerzos.
#   --salida        carpeta del informe (por defecto, ./resultados-banco-ia).
#   --precio-*      dólares por millón de tokens, para un modelo que aún no esté
#                   en tests/CaeManager.Web.Tests/BancoModelosIa/tarifas-anthropic.json.
#   --solo-estimar  NO llama a la API ni necesita clave: cuenta los casos y da
#                   una cota superior del coste.
#   --anotar        pasa al libro de gasto local un informe JSON ya medido (el
#                   artefacto de una ejecución de CI). No llama a la API.
#   --sin-compilar  usa los binarios ya compilados (lo que hace CI).
#
# LA CLAVE. En CI llega en la variable Anthropic__ApiKey. En local se lee de un
# fichero FUERA de todo repositorio, con una línea `Anthropic__ApiKey=…`:
#   $TALVEG_BANCO_IA_ENV, o por defecto ~/.talveg/banco-ia.env
# No se pone como variable de entorno del usuario ni en user-secrets a
# propósito: cualquier suite que viera Anthropic__ApiKey en el entorno activaría
# sus tests con modelo real y gastaría sin que nadie lo pidiera. Este guion la
# exporta SOLO al proceso que ejecuta el banco (la compilación va antes y sin
# ella), por el entorno y no por la línea de órdenes, no la escribe nunca y la
# tacha de la salida si algo llegara a imprimirla.
#
# EL GASTO. En local, cada ejecución añade una línea por ruta al libro de gasto
# ($TALVEG_BANCO_IA_LIBRO, por defecto banco-ia-gasto.csv junto al fichero de la
# clave) y el resumen da el acumulado del mes frente al presupuesto
# ($TALVEG_BANCO_IA_PRESUPUESTO, por defecto 240 $).
#
# Salida: 0 medido · 1 la medición no es válida o el test falló · 2 uso
# incorrecto · 3 NO ejecutado (falta la clave).

set -uo pipefail

RAIZ=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
DOTNET="${BANCO_IA_DOTNET:-dotnet}"
PROYECTO="tests/CaeManager.Web.Tests"

modelo="" esfuerzo="" rutas="" salida="" precio_entrada="" precio_salida="" anotar="" max_tokens=""
solo_estimar=0 compilar=1

uso() { sed -n '2,41p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; }

while [ $# -gt 0 ]; do
  case "$1" in
    --modelo|--esfuerzo|--rutas|--max-tokens|--salida|--precio-entrada|--precio-salida|--anotar)
      # Sin esto, una opción sin valor al final dejaría `shift 2` sin desplazar y el bucle no acabaría.
      if [ $# -lt 2 ]; then echo "ERROR: falta el valor de $1." >&2; exit 2; fi ;;
  esac
  case "$1" in
    --modelo) modelo="${2:-}"; shift 2 ;;
    --esfuerzo) esfuerzo="${2:-}"; shift 2 ;;
    --rutas) rutas="${2:-}"; shift 2 ;;
    --max-tokens) max_tokens="${2:-}"; shift 2 ;;
    --salida) salida="${2:-}"; shift 2 ;;
    --precio-entrada) precio_entrada="${2:-}"; shift 2 ;;
    --precio-salida) precio_salida="${2:-}"; shift 2 ;;
    --anotar) anotar="${2:-}"; shift 2 ;;
    --solo-estimar) solo_estimar=1; shift ;;
    --sin-compilar) compilar=0; shift ;;
    -h|--help) uso; exit 0 ;;
    *) echo "ERROR: opción desconocida '$1'." >&2; uso >&2; exit 2 ;;
  esac
done

# Se compara el valor ENTERO con `[[ =~ ]]`, no línea a línea con grep: con grep
# bastaría que una línea casara para colar un salto de línea y lo que viniera detrás.
# El modelo y el esfuerzo no admiten coma: son un campo del libro de gasto, que es un CSV.
presupuesto="${TALVEG_BANCO_IA_PRESUPUESTO:-240}"
nombre='^[A-Za-z0-9._-]*$'
lista='^[A-Za-z0-9._,\ -]*$'
importe='^([0-9]+(\.[0-9]+)?)?$'
if ! [[ $modelo =~ $nombre && $esfuerzo =~ $nombre ]]; then
  echo "ERROR: --modelo y --esfuerzo solo admiten letras, cifras, punto y guion." >&2
  exit 2
fi
if ! [[ $rutas =~ $lista ]]; then
  echo "ERROR: --rutas solo admite letras, cifras, punto, guion, coma y espacio." >&2
  exit 2
fi
if ! [[ $precio_entrada =~ $importe && $precio_salida =~ $importe ]]; then
  echo "ERROR: --precio-entrada y --precio-salida son importes con punto decimal (p. ej. 2.5)." >&2
  exit 2
fi
if ! [[ $presupuesto =~ ^[0-9]+(\.[0-9]+)?$ ]] || [[ $presupuesto =~ ^[0.]+$ ]]; then
  echo "ERROR: TALVEG_BANCO_IA_PRESUPUESTO debe ser un importe mayor que cero, con punto decimal." >&2
  exit 2
fi
if ! [[ $max_tokens =~ ^([1-9][0-9]*)?$ ]]; then
  echo "ERROR: --max-tokens debe ser un entero positivo." >&2
  exit 2
fi
if [ "$solo_estimar" -eq 1 ] && [ -n "$anotar" ]; then
  echo "ERROR: --solo-estimar y --anotar no se combinan." >&2
  exit 2
fi
if [ -n "$anotar" ] && [ ! -f "$anotar" ]; then
  echo "ERROR: no existe el informe '$anotar'." >&2
  exit 2
fi

salida="${salida:-$RAIZ/resultados-banco-ia}"
mkdir -p "$salida"
salida=$(cd "$salida" && pwd)

fichero_clave="${TALVEG_BANCO_IA_ENV:-$HOME/.talveg/banco-ia.env}"
libro="${TALVEG_BANCO_IA_LIBRO:-$(dirname "$fichero_clave")/banco-ia-gasto.csv}"

# ── La clave: solo para medir. Estimar y anotar no la necesitan ni la reciben.
clave=""
if [ "$solo_estimar" -eq 0 ] && [ -z "$anotar" ]; then
  clave="${Anthropic__ApiKey:-}"
  if [ -z "$clave" ] && [ -f "$fichero_clave" ]; then
    # Se LEE la línea, no se ejecuta el fichero: un `source` correría lo que hubiera dentro.
    clave=$(grep -m1 '^Anthropic__ApiKey=' "$fichero_clave" | cut -d= -f2- | tr -d '\r"' | sed -e 's/[[:space:]]#.*$//' -e "s/^[[:space:]']*//" -e "s/[[:space:]']*$//")
  fi
  if [ -z "$clave" ]; then
    echo "BANCO DE MODELOS DE IA — NO EJECUTADO: no hay clave de Anthropic." >&2
    echo "  Crea el fichero '$fichero_clave' con una línea  Anthropic__ApiKey=<tu clave>" >&2
    echo "  (o apunta a otro con TALVEG_BANCO_IA_ENV). No se ha medido nada ni se ha gastado nada." >&2
    echo "  Para ver qué costaría sin clave:  bash scripts/banco-ia.sh --solo-estimar" >&2
    exit 3
  fi
  case "$clave" in
    sk-ant-admin*)
      echo "ERROR: la clave es de ADMINISTRACIÓN de Anthropic (sk-ant-admin…). Usa una clave de la API de un workspace de Anthropic acotado." >&2
      exit 2 ;;
  esac
fi

# ── El entorno del proceso del banco, y de ningún otro.
entorno=(
  "BANCO_IA_MODELO=$modelo" "BANCO_IA_ESFUERZO=$esfuerzo" "BANCO_IA_RUTAS=$rutas" "BANCO_IA_SALIDA=$salida"
  "BANCO_IA_PRECIO_ENTRADA=$precio_entrada" "BANCO_IA_PRECIO_SALIDA=$precio_salida" "BANCO_IA_MAX_TOKENS=$max_tokens"
  "BANCO_IA_PRESUPUESTO_MES=$presupuesto"
)
if [ "$solo_estimar" -eq 1 ]; then
  filtro="FullyQualifiedName~BancoModelosIaTests.Estima_el_coste_sin_llamar_a_la_API"
  entorno+=("BANCO_IA_SOLO_ESTIMAR=1")
  titulo="estimación sin llamadas"
elif [ -n "$anotar" ]; then
  filtro="FullyQualifiedName~BancoModelosIaTests.Anota_en_el_libro_de_gasto_un_informe_ya_medido"
  entorno+=("BANCO_IA_ANOTAR=$(cd "$(dirname "$anotar")" && pwd)/$(basename "$anotar")" "BANCO_IA_LIBRO_GASTO=$libro" "BANCO_IA_ORIGEN=ci")
  titulo="anotación en el libro de gasto"
else
  filtro="FullyQualifiedName~BancoModelosIaTests.Mide_las_rutas_Anthropic"
  # BANCO_IA_EJECUTAR es la petición expresa: sin ella el test se omite aunque haya clave.
  # La clave NO va en esta lista: se exporta aparte, más abajo, para que no pase por ninguna línea de órdenes.
  entorno+=("BANCO_IA_EJECUTAR=1")
  if [ "${GITHUB_ACTIONS:-}" = "true" ]; then
    entorno+=("BANCO_IA_ORIGEN=ci")
  else
    # El libro de gasto es local: en CI no hay dónde dejarlo y el artefacto JSON se anota después con --anotar.
    entorno+=("BANCO_IA_LIBRO_GASTO=$libro" "BANCO_IA_ORIGEN=local")
  fi
  titulo="medición con modelo real"
fi

argumentos=(test "$PROYECTO" --no-build --filter "$filtro" --logger "trx;LogFileName=banco-ia.trx" --logger "console;verbosity=normal" --results-directory "$salida")

echo "Banco de modelos de IA — $titulo (modelo: ${modelo:-el del producto}; esfuerzo: ${esfuerzo:-el del producto}; rutas: ${rutas:-todas})"
cd "$RAIZ" || exit 1

# Se compila ANTES y SIN la clave: MSBuild, los analizadores y los procesos de
# compilación que quedan vivos para la siguiente vez no tienen por qué verla.
if [ "$compilar" -eq 1 ]; then
  if ! (unset Anthropic__ApiKey BANCO_IA_EJECUTAR; exec "$DOTNET" build "$PROYECTO" -warnaserror) > "$salida/banco-ia-compilacion.log" 2>&1; then
    tail -40 "$salida/banco-ia-compilacion.log" >&2
    echo "ERROR: la compilación falló; no se ha medido nada ni se ha gastado nada." >&2
    exit 1
  fi
fi

rm -f "$salida/banco-ia.trx"
marca="$salida/.banco-ia-inicio"
: > "$marca"

# La clave llega al proceso por su ENTORNO (un `export` dentro del subshell) y
# nunca como argumento: una línea de órdenes la ve cualquiera que liste procesos.
# Estimar y anotar corren SIN ella aunque el entorno de quien lanza la traiga.
# La salida pasa por un filtro que tacha la clave: nada de lo que el banco
# escribe la contiene, pero una traza de un tercero no debe poder filtrarla.
(
  unset Anthropic__ApiKey BANCO_IA_EJECUTAR
  export "${entorno[@]}"
  if [ -n "$clave" ]; then export Anthropic__ApiKey="$clave"; fi
  exec "$DOTNET" "${argumentos[@]}"
) 2>&1 | while IFS= read -r linea || [ -n "$linea" ]; do
  if [ -n "$clave" ]; then linea="${linea//"$clave"/[clave oculta]}"; fi
  printf '%s\n' "$linea"
done
codigo=${PIPESTATUS[0]}

# El registrador escribe en el .trx lo que el test saca por su salida, sin pasar
# por el filtro de arriba. Si la clave apareciera en algún fichero de la carpeta,
# ese fichero se borra y la ejecución se da por mala.
if [ -n "$clave" ]; then
  sucios=$(grep -rlF -- "$clave" "$salida" 2>/dev/null)
  if [ -n "$sucios" ]; then
    printf '%s\n' "$sucios" | while IFS= read -r sucio; do rm -f "$sucio"; done
    echo "ERROR: la clave apareció en un fichero de la carpeta de salida; se ha borrado. Revisa qué la escribió antes de repetir." >&2
    exit 1
  fi
fi

# ── No basta el código de salida: un test omitido también sale con 0.
trx="$salida/banco-ia.trx"
if [ ! -f "$trx" ]; then
  echo "ERROR: no se generó el .trx: no hay prueba de que se ejecutara nada." >&2
  exit 1
fi
pasados=$(grep -o 'outcome="Passed"' "$trx" | wc -l)
omitidos=$(grep -o 'outcome="NotExecuted"' "$trx" | wc -l)
if [ "$omitidos" -gt 0 ] || { [ "$codigo" -eq 0 ] && [ "$pasados" -ne 1 ]; }; then
  echo "ERROR: el test del banco no se ejecutó (pasados=$pasados, omitidos=$omitidos). Verde falso descartado." >&2
  exit 1
fi

# La tabla, a la vista: es el resultado, y el registrador de tests no la enseña.
if [ -z "$anotar" ]; then
  # Solo la de ESTA ejecución: una tabla anterior no es el resultado de esta.
  tabla=$(find "$salida" -maxdepth 1 -name 'banco-ia-*.md' -newer "$marca" 2>/dev/null | head -1)
  if [ -n "$tabla" ]; then echo; cat "$tabla"; echo; fi
fi
if [ "$solo_estimar" -eq 0 ] && [ -z "$anotar" ]; then
  echo "Informe (JSON, CSV y tabla): $salida"
  [ "${GITHUB_ACTIONS:-}" = "true" ] || echo "Libro de gasto: $libro"
fi
if [ "$codigo" -ne 0 ]; then
  echo "ERROR: la medición NO es válida para comparar (o el test falló). El informe dice por qué." >&2
  exit 1
fi
exit 0
