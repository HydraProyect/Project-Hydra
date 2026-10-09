#!/usr/bin/env bash
# Tests de scripts/banco-ia.sh
#
# No necesitan .NET, red ni clave: `dotnet` se sustituye (BANCO_IA_DOTNET) por
# un doble que apunta qué entorno recibió, IMPRIME la clave a propósito y deja
# el .trx que se le pida. Lo que se comprueba es lo que puede romperse en
# silencio: que la clave no salga por ningún sitio, que solo la reciba la
# medición, y que un test omitido no se cuente como medido.
#
# BANCO_IA_SCRIPT permite apuntar a una copia mutada del guion (prueba de
# sensibilidad) sin tocar el árbol de trabajo.

set -uo pipefail

AQUI=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
GUION="${BANCO_IA_SCRIPT:-$AQUI/banco-ia.sh}"

# No es una clave: es un canario que no debe aparecer en ninguna salida.
CANARIO="canario-banco-ia-0123456789-no-es-una-clave"

BASE=$(mktemp -d)
trap 'rm -rf "$BASE"' EXIT

# El entorno del que parte cada caso: sin clave, sin CI y sin rutas heredadas.
unset Anthropic__ApiKey GITHUB_ACTIONS TALVEG_BANCO_IA_ENV TALVEG_BANCO_IA_LIBRO TALVEG_BANCO_IA_PRESUPUESTO

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
contiene() { if grep -qF -- "$2" "$1" 2>/dev/null; then echo si; else echo no; fi; }
aparece_en() { if grep -rqF -- "$2" "$1" 2>/dev/null; then echo si; else echo no; fi; }

# ── El doble de `dotnet`.
FALSO="$BASE/dotnet-falso.sh"
cat > "$FALSO" <<'DOBLE'
#!/usr/bin/env bash
salida=""
previo=""
for a in "$@"; do
  [ "$previo" = "--results-directory" ] && salida="$a"
  previo="$a"
done
{
  echo "argumentos=$*"
  echo "clave=${Anthropic__ApiKey:-<ninguna>}"
  echo "modelo=${BANCO_IA_MODELO:-}"
  echo "esfuerzo=${BANCO_IA_ESFUERZO:-}"
  echo "rutas=${BANCO_IA_RUTAS:-}"
  echo "libro=${BANCO_IA_LIBRO_GASTO:-<ninguno>}"
  echo "origen=${BANCO_IA_ORIGEN:-<ninguno>}"
  echo "estimar=${BANCO_IA_SOLO_ESTIMAR:-<no>}"
  echo "anotar=${BANCO_IA_ANOTAR:-<no>}"
  echo "presupuesto=${BANCO_IA_PRESUPUESTO_MES:-}"
  echo "ejecutar=${BANCO_IA_EJECUTAR:-<no>}"
  echo "max_tokens=${BANCO_IA_MAX_TOKENS:-}"
} > "$FALSO_REGISTRO"
# Una traza de un tercero que vuelca la cabecera de la petición.
echo "traza: x-api-key: ${Anthropic__ApiKey:-}"
echo "traza por error: ${Anthropic__ApiKey:-}" >&2
case "${FALSO_MODO:-pasa}" in
  pasa)    echo '<UnitTestResult outcome="Passed" />' > "$salida/banco-ia.trx"; exit 0 ;;
  omitido) echo '<UnitTestResult outcome="NotExecuted" />' > "$salida/banco-ia.trx"; exit 0 ;;
  ninguno) echo '<TestRun />' > "$salida/banco-ia.trx"; exit 0 ;;
  pasa-y-omite) printf '%s
' '<UnitTestResult outcome="Passed" />' '<UnitTestResult outcome="NotExecuted" />' > "$salida/banco-ia.trx"; exit 0 ;;
  falla)   echo '<UnitTestResult outcome="Failed" />' > "$salida/banco-ia.trx"; exit 1 ;;
  sin-trx) exit 0 ;;
esac
DOBLE
chmod +x "$FALSO"
export BANCO_IA_DOTNET="$FALSO"

nuevo_caso() {
  CASO="$BASE/$1"
  mkdir -p "$CASO/casa/.talveg" "$CASO/salida"
  export FALSO_REGISTRO="$CASO/registro"
  export HOME="$CASO/casa"
  unset FALSO_MODO
}
registro() { grep -m1 "^$1=" "$CASO/registro" 2>/dev/null | cut -d= -f2-; }

echo "Sin clave"
nuevo_caso sin-clave
bash "$GUION" --salida "$CASO/salida" > "$CASO/o" 2>&1; rc=$?
comprobar "sin fichero ni variable: sale con 3" "3" "$rc"
comprobar "y lo dice de forma visible" "si" "$(contiene "$CASO/o" "NO EJECUTADO")"
comprobar "y no lanza nada" "no" "$([ -e "$CASO/registro" ] && echo si || echo no)"

nuevo_caso fichero-sin-la-linea
echo "OTRA_COSA=1" > "$HOME/.talveg/banco-ia.env"
bash "$GUION" --salida "$CASO/salida" > "$CASO/o" 2>&1; rc=$?
comprobar "fichero sin la línea de la clave: sale con 3 y no lanza" "3 no" "$rc $([ -e "$CASO/registro" ] && echo si || echo no)"

echo "La clave del fichero local"
nuevo_caso fichero-por-defecto
echo "Anthropic__ApiKey=$CANARIO" > "$HOME/.talveg/banco-ia.env"
bash "$GUION" --modelo claude-haiku-5-5 --esfuerzo low --rutas ChatDelAsistente --max-tokens 8000 --sin-compilar --salida "$CASO/salida" > "$CASO/o" 2>&1; rc=$?
comprobar "con ~/.talveg/banco-ia.env: mide y sale con 0" "0" "$rc"
comprobar "el proceso del banco recibe la clave" "$CANARIO" "$(registro clave)"
comprobar "y los parámetros" "claude-haiku-5-5 low ChatDelAsistente 8000" "$(registro modelo) $(registro esfuerzo) $(registro rutas) $(registro max_tokens)"
comprobar "y la petición expresa, sin la que el test se omite" "1" "$(registro ejecutar)"
comprobar "la clave que imprime un tercero NO sale por la salida" "no" "$(contiene "$CASO/o" "$CANARIO")"
comprobar "sale tachada (el doble sí la imprimió, por las dos salidas)" "2" "$(grep -cF '[clave oculta]' "$CASO/o")"
comprobar "ni queda en la carpeta del informe" "no" "$(aparece_en "$CASO/salida" "$CANARIO")"
comprobar "el libro de gasto va junto al fichero de la clave" "$HOME/.talveg/banco-ia-gasto.csv local" "$(registro libro) $(registro origen)"
comprobar "--sin-compilar llega como --no-build" "si" "$(contiene "$CASO/registro" "--no-build")"
comprobar "el presupuesto por defecto es 240" "240" "$(registro presupuesto)"

nuevo_caso fichero-con-crlf-y-comillas
printf '# comentario\r\nAnthropic__ApiKey="%s"\r\n' "$CANARIO" > "$CASO/otra.env"
TALVEG_BANCO_IA_ENV="$CASO/otra.env" bash "$GUION" --salida "$CASO/salida" > "$CASO/o" 2>&1; rc=$?
comprobar "TALVEG_BANCO_IA_ENV, CRLF y comillas: la clave llega limpia" "0 $CANARIO" "$rc $(registro clave)"
comprobar "y el libro sigue al fichero de la clave" "$CASO/banco-ia-gasto.csv" "$(registro libro)"

nuevo_caso fichero-no-se-ejecuta
printf 'touch "%s/ejecutado"\nAnthropic__ApiKey=%s\n' "$CASO" "$CANARIO" > "$HOME/.talveg/banco-ia.env"
bash "$GUION" --salida "$CASO/salida" > "$CASO/o" 2>&1
comprobar "el fichero se lee, no se ejecuta" "no" "$([ -e "$CASO/ejecutado" ] && echo si || echo no)"

nuevo_caso clave-de-administracion
echo "Anthropic__ApiKey=sk-ant-admin-canario" > "$HOME/.talveg/banco-ia.env"
bash "$GUION" --salida "$CASO/salida" > "$CASO/o" 2>&1; rc=$?
comprobar "clave de administración: se rechaza con 2 y no lanza" "2 no" "$rc $([ -e "$CASO/registro" ] && echo si || echo no)"
comprobar "y el aviso no repite la clave" "no" "$(contiene "$CASO/o" "sk-ant-admin-canario")"

echo "En CI"
nuevo_caso ci
GITHUB_ACTIONS=true Anthropic__ApiKey="$CANARIO" bash "$GUION" --salida "$CASO/salida" > "$CASO/o" 2>&1; rc=$?
comprobar "la clave llega por variable y no hay libro local" "0 $CANARIO <ninguno> ci" "$rc $(registro clave) $(registro libro) $(registro origen)"
comprobar "tampoco sale por la salida" "no" "$(contiene "$CASO/o" "$CANARIO")"

echo "Estimar y anotar no reciben la clave"
nuevo_caso solo-estimar
Anthropic__ApiKey="$CANARIO" bash "$GUION" --solo-estimar --salida "$CASO/salida" > "$CASO/o" 2>&1; rc=$?
comprobar "--solo-estimar: corre sin clave aunque el entorno la traiga" "0 <ninguna> 1" "$rc $(registro clave) $(registro estimar)"
comprobar "y sin la petición de medir, aunque el entorno la traiga" "<no>" "$(BANCO_IA_EJECUTAR=1 bash "$GUION" --solo-estimar --salida "$CASO/salida" > /dev/null 2>&1; registro ejecutar)"
comprobar "y solo el test de estimación" "si" "$(contiene "$CASO/registro" "Estima_el_coste_sin_llamar_a_la_API")"

nuevo_caso solo-estimar-sin-nada
bash "$GUION" --solo-estimar --salida "$CASO/salida" > "$CASO/o" 2>&1; rc=$?
comprobar "--solo-estimar no necesita el fichero de la clave" "0" "$rc"

nuevo_caso anotar
echo '{}' > "$CASO/informe.json"
Anthropic__ApiKey="$CANARIO" bash "$GUION" --anotar "$CASO/informe.json" --salida "$CASO/salida" > "$CASO/o" 2>&1; rc=$?
comprobar "--anotar: sin clave, con libro y origen ci" "0 <ninguna> $HOME/.talveg/banco-ia-gasto.csv ci" "$rc $(registro clave) $(registro libro) $(registro origen)"
comprobar "y el informe llega con ruta absoluta" "$(cd "$CASO" && pwd)/informe.json" "$(registro anotar)"

nuevo_caso anotar-inexistente
bash "$GUION" --anotar "$CASO/no-existe.json" --salida "$CASO/salida" > "$CASO/o" 2>&1; rc=$?
comprobar "--anotar con un informe que no existe: 2 y no lanza" "2 no" "$rc $([ -e "$CASO/registro" ] && echo si || echo no)"

echo "Un verde que no midió no es un verde"
for modo in omitido pasa-y-omite ninguno sin-trx falla; do
  nuevo_caso "modo-$modo"
  echo "Anthropic__ApiKey=$CANARIO" > "$HOME/.talveg/banco-ia.env"
  FALSO_MODO=$modo bash "$GUION" --salida "$CASO/salida" > "$CASO/o" 2>&1; rc=$?
  comprobar "test '$modo': sale con 1" "1" "$rc"
done

nuevo_caso trx-viejo
echo "Anthropic__ApiKey=$CANARIO" > "$HOME/.talveg/banco-ia.env"
echo '<UnitTestResult outcome="Passed" />' > "$CASO/salida/banco-ia.trx"
FALSO_MODO=sin-trx bash "$GUION" --salida "$CASO/salida" > "$CASO/o" 2>&1; rc=$?
comprobar "un .trx de una ejecución anterior no vale como prueba" "1" "$rc"

echo "Uso incorrecto"
nuevo_caso uso
echo "Anthropic__ApiKey=$CANARIO" > "$HOME/.talveg/banco-ia.env"
bash "$GUION" --no-existe > "$CASO/o" 2>&1; rc=$?
comprobar "opción desconocida: 2" "2" "$rc"
for malo in 'x;touch marca' '$(id)' 'a|b' "a'b"; do
  bash "$GUION" --modelo "$malo" --salida "$CASO/salida" > "$CASO/o" 2>&1; rc=$?
  comprobar "--modelo '$malo': se rechaza con 2 y no lanza" "2 no" "$rc $([ -e "$CASO/registro" ] && echo si || echo no)"
done
for malo in 0 -5 1.5 mil; do
  bash "$GUION" --max-tokens "$malo" --salida "$CASO/salida" > "$CASO/o" 2>&1; rc=$?
  comprobar "--max-tokens '$malo': se rechaza con 2 y no lanza" "2 no" "$rc $([ -e "$CASO/registro" ] && echo si || echo no)"
done
bash "$GUION" --solo-estimar --anotar "$CASO/o" > "$CASO/o2" 2>&1; rc=$?
comprobar "--solo-estimar con --anotar: 2" "2" "$rc"

echo
if [ "$fallos" -eq 0 ]; then
  echo "TODOS LOS CASOS EN VERDE"
  exit 0
fi
echo "$fallos caso(s) en FALLO"
exit 1
