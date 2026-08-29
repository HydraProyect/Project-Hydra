#!/usr/bin/env bash
# Tests de scripts/repartir-clases-de-test.sh
#
# Se le pasa un listado fijo por LISTADO_DE_TESTS, asi que no necesita
# compilar nada ni tener .NET delante: comprueba el REPARTO, que es lo que
# puede romperse en silencio.

set -uo pipefail

AQUI=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
GUION="$AQUI/repartir-clases-de-test.sh"

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

LISTADO=$(mktemp)
trap 'rm -f "$LISTADO"' EXIT
cat > "$LISTADO" <<'FIN'
The following Tests are available:
    CaeManager.IntegrationTests.Alertas.AlfaTests.HaceAlgo
    CaeManager.IntegrationTests.Alertas.AlfaTests.HaceOtraCosa
    CaeManager.IntegrationTests.BetaTests.CasoConTheory(valor: 1)
    CaeManager.IntegrationTests.GammaTests.Metodo_con_guiones_bajos
    CaeManager.IntegrationTests.DeltaTests.Metodo
FIN

echo "repartir-clases-de-test.sh"

# Cuatro clases distintas, aunque una tenga dos metodos y otra un Theory.
total=$(LISTADO_DE_TESTS="$LISTADO" bash "$GUION" proyecto 1 1 2>/dev/null | tr '|' '\n' | wc -l | tr -d ' ')
comprobar "un solo bloque recoge las 4 clases" "4" "$total"

# La particion cubre todo y no solapa: la union de los bloques son las 4.
union=""
for b in 1 2; do
  union+=$(LISTADO_DE_TESTS="$LISTADO" bash "$GUION" proyecto 2 "$b" 2>/dev/null | tr '|' '\n')$'\n'
done
distintas=$(printf '%s' "$union" | grep -c . )
unicas=$(printf '%s' "$union" | grep . | sort -u | wc -l | tr -d ' ')
comprobar "2 bloques cubren las 4 clases sin repetir" "4 4" "$distintas $unicas"

# El punto final evita que una clase prefijo arrastre a la otra.
punto=$(LISTADO_DE_TESTS="$LISTADO" bash "$GUION" proyecto 1 1 2>/dev/null)
case "$punto" in
  *"AlfaTests."*) comprobar "el filtro termina cada clase en punto" "si" "si" ;;
  *) comprobar "el filtro termina cada clase en punto" "si" "no" ;;
esac

# Un bloque vacio TIENE que fallar: en verde seria un bloque mudo que pasa.
LISTADO_DE_TESTS="$LISTADO" bash "$GUION" proyecto 9 9 >/dev/null 2>&1
comprobar "un bloque sin clases sale con error" "1" "$?"

# Un listado sin ninguna clase reconocible tampoco puede pasar en silencio.
VACIO=$(mktemp); echo "The following Tests are available:" > "$VACIO"
LISTADO_DE_TESTS="$VACIO" bash "$GUION" proyecto 1 1 >/dev/null 2>&1
comprobar "un listado sin clases sale con error" "1" "$?"
rm -f "$VACIO"

# Argumentos invalidos: bloque fuera de rango.
LISTADO_DE_TESTS="$LISTADO" bash "$GUION" proyecto 4 5 >/dev/null 2>&1
comprobar "bloque fuera de rango sale con error de uso" "2" "$?"

if [ "$fallos" -gt 0 ]; then
  echo "$fallos comprobacion(es) fallaron."
  exit 1
fi
echo "Todas las comprobaciones pasaron."
