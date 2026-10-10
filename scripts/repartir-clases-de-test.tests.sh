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

# EL SUELO. Un descubrimiento colapsado tiene que saltar POR EL SUELO, no por
# la igualdad del reparto: esa comparacion no lo ve, porque sus dos lados
# salen de la misma lista truncada.
LISTADO_DE_TESTS="$LISTADO" MINIMO_CLASES=120 bash "$GUION" proyecto 1 1 >/dev/null 2>&1
comprobar "4 clases con suelo de 120 salta" "1" "$?"

salida=$(LISTADO_DE_TESTS="$LISTADO" MINIMO_CLASES=120 bash "$GUION" proyecto 1 1 2>&1 >/dev/null | head -1)
case "$salida" in
  *"por debajo del suelo"*) comprobar "y salta POR EL SUELO, no por otra cosa" "si" "si" ;;
  *) comprobar "y salta POR EL SUELO, no por otra cosa" "si" "no ($salida)" ;;
esac

# Con el suelo por debajo del numero real, no estorba.
LISTADO_DE_TESTS="$LISTADO" MINIMO_CLASES=4 bash "$GUION" proyecto 1 1 >/dev/null 2>&1
comprobar "4 clases con suelo de 4 pasa" "0" "$?"

# Sin suelo declarado, el guion no lo inventa.
LISTADO_DE_TESTS="$LISTADO" bash "$GUION" proyecto 1 1 >/dev/null 2>&1
comprobar "sin MINIMO_CLASES no se aplica suelo" "0" "$?"

# Argumentos invalidos: bloque fuera de rango.
LISTADO_DE_TESTS="$LISTADO" bash "$GUION" proyecto 4 5 >/dev/null 2>&1
comprobar "bloque fuera de rango sale con error de uso" "2" "$?"

# CLASES FIJADAS (REPARTO_FIJADAS). Una clase nombrada va al bloque que dice el
# fichero; el resto no se mueve de donde lo deja su hash.
FIJADAS=$(mktemp)
bloque_de() {
  # bloque_de <clase> [fichero de fijadas]: en que bloque de 2 cae la clase.
  local b
  for b in 1 2; do
    if REPARTO_FIJADAS="${2:-}" LISTADO_DE_TESTS="$LISTADO" bash "$GUION" proyecto 2 "$b" 2>/dev/null \
         | tr '|' '\n' | grep -qxF "FullyQualifiedName~$1."; then
      printf '%s' "$b"
    fi
  done
}
ALFA=CaeManager.IntegrationTests.Alertas.AlfaTests
por_hash=$(bloque_de "$ALFA")
otro=$(( 3 - por_hash ))
printf '# comentario\r\n\r\n%s %s\r\n' "$ALFA" "$otro" > "$FIJADAS"
comprobar "una clase fijada va al bloque del fichero, no al de su hash" "$otro" "$(bloque_de "$ALFA" "$FIJADAS")"

# Fijar una clase no mueve a ninguna otra.
movidas=0
for c in CaeManager.IntegrationTests.BetaTests CaeManager.IntegrationTests.GammaTests CaeManager.IntegrationTests.DeltaTests; do
  [ "$(bloque_de "$c")" = "$(bloque_de "$c" "$FIJADAS")" ] || movidas=$((movidas + 1))
done
comprobar "las clases no fijadas siguen en el bloque de su hash" "0" "$movidas"

# Con fijadas la particion sigue cubriendo todo sin repetir.
union=""
for b in 1 2; do
  union+=$(REPARTO_FIJADAS="$FIJADAS" LISTADO_DE_TESTS="$LISTADO" bash "$GUION" proyecto 2 "$b" 2>/dev/null | tr '|' '\n')$'\n'
done
comprobar "con fijadas, 2 bloques cubren las 4 clases sin repetir" "4 4" \
  "$(printf '%s' "$union" | grep -c .) $(printf '%s' "$union" | grep . | sort -u | wc -l | tr -d ' ')"

# Un bloque mayor que el total dejaria la clase sin correr en ninguno.
printf '%s 3\n' "$ALFA" > "$FIJADAS"
REPARTO_FIJADAS="$FIJADAS" LISTADO_DE_TESTS="$LISTADO" bash "$GUION" proyecto 2 1 >/dev/null 2>&1
comprobar "una clase fijada a un bloque que no existe sale con error de uso" "2" "$?"

printf '%s uno\n' "$ALFA" > "$FIJADAS"
REPARTO_FIJADAS="$FIJADAS" LISTADO_DE_TESTS="$LISTADO" bash "$GUION" proyecto 2 1 >/dev/null 2>&1
comprobar "un bloque que no es un numero sale con error de uso" "2" "$?"

printf '%s 1\n%s 2\n' "$ALFA" "$ALFA" > "$FIJADAS"
REPARTO_FIJADAS="$FIJADAS" LISTADO_DE_TESTS="$LISTADO" bash "$GUION" proyecto 2 1 >/dev/null 2>&1
comprobar "la misma clase fijada dos veces sale con error de uso" "2" "$?"

REPARTO_FIJADAS="$FIJADAS.no-existe" LISTADO_DE_TESTS="$LISTADO" bash "$GUION" proyecto 2 1 >/dev/null 2>&1
comprobar "un fichero de fijadas que no existe sale con error de uso" "2" "$?"

# Una linea caducada (clase que ya no esta en el listado) avisa y no rompe.
printf 'CaeManager.IntegrationTests.YaNoExisteTests 1\n' > "$FIJADAS"
aviso=$(REPARTO_FIJADAS="$FIJADAS" LISTADO_DE_TESTS="$LISTADO" bash "$GUION" proyecto 1 1 2>&1 >/dev/null | grep -c 'ha caducado')
comprobar "una clase fijada que ya no existe avisa" "1" "$aviso"
REPARTO_FIJADAS="$FIJADAS" LISTADO_DE_TESTS="$LISTADO" bash "$GUION" proyecto 1 1 >/dev/null 2>&1
comprobar "y no rompe el reparto" "0" "$?"
rm -f "$FIJADAS"

# El fichero real: cada linea nombra una clase que existe en el arbol y un
# bloque de la matriz de ci.yml. No detecta una clase movida de espacio de
# nombres sin renombrar el fichero; eso lo cubre el aviso de arriba en CI.
REAL="$AQUI/integracion-clases-fijadas.txt"
malas=0; lineas=0
while read -r nombre destino _; do
  case "$nombre" in ''|'#'*) continue ;; esac
  lineas=$((lineas + 1))
  corto=${nombre##*.}
  grep -rqE "class $corto\b" "$AQUI/../tests/CaeManager.IntegrationTests" --include='*.cs' || malas=$((malas + 1))
  case "$destino" in 1|2|3|4) ;; *) malas=$((malas + 1)) ;; esac
done < "$REAL"
comprobar "el fichero real nombra clases que existen y bloques de 1 a 4" "0" "$malas"
[ "$lineas" -ge 1 ]
comprobar "el fichero real tiene al menos una clase fijada" "0" "$?"

if [ "$fallos" -gt 0 ]; then
  echo "$fallos comprobacion(es) fallaron."
  exit 1
fi
echo "Todas las comprobaciones pasaron."
