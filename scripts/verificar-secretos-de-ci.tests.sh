#!/bin/bash
# Prueba de verificar-secretos-de-ci.sh sobre workflows sintéticos.
#
# La propiedad que importa no es "el guion se ejecuta": es que CAZA el día que
# alguien enchufe un secreto restringido a un disparador que un PR puede
# controlar, lo lea desde un job sin el environment que lo protege, lo alcance
# de forma indirecta, o pegue una clave de producción; y que no se calla cuando
# no ha podido leer nada. Cada caso "rojo" es una mutación del caso "verde"
# contiguo que cambia UNA cosa, y comprueba el MOTIVO del rojo, no solo el color.
#
# La clave con forma de producción se arma por concatenación para que este
# fichero no contenga en claro nada que el propio guion marcaría.
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
VERIFICADOR=(bash "$SCRIPT_DIR/verificar-secretos-de-ci.sh")

TMP_ROOT="$(mktemp -d)"
trap 'rm -rf "$TMP_ROOT"' EXIT

FALLOS=0
PRUEBAS=0

# esperar <nombre> <salida-esperada 0|1> <fragmento-que-debe-salir|""> <dir>
esperar() {
  local nombre="$1" esperado="$2" fragmento="$3" dir="$4" salida codigo
  PRUEBAS=$((PRUEBAS + 1))
  salida="$("${VERIFICADOR[@]}" "$dir" 2>&1)"
  codigo=$?
  if [ "$codigo" -ne "$esperado" ]; then
    echo "FALLO [$nombre]: salida $codigo, esperada $esperado" >&2
    echo "$salida" >&2
    FALLOS=$((FALLOS + 1))
    return
  fi
  if [ -n "$fragmento" ] && ! grep -qF -- "$fragmento" <<<"$salida"; then
    echo "FALLO [$nombre]: rojo por un motivo distinto del esperado ('$fragmento'):" >&2
    echo "$salida" >&2
    FALLOS=$((FALLOS + 1))
    return
  fi
  echo "OK   [$nombre]"
}

nuevo_dir() {
  local d="$TMP_ROOT/$1"
  mkdir -p "$d"
  echo "$d"
}

# Verde de referencia: push + workflow_dispatch, un job con el environment y el secreto.
verde_de_referencia() {
  cat <<'YML'
name: A
on:
  # comentario con pull_request que no cuenta
  push:
    branches: [main]
  workflow_dispatch:
jobs:
  j:
    runs-on: ubuntu-latest
    environment: ci-integraciones
    steps:
      - run: echo
        env:
          K: ${{ secrets.CI_ANTHROPIC_API_KEY }}
YML
}

d="$(nuevo_dir verde)"; verde_de_referencia >"$d/a.yml"
esperar "verde: push + workflow_dispatch + environment" 0 "OK:" "$d"

# --- Disparadores ---------------------------------------------------------------------
d="$(nuevo_dir rojo-pr-bloque)"
verde_de_referencia | sed 's/^  workflow_dispatch:$/  pull_request:/' >"$d/a.yml"
esperar "rojo: bloque con pull_request" 1 "'pull_request'" "$d"

d="$(nuevo_dir rojo-linea-corchetes)"
cat >"$d/a.yml" <<'YML'
name: A
on: [merge_group, pull_request_target]
jobs:
  j:
    runs-on: ubuntu-latest
    environment: ci-integraciones
    steps:
      - run: echo ${{ secrets.CI_STRIPE_TEST_API_KEY }}
YML
esperar "rojo: on en línea con corchetes" 1 "'pull_request_target'" "$d"

d="$(nuevo_dir verde-una-palabra)"
cat >"$d/a.yml" <<'YML'
name: A
on: push
jobs:
  j:
    runs-on: ubuntu-latest
    environment: ci-integraciones
    steps:
      - run: echo ${{ secrets.CI_ANTHROPIC_API_KEY }}
YML
esperar "verde: on: push con environment" 0 "OK:" "$d"

d="$(nuevo_dir rojo-una-palabra-pr)"
sed 's/^on: push$/on: pull_request/' "$TMP_ROOT/verde-una-palabra/a.yml" >"$d/a.yml"
esperar "rojo: on: pull_request (mutación de una palabra)" 1 "'pull_request'" "$d"

d="$(nuevo_dir rojo-workflow-call)"
cat >"$d/a.yml" <<'YML'
name: A
on:
  workflow_call:
jobs:
  j:
    runs-on: ubuntu-latest
    environment: ci-integraciones
    steps:
      - run: echo ${{ secrets.CI_ANTHROPIC_API_KEY }}
YML
esperar "rojo: workflow_call hereda el disparador del llamante" 1 "'workflow_call'" "$d"

d="$(nuevo_dir rojo-sin-on)"
cat >"$d/a.yml" <<'YML'
name: A
jobs:
  j:
    runs-on: ubuntu-latest
    environment: ci-integraciones
    steps:
      - run: echo ${{ secrets.CI_ANTHROPIC_API_KEY }}
YML
esperar "rojo: sin bloque on legible" 1 "no se pudo leer ningún disparador" "$d"

# --- Environment por job --------------------------------------------------------------
d="$(nuevo_dir rojo-sin-environment)"
verde_de_referencia | grep -v 'environment: ci-integraciones' >"$d/a.yml"
esperar "rojo: job con el secreto sin environment" 1 "el job 'j' cita un secreto restringido sin declarar" "$d"

d="$(nuevo_dir rojo-otro-environment)"
verde_de_referencia | sed 's/ci-integraciones/produccion/' >"$d/a.yml"
esperar "rojo: environment de otro nombre" 1 "el job 'j' cita un secreto restringido sin declarar" "$d"

d="$(nuevo_dir rojo-un-job-sin-environment)"
cat >"$d/a.yml" <<'YML'
name: A
on:
  push:
jobs:
  bueno:
    runs-on: ubuntu-latest
    environment: ci-integraciones
    steps:
      - run: echo ${{ secrets.CI_ANTHROPIC_API_KEY }}
  malo:
    runs-on: ubuntu-latest
    steps:
      - run: echo ${{ secrets.CI_STRIPE_TEST_API_KEY }}
YML
esperar "rojo: solo el segundo job carece de environment (y se nombra ese)" 1 "el job 'malo'" "$d"
salida="$("${VERIFICADOR[@]}" "$d" 2>&1)"; PRUEBAS=$((PRUEBAS + 1))
if grep -qF "el job 'bueno'" <<<"$salida"; then
  echo "FALLO [rojo: un job}: se acusó también al job que SÍ declara el environment" >&2; FALLOS=$((FALLOS + 1))
else
  echo "OK   [el job con environment no se acusa]"
fi

d="$(nuevo_dir rojo-job-entrecomillado)"
cat >"$d/a.yml" <<'YML'
name: A
on:
  push:
jobs:
  "stripe":
    runs-on: ubuntu-latest
    steps:
      - run: echo ${{ secrets.CI_STRIPE_TEST_API_KEY }}
  'ia':
    runs-on: ubuntu-latest
    steps:
      - run: echo ${{ secrets.CI_ANTHROPIC_API_KEY }}
YML
esperar "rojo: job con clave entre comillas dobles sin environment" 1 "el job 'stripe'" "$d"
esperar "rojo: job con clave entre comillas simples sin environment" 1 "el job 'ia'" "$d"

d="$(nuevo_dir verde-job-entrecomillado)"
cat >"$d/a.yml" <<'YML'
name: A
on:
  push:
jobs:
  "stripe":
    runs-on: ubuntu-latest
    environment: ci-integraciones
    steps:
      - run: echo ${{ secrets.CI_STRIPE_TEST_API_KEY }}
YML
esperar "verde: job entrecomillado que SÍ declara el environment" 0 "OK:" "$d"

d="$(nuevo_dir rojo-job-no-reconocido)"
cat >"$d/a.yml" <<'YML'
name: A
on:
  push:
jobs:
  ? stripe
  : runs-on: ubuntu-latest
    steps:
      - run: echo ${{ secrets.CI_STRIPE_TEST_API_KEY }}
YML
esperar "rojo: secreto sin ningún job reconocible (fallo cerrado)" 1 "<job-no-reconocido>" "$d"

d="$(nuevo_dir verde-environment-con-comentario)"
verde_de_referencia | sed 's/environment: ci-integraciones/environment: ci-integraciones  # solo main/' >"$d/a.yml"
esperar "verde: environment con comentario al final" 0 "OK:" "$d"

# --- Acceso indirecto a secretos ------------------------------------------------------
for caso in \
  "indexado:\${{ secrets[format('CI_{0}_API_KEY', 'ANTHROPIC')] }}" \
  "toJSON:\${{ toJSON(secrets) }}" \
  "join:\${{ join(secrets, ',') }}" \
  "expansion:\${{ secrets }}"; do
  nombre_caso="${caso%%:*}"; expr="${caso#*:}"
  d="$(nuevo_dir "rojo-dinamico-$nombre_caso")"
  cat >"$d/a.yml" <<YML
name: A
on:
  pull_request:
jobs:
  j:
    runs-on: ubuntu-latest
    steps:
      - run: echo "$expr"
YML
  esperar "rojo: pull_request + acceso dinámico ($nombre_caso)" 1 "accede a los secretos de forma dinámica" "$d"
done

d="$(nuevo_dir rojo-inherit)"
cat >"$d/a.yml" <<'YML'
name: A
on:
  pull_request:
jobs:
  j:
    uses: ./.github/workflows/otro.yml
    secrets: inherit
YML
esperar "rojo: pull_request + secrets: inherit" 1 "accede a los secretos de forma dinámica" "$d"

d="$(nuevo_dir verde-github-token-en-pr)"
cat >"$d/a.yml" <<'YML'
name: A
on:
  pull_request_target:
jobs:
  j:
    runs-on: ubuntu-latest
    steps:
      - run: echo ${{ secrets.GITHUB_TOKEN }}
YML
esperar "verde: pull_request_target con secrets.GITHUB_TOKEN literal" 0 "OK:" "$d"

d="$(nuevo_dir verde-dinamico-en-disparador-de-confianza)"
cat >"$d/a.yml" <<'YML'
name: A
on:
  push:
jobs:
  j:
    runs-on: ubuntu-latest
    steps:
      - run: echo ${{ toJSON(secrets) }}
YML
esperar "verde: acceso dinámico solo con disparadores de confianza (no es el riesgo que se vigila)" 0 "OK:" "$d"

# --- Falsos positivos que NO deben saltar ---------------------------------------------
d="$(nuevo_dir verde-pr-sin-secreto)"
cat >"$d/a.yml" <<'YML'
name: A
on:
  pull_request:
jobs:
  j:
    runs-on: ubuntu-latest
    steps:
      - run: echo ${{ secrets.OTRO_SECRETO }}
YML
esperar "verde: pull_request sin secreto restringido" 0 "OK:" "$d"

d="$(nuevo_dir verde-comentario)"
cat >"$d/a.yml" <<'YML'
name: A
on:
  pull_request:
# secrets.CI_ANTHROPIC_API_KEY solo se cita aquí, no se lee.
jobs:
  j:
    runs-on: ubuntu-latest
    steps:
      - run: echo hola
YML
esperar "verde: secreto solo en comentario" 0 "OK:" "$d"

# --- Claves de producción de Stripe ---------------------------------------------------
for prefijo in "sk_""live_" "rk_""live_"; do
  d="$(nuevo_dir "rojo-$prefijo")"
  cat >"$d/a.yml" <<YML
name: A
on:
  push:
jobs:
  j:
    runs-on: ubuntu-latest
    steps:
      - run: echo ${prefijo}abcdefghijklmnop1234
YML
  esperar "rojo: clave con forma real (${prefijo:0:2}…)" 1 "clave de producción de Stripe" "$d"
done

d="$(nuevo_dir verde-prefijo-nombrado)"
cat >"$d/a.yml" <<'YML'
name: A
on:
  push:
jobs:
  j:
    runs-on: ubuntu-latest
    steps:
      - run: echo "rechazada: empieza por sk_live_ o rk_live_"
YML
esperar "verde: prefijo citado sin cuerpo" 0 "OK:" "$d"

# --- Instrumento ciego ----------------------------------------------------------------
esperar "rojo: directorio inexistente" 1 "no existe el directorio" "$TMP_ROOT/no-existe"
d="$(nuevo_dir vacio)"
esperar "rojo: directorio sin workflows" 1 "no contiene ningún workflow" "$d"

# --- Contra los workflows REALES del repositorio --------------------------------------
RAIZ="$(cd "$SCRIPT_DIR/.." && pwd)"
esperar "workflows reales del repositorio" 0 "OK:" "$RAIZ/.github/workflows"

# Control positivo del instrumento sobre el repositorio real: el workflow real
# que usa los secretos DEBE estar en el radar del guion (si el guion no lo viera,
# el verde de arriba no significaría nada).
PRUEBAS=$((PRUEBAS + 1))
if [ -f "$RAIZ/.github/workflows/integraciones-con-clave.yml" ] \
   && grep -v '^[[:space:]]*#' "$RAIZ/.github/workflows/integraciones-con-clave.yml" | grep -Eq 'CI_ANTHROPIC_API_KEY' \
   && ! (source <(sed -n '/^jobs_con_secreto_sin_entorno()/,/^}/p' "$SCRIPT_DIR/verificar-secretos-de-ci.sh"); \
         SECRETOS_RESTRINGIDOS='CI_ANTHROPIC_API_KEY|CI_STRIPE_TEST_API_KEY' ENTORNO_EXIGIDO='ci-integraciones' \
         jobs_con_secreto_sin_entorno "$RAIZ/.github/workflows/integraciones-con-clave.yml" | grep -q .); then
  echo "OK   [control positivo: el workflow real cita el secreto y todos sus jobs declaran el environment]"
else
  echo "FALLO [control positivo]: el workflow real no cita el secreto o algún job carece de environment" >&2
  FALLOS=$((FALLOS + 1))
fi

echo ""
echo "$((PRUEBAS - FALLOS))/$PRUEBAS pruebas correctas"
[ "$FALLOS" -eq 0 ]
