#!/usr/bin/env bash
# Pruebas de scripts/repartir-e2e-por-coleccion.sh. No necesitan .NET ni base
# de datos: fabrican un árbol de fuentes y un listado de `--list-tests`
# sintéticos, y al final contrastan el guion con el árbol real de
# tests/CaeManager.E2ETests.
#
# Uso: bash scripts/repartir-e2e-por-coleccion.tests.sh
set -uo pipefail
export LC_ALL=C

RAIZ="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SCRIPT="$RAIZ/scripts/repartir-e2e-por-coleccion.sh"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

PRUEBAS=0
FALLOS=0
igual() {
  PRUEBAS=$((PRUEBAS + 1))
  if [[ "$2" != "$3" ]]; then
    FALLOS=$((FALLOS + 1))
    echo "FALLO: $1 — esperado '$2', obtenido '$3'" >&2
  fi
}
contiene() {
  PRUEBAS=$((PRUEBAS + 1))
  if ! grep -qF -- "$2" <<< "$3"; then
    FALLOS=$((FALLOS + 1))
    echo "FALLO: $1 — falta '$2' en: $3" >&2
  fi
}
no_contiene() {
  PRUEBAS=$((PRUEBAS + 1))
  if grep -qF -- "$2" <<< "$3"; then
    FALLOS=$((FALLOS + 1))
    echo "FALLO: $1 — sobra '$2' en: $3" >&2
  fi
}

# --- Árbol sintético -------------------------------------------------------
F="$TMP/fuentes"
mkdir -p "$F/Sub" "$F/bin" "$F/obj"

# Dos clases en un mismo fichero, cada una en su colección; entre el atributo
# y la clase caben otro atributo, un comentario y una línea en blanco.
cat > "$F/Dos.cs" <<'EOF'
namespace Suite.E2E;

[Collection("Grande")]
[Trait("x", "y")]
// comentario

public sealed class AlfaTests
{
}

[Collection("Mediana")]
public class BetaTests { }
EOF

# Clase partial: el atributo va en una sola de las dos partes.
cat > "$F/Parte1.cs" <<'EOF'
namespace Suite.E2E;
[Collection("Grande")]
public partial class GammaTests { }
EOF
cat > "$F/Parte2.cs" <<'EOF'
namespace Suite.E2E;
public partial class GammaTests { }
EOF

# Subespacio de nombres, CRLF y namespace con llaves.
printf 'namespace Suite.E2E.Sub\r\n{\r\n    [Collection("Pequena")]\r\n    public class DeltaTests { }\r\n}\r\n' > "$F/Sub/Delta.cs"

# Una clase cuyo nombre es prefijo de otra, en colecciones distintas.
cat > "$F/Prefijo.cs" <<'EOF'
namespace Suite.E2E;
[Collection("Mediana")]
public class Visita { }
[Collection("SinPeso")]
public class VisitaDetalle { }
EOF

# Sin colección, y una colección de fixture que no es una clase de test.
cat > "$F/Suelta.cs" <<'EOF'
namespace Suite.E2E;
public class SueltaTests { }

// [Collection("Comentada")]
public class OtraSueltaTests { }

[CollectionDefinition("Grande")]
public class GrandeCollection { }

[Collection("Desligada")]
public record Cosa(int X);
public class TrasUnRecordTests { }
EOF

# Lo de bin/ y obj/ no cuenta.
cat > "$F/bin/Basura.cs" <<'EOF'
namespace Suite.E2E;
[Collection("Basura")]
public class AlfaTests { }
EOF
cp "$F/bin/Basura.cs" "$F/obj/Basura.cs"

cat > "$TMP/pesos.txt" <<'EOF'
# comentario
Grande 500
Mediana 100

Pequena 90
EOF

cat > "$TMP/listado.txt" <<'EOF'
Test run for X.dll (.NETCoreApp,Version=v10.0)
The following Tests are available:
    Suite.E2E.AlfaTests.Uno
    Suite.E2E.AlfaTests.Dos(caso: 1, texto: "a.b c")
    Suite.E2E.BetaTests.Uno
    Suite.E2E.GammaTests.Uno
    Suite.E2E.Sub.DeltaTests.Uno
    Suite.E2E.Visita.Uno
    Suite.E2E.VisitaDetalle.Uno
    Suite.E2E.SueltaTests.Uno
    Suite.E2E.OtraSueltaTests.Uno
    Suite.E2E.TrasUnRecordTests.Uno
EOF

correr() { # correr <total> <bloque> [VAR=valor…]  -> SALIDA, ERR, CODIGO
  local total="$1" bloque="$2"; shift 2
  SALIDA="$(env FUENTES_E2E="$F" PESOS_E2E="$TMP/pesos.txt" LISTADO_DE_TESTS="$TMP/listado.txt" "$@" \
            bash "$SCRIPT" "$total" "$bloque" 2> "$TMP/err")"
  CODIGO=$?
  ERR="$(cat "$TMP/err")"
}

echo "=== Mapa de fuentes ==="
MAPA="$(FUENTES_E2E="$F" bash "$SCRIPT" --mapa | sort)"
ESPERADO="$(printf '%s\t%s\n' \
  Suite.E2E.AlfaTests Grande \
  Suite.E2E.BetaTests Mediana \
  Suite.E2E.GammaTests Grande \
  Suite.E2E.Sub.DeltaTests Pequena \
  Suite.E2E.Visita Mediana \
  Suite.E2E.VisitaDetalle SinPeso | sort)"
igual "el mapa lee dos clases por fichero, partial, subespacio y CRLF, y nada más" "$ESPERADO" "$MAPA"

echo "=== Reparto en tres bloques ==="
# Unidades y pesos: Grande 500 · Mediana 100 · Pequena 90 · SinPeso 60 (por
# defecto) · tres clases sueltas a 5. Plan: B1 = Grande (500); B2 = Mediana,
# dos sueltas (110); B3 = Pequena, SinPeso, una suelta (155).
: > "$TMP/union.txt"
for b in 1 2 3; do
  correr 3 "$b" REPARTO_SALIDA="$TMP/bloque-$b.txt"
  igual "bloque $b de 3 sale con 0" 0 "$CODIGO"
  cat "$TMP/bloque-$b.txt" >> "$TMP/union.txt"
  eval "FILTRO_$b=\$SALIDA"
  eval "PLAN_$b=\"\$(grep -E '^(Clases en total|Bloque )' <<< \"\$ERR\")\""
done
igual "los tres bloques calculan el mismo plan (1 y 2)" "$PLAN_1" "$PLAN_2"
igual "los tres bloques calculan el mismo plan (1 y 3)" "$PLAN_1" "$PLAN_3"
igual "la unión de los bloques son las 9 clases" 9 "$(wc -l < "$TMP/union.txt" | tr -d ' ')"
igual "ninguna clase está en dos bloques" 9 "$(sort -u "$TMP/union.txt" | wc -l | tr -d ' ')"
igual "la colección más pesada queda sola en su bloque" \
  "FullyQualifiedName~Suite.E2E.AlfaTests.|FullyQualifiedName~Suite.E2E.GammaTests." "$FILTRO_1"
contiene "el plan da 500 s al bloque 1" "Bloque 1 de 3, 500 s previstos: Grande (500 s)" "$PLAN_1"
contiene "una clase partial va entera con su colección" "Suite.E2E.GammaTests" "$(cat "$TMP/bloque-1.txt")"
contiene "la colección sin peso entra con el peso por defecto" "SinPeso (60 s)" "$PLAN_1"
contiene "y el guion avisa de que le falta peso" "AVISO: la coleccion 'SinPeso' no tiene peso" "$ERR"
contiene "las clases sin colección son unidades propias" "sin coleccion: Suite.E2E.SueltaTests (5 s)" "$PLAN_1"
contiene "un atributo comentado no asigna colección" "sin coleccion: Suite.E2E.OtraSueltaTests (5 s)" "$PLAN_1"
contiene "un atributo seguido de otra declaración se desliga" "sin coleccion: Suite.E2E.TrasUnRecordTests (5 s)" "$PLAN_1"
no_contiene "lo que hay en bin/ y obj/ no cuenta" "Basura" "$PLAN_1"

# Visita (Mediana) y VisitaDetalle (SinPeso) caen en bloques distintos: el
# punto final del filtro impide que el primero arrastre al segundo.
B_VISITA="$(grep -lx 'Suite.E2E.Visita' "$TMP"/bloque-*.txt)"
B_DETALLE="$(grep -lx 'Suite.E2E.VisitaDetalle' "$TMP"/bloque-*.txt)"
PRUEBAS=$((PRUEBAS + 1))
if [[ "$B_VISITA" == "$B_DETALLE" ]]; then
  FALLOS=$((FALLOS + 1)); echo "FALLO: el caso de prefijo no separa las dos clases; no prueba nada" >&2
fi
n="${B_VISITA##*bloque-}"; n="${n%.txt}"
eval "FILTRO_VISITA=\$FILTRO_$n"
contiene "el filtro lleva el punto final" "FullyQualifiedName~Suite.E2E.Visita." "$FILTRO_VISITA"
no_contiene "y no arrastra a la clase de la que es prefijo" "VisitaDetalle" "$FILTRO_VISITA"

echo "=== Casos que deben fallar ==="
correr 3 1 MINIMO_CLASES=10
igual "por debajo del suelo de clases sale con 1" 1 "$CODIGO"
contiene "y dice que el descubrimiento está averiado" "descubrimiento averiado" "$ERR"
igual "y no escribe filtro" "" "$SALIDA"
correr 3 1 MINIMO_CLASES=9
igual "en el suelo exacto sale con 0" 0 "$CODIGO"

# Bloque vacío: 2 unidades para 3 bloques.
printf '    Suite.E2E.AlfaTests.Uno\n    Suite.E2E.BetaTests.Uno\n' > "$TMP/corto.txt"
correr 3 3 LISTADO_DE_TESTS="$TMP/corto.txt"
igual "un bloque sin clases sale con 1" 1 "$CODIGO"
contiene "y lo dice" "sin ninguna clase" "$ERR"
igual "y no escribe filtro" "" "$SALIDA"

# Clase en dos colecciones.
G="$TMP/doble"; mkdir -p "$G"
cp "$F/Dos.cs" "$G/"
printf 'namespace Suite.E2E;\n[Collection("Otra")]\npublic partial class AlfaTests { }\n' > "$G/Otra.cs"
correr 2 1 FUENTES_E2E="$G" LISTADO_DE_TESTS="$TMP/corto.txt"
igual "una clase en dos colecciones sale con 1" 1 "$CODIGO"
contiene "y nombra la clase" "La clase Suite.E2E.AlfaTests aparece en dos colecciones" "$ERR"

: > "$TMP/vacio.txt"
correr 3 1 LISTADO_DE_TESTS="$TMP/vacio.txt"
igual "un listado sin clases sale con 1" 1 "$CODIGO"

echo "=== Argumentos ==="
for par in "0 1" "x 1" "3 0" "3 4" "3 x"; do
  # shellcheck disable=SC2086
  correr $par
  igual "argumentos '$par' salen con 2" 2 "$CODIGO"
done
correr 3 1 LISTADO_DE_TESTS="$TMP/no-existe.txt"
igual "listado inexistente sale con 2" 2 "$CODIGO"
correr 3 1 PESOS_E2E="$TMP/no-existe.txt"
igual "tabla de pesos inexistente sale con 2" 2 "$CODIGO"
printf 'Grande mucho\n' > "$TMP/pesos-malos.txt"
correr 3 1 PESOS_E2E="$TMP/pesos-malos.txt"
igual "peso no entero sale con 2" 2 "$CODIGO"

echo "=== Árbol real ==="
# El guion solo interpreta `[Collection("Nombre")]` en sus propios corchetes.
# Si aparece un atributo escrito de otra forma, el mapa lo perdería y esa
# clase se repartiría como suelta, lejos de su fixture: aquí se ve.
REAL="$RAIZ/tests/CaeManager.E2ETests"
ATRIBUTOS="$(grep -rE --include='*.cs' --exclude-dir=bin --exclude-dir=obj '\[Collection\(' "$REAL" | grep -vcE '^[^:]+:[[:space:]]*//' || true)"
MAPA_REAL="$(cd "$RAIZ" && bash "$SCRIPT" --mapa)"
PARES="$(grep -c . <<< "$MAPA_REAL" || true)"
igual "cada [Collection( del árbol real da un par clase-colección" "$ATRIBUTOS" "$PARES"
igual "ninguna clase real aparece dos veces en el mapa" "$PARES" "$(cut -f1 <<< "$MAPA_REAL" | sort -u | wc -l | tr -d ' ')"
PRUEBAS=$((PRUEBAS + 1))
if [[ "$PARES" -lt 70 ]]; then
  FALLOS=$((FALLOS + 1)); echo "FALLO: el mapa real tiene $PARES pares; se esperaban al menos 70" >&2
fi
SIN_PESO="$(comm -23 <(cut -f2 <<< "$MAPA_REAL" | sort -u) \
                     <(grep -vE '^[[:space:]]*(#|$)' "$RAIZ/scripts/e2e-pesos-por-coleccion.txt" | tr -d '\r' | cut -d' ' -f1 | sort -u) | paste -sd' ' -)"
igual "todas las colecciones reales tienen peso en la tabla" "" "$SIN_PESO"

echo
echo "Pruebas: $PRUEBAS · Fallos: $FALLOS"
[[ "$FALLOS" == 0 ]]
