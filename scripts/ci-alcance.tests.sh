#!/bin/bash
# Tests de scripts/ci-alcance.sh con un `gh` simulado.
#
# El `gh` falso se antepone al PATH en un directorio temporal y contesta por
# endpoint con el contenido de un fichero de fixture, ya en la forma final
# (post --jq) que produce el `gh` real, que es lo único que el guion consume:
#   runs   -> .../actions/workflows/ci.yml/runs?...   (seis campos separados por |)
#   labels -> .../issues/N/labels                      (un nombre por línea)
#   files  -> .../pulls/N/files                        (una ruta por línea)
# Un fichero cuya primera línea sea MOCK_ERROR simula que `gh` falla. Si falta
# el fichero, el `gh` falso también falla: una llamada no prevista nunca
# devuelve «vacío» por accidente.
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPT="$SCRIPT_DIR/ci-alcance.sh"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

mkdir -p "$TMP/bin"
cat > "$TMP/bin/gh" <<'MOCK'
#!/bin/bash
todo="$*"
echo "$todo" >> "$FIXTURE_DIR/llamadas.log"
case "$todo" in
  *"/actions/workflows/ci.yml/runs?"*) f="$FIXTURE_DIR/runs" ;;
  *"/issues/"*"/labels"*)              f="$FIXTURE_DIR/labels" ;;
  *"/pulls/"*"/files"*)                f="$FIXTURE_DIR/files" ;;
  *) echo "MOCK gh: llamada no prevista: $todo" >&2; exit 1 ;;
esac
[[ -f "$f" ]] || { echo "MOCK gh: sin fixture para $todo" >&2; exit 1; }
[[ "$(head -n1 "$f")" == "MOCK_ERROR" ]] && { echo "MOCK gh: fallo simulado" >&2; exit 1; }
cat "$f"
MOCK
chmod +x "$TMP/bin/gh"

REPO="Owner/repo-de-prueba"
SHA="1111111111111111111111111111111111111111"
OTRO="2222222222222222222222222222222222222222"
RUTA=".github/workflows/ci.yml"
PRUEBAS=0
FALLOS=0

nuevo() { FIXTURE_DIR="$(mktemp -d "$TMP/fx.XXXXXX")"; }
fx() { local n="$1"; shift; printf '%s\n' "$@" > "$FIXTURE_DIR/$n"; }
fx_vacia() { : > "$FIXTURE_DIR/$1"; }
run_tsv() { printf '%s|%s|%s|%s|%s|%s' "$@"; }

# correr EVENTO [PR]  -> CODIGO y RES="integracion e2e carga" leído de GITHUB_OUTPUT
correr() {
  : > "$FIXTURE_DIR/out"
  FIXTURE_DIR="$FIXTURE_DIR" PATH="$TMP/bin:$PATH" EVENTO="$1" PR_NUMERO="${2:-}" CI_ALCANCE_ESPERA_PUSH_S=0 \
    GITHUB_REPOSITORY="${REPO_ENV-$REPO}" GITHUB_SHA="${SHA_ENV-$SHA}" GITHUB_OUTPUT="$FIXTURE_DIR/out" \
    bash "$SCRIPT" > "$FIXTURE_DIR/stdout" 2> "$FIXTURE_DIR/stderr"
  CODIGO=$?
  local i e c
  i="$(sed -n 's/^integracion=//p' "$FIXTURE_DIR/out" | tail -1)"
  e="$(sed -n 's/^e2e=//p' "$FIXTURE_DIR/out" | tail -1)"
  c="$(sed -n 's/^carga=//p' "$FIXTURE_DIR/out" | tail -1)"
  RES="$i $e $c"
}

assert() {
  local desc="$1" esp="$2"
  PRUEBAS=$((PRUEBAS + 1))
  if [[ "$CODIGO" == 0 && "$RES" == "$esp" ]]; then
    echo "OK: $desc -> $esp"
  else
    FALLOS=$((FALLOS + 1))
    echo "FALLO: $desc — esperado '$esp', obtenido '$RES' (código $CODIGO)" >&2
    sed 's/^/  /' "$FIXTURE_DIR/stdout" "$FIXTURE_DIR/stderr" >&2
  fi
}

TODO="true true true"
NADA="false false false"

echo "=== merge_group y eventos no previstos: todo, sin llamar a gh ==="
nuevo; correr merge_group; assert "grupo de fusión" "$TODO"
PRUEBAS=$((PRUEBAS + 1))
if [[ -f "$FIXTURE_DIR/llamadas.log" ]]; then FALLOS=$((FALLOS + 1)); echo "FALLO: merge_group llamó a gh" >&2; else echo "OK: merge_group no consulta nada"; fi
nuevo; correr workflow_dispatch; assert "evento no previsto" "$TODO"
nuevo; correr ""; assert "evento vacío" "$TODO"

echo "=== push ==="
nuevo; fx runs "$(run_tsv "$SHA" merge_group success "$RUTA" "$REPO" "$REPO")"
correr push; assert "push con run del grupo en verde para el mismo SHA" "$NADA"
PRUEBAS=$((PRUEBAS + 1))
if grep -q "head_sha=$SHA" "$FIXTURE_DIR/llamadas.log" && grep -q "event=merge_group" "$FIXTURE_DIR/llamadas.log" && grep -q "status=success" "$FIXTURE_DIR/llamadas.log" && grep -q "/actions/workflows/ci.yml/runs" "$FIXTURE_DIR/llamadas.log"; then
  echo "OK: la consulta acota por workflow ci.yml, SHA, evento y estado"
else
  FALLOS=$((FALLOS + 1)); echo "FALLO: la consulta no acota por workflow, SHA, evento y estado" >&2
fi
PRUEBAS=$((PRUEBAS + 1))
if [[ "$(wc -l < "$FIXTURE_DIR/llamadas.log")" -eq 1 ]]; then echo "OK: con el run a la primera no se reintenta"; else FALLOS=$((FALLOS + 1)); echo "FALLO: se reintentó sin necesidad" >&2; fi

nuevo; fx_vacia runs
correr push; assert "push: la API no devuelve ningún run (push directo)" "$TODO"
PRUEBAS=$((PRUEBAS + 1))
if [[ "$(wc -l < "$FIXTURE_DIR/llamadas.log")" -eq 3 ]]; then echo "OK: sin run, pregunta tres veces antes de rendirse"; else FALLOS=$((FALLOS + 1)); echo "FALLO: no preguntó tres veces" >&2; fi
nuevo; fx runs MOCK_ERROR
correr push; assert "push: gh falla" "$TODO"
nuevo; fx runs "$(run_tsv "$OTRO" merge_group success "$RUTA" "$REPO" "$REPO")"
correr push; assert "push: run verde del grupo, pero de OTRO SHA" "$TODO"
nuevo; fx runs "$(run_tsv "$SHA" pull_request success "$RUTA" "$REPO" "$REPO")"
correr push; assert "push: run verde del mismo SHA, pero de pull_request" "$TODO"
nuevo; fx runs "$(run_tsv "$SHA" push success "$RUTA" "$REPO" "$REPO")"
correr push; assert "push: run verde del mismo SHA, pero de push" "$TODO"
nuevo; fx runs "$(run_tsv "$SHA" merge_group failure "$RUTA" "$REPO" "$REPO")"
correr push; assert "push: run del grupo en rojo" "$TODO"
nuevo; fx runs "$(run_tsv "$SHA" merge_group cancelled "$RUTA" "$REPO" "$REPO")"
correr push; assert "push: run del grupo cancelado" "$TODO"
nuevo; fx runs "$(run_tsv "$SHA" merge_group "" "$RUTA" "$REPO" "$REPO")"
correr push; assert "push: run del grupo sin conclusión (en curso)" "$TODO"
nuevo; fx runs "$(run_tsv "$SHA" merge_group success ".github/workflows/otro.yml" "$REPO" "$REPO")"
correr push; assert "push: run verde de OTRO workflow" "$TODO"
nuevo; fx runs "$(run_tsv "$SHA" merge_group success "$RUTA" "Otro/repo" "$REPO")"
correr push; assert "push: run de otro repositorio" "$TODO"
nuevo; fx runs "$(run_tsv "$SHA" merge_group success "$RUTA" "$REPO" "Fork/repo-de-prueba")"
correr push; assert "push: run cuya cabeza viene de un fork" "$TODO"
nuevo; fx runs "$(run_tsv "$SHA" merge_group success "$RUTA" "$REPO" "")"
correr push; assert "push: run sin repositorio de cabeza" "$TODO"
nuevo; fx runs "$(run_tsv "$SHA" merge_group success "" "$REPO" "$REPO")"
correr push; assert "push: run sin ruta de workflow" "$TODO"
nuevo; fx runs "basura sin separadores"
correr push; assert "push: respuesta con forma inesperada" "$TODO"
nuevo; fx runs "$(run_tsv "$OTRO" merge_group success "$RUTA" "$REPO" "$REPO")" "$(run_tsv "$SHA" merge_group success "$RUTA" "$REPO" "$REPO")"
correr push; assert "push: varios runs, el segundo es el bueno" "$NADA"
nuevo; fx runs "$(run_tsv "$SHA" merge_group success "$RUTA" "$REPO" "$REPO")"
SHA_ENV="" correr push; assert "push: sin SHA en el entorno" "$TODO"
nuevo; fx runs "$(run_tsv "$SHA" merge_group success "$RUTA" "$REPO" "$REPO")"
REPO_ENV="" correr push; assert "push: sin repositorio en el entorno" "$TODO"

echo "=== pull_request ==="
pr() { nuevo; fx labels "${ETIQUETAS[@]}"; fx files "$@"; correr pull_request 77; }
ETIQUETAS=("Type: Enhancement" "Priority: Medium")

pr "src/CaeManager.Web/Features/Clientes/Pages/Clientes.razor" "tests/CaeManager.Web.Tests/ClientesTests.cs"
assert "PR de pantalla (Web y bUnit): ligera" "$NADA"
pr "src/CaeManager.Application/Trabajadores/CrearTrabajadorCommand.cs" "src/CaeManager.Domain/Trabajador.cs"
assert "PR de Application y Domain sin nombres sensibles: ligera" "$NADA"
pr "tests/CaeManager.IntegrationTests/AlgoTests.cs"
assert "toca tests de integración" "true false false"
pr "src/CaeManager.Infrastructure/Persistence/AppDbContext.cs"
assert "toca Infrastructure" "true false false"
pr "src/CaeManager.Migrations.PostgreSQL/Migrations/20261009_X.cs"
assert "toca migraciones" "true false false"
pr "scripts/turno-postgres.sh"
assert "toca scripts/" "true false false"
pr "src/CaeManager.Application/Seguridad/AutorizacionDeCartera.cs"
assert "nombre con Autorizacion" "true false false"
pr "src/CaeManager.Application/Common/IAuthorizationService.cs"
assert "nombre con Authoriz" "true false false"
pr "src/CaeManager.Application/Carteras/AlcanceDeCartera.cs"
assert "nombre con Alcance" "true false false"
pr "src/CaeManager.Web/Components/PermisosPanel.razor"
assert "nombre con Permis" "true false false"
pr "src/CaeManager.Domain/Tenants/TenantId.cs"
assert "ruta con Tenant" "true false false"
pr "tests/CaeManager.Application.Tests/PoliticaRlsTests.cs"
assert "nombre con Rls" "true false false"
pr "tests/CaeManager.E2ETests/ClientesE2ETests.cs"
assert "toca tests E2E" "false true false"
pr "tests/CaeManager.E2ETests/X.cs" "tests/CaeManager.IntegrationTests/Y.cs"
assert "toca tests E2E y de integración" "true true false"
pr ".github/workflows/ci.yml"
assert "toca un workflow" "$TODO"
pr "deploy/local/docker-compose.produccion.yml"
assert "toca deploy/" "$TODO"
pr "Dockerfile"
assert "toca el Dockerfile de la raíz" "$TODO"
pr "src/CaeManager.Web/Dockerfile.dev"
assert "toca un Dockerfile en otra ruta" "$TODO"
pr "src/CaeManager.Web/CaeManager.Web.csproj"
assert "toca un .csproj (dependencias)" "$TODO"
pr "src/CaeManager.Web/notas.csproj.bak"
assert "un .csproj.bak no es un .csproj" "$NADA"
pr "Directory.Build.props"
assert "toca Directory.Build.props" "$TODO"
pr "tests/CaeManager.Web.Tests/packages.lock.json"
assert "toca un packages.lock.json" "$TODO"
pr "global.json"
assert "toca global.json" "$TODO"
pr ".config/dotnet-tools.json"
assert "toca .config/" "$TODO"
pr "docker-entrypoint.sh"
assert "toca docker-entrypoint.sh" "$TODO"
nuevo; fx labels "${ETIQUETAS[@]}"; for i in $(seq 1 3000); do echo "src/CaeManager.Web/f$i.razor"; done > "$FIXTURE_DIR/files"; correr pull_request 77
assert "3000 ficheros: la API trunca la lista" "$TODO"
nuevo; fx labels "${ETIQUETAS[@]}"; for i in $(seq 1 2999); do echo "src/CaeManager.Web/f$i.razor"; done > "$FIXTURE_DIR/files"; correr pull_request 77
assert "2999 ficheros de pantalla: ligera" "$NADA"
pr "docs/Dockerfile/notas.txt"
assert "un directorio llamado Dockerfile no es un Dockerfile" "$NADA"
pr "src/CaeManager.Web/Pages/A.razor" "src/CaeManager.Infrastructure/B.cs"
assert "la ruta sensible va la segunda" "true false false"
nuevo; fx labels "${ETIQUETAS[@]}"; printf 'src/CaeManager.Web/A.razor\r\ntests/CaeManager.E2ETests/B.cs\r\n' > "$FIXTURE_DIR/files"; correr pull_request 77
assert "lista con finales de línea CRLF" "false true false"

ETIQUETAS=("Type: Enhancement" "CI: completo")
pr "src/CaeManager.Web/Features/Clientes/Pages/Clientes.razor"
assert "PR ligera con etiqueta «CI: completo»" "$TODO"
ETIQUETAS=("CI: completo y algo más")
pr "src/CaeManager.Web/Features/Clientes/Pages/Clientes.razor"
assert "una etiqueta que solo empieza igual no cuenta" "$NADA"
ETIQUETAS=("Type: Enhancement")

nuevo; fx labels MOCK_ERROR; fx files "src/CaeManager.Web/A.razor"; correr pull_request 77
assert "PR: no se pueden leer las etiquetas" "$TODO"
nuevo; fx labels "${ETIQUETAS[@]}"; fx files MOCK_ERROR; correr pull_request 77
assert "PR: no se puede leer la lista de ficheros" "$TODO"
nuevo; fx labels "${ETIQUETAS[@]}"; fx_vacia files; correr pull_request 77
assert "PR: lista de ficheros vacía" "$TODO"
nuevo; fx_vacia labels; fx files "src/CaeManager.Web/A.razor"; correr pull_request 77
assert "PR sin etiquetas (respuesta vacía legítima): ligera" "$NADA"
nuevo; fx labels "${ETIQUETAS[@]}"; fx files "src/CaeManager.Web/A.razor"; correr pull_request ""
assert "PR sin número" "$TODO"
nuevo; fx labels "${ETIQUETAS[@]}"; fx files "src/CaeManager.Web/A.razor"; correr pull_request "77; rm -rf x"
assert "número de PR que no es un número" "$TODO"

echo
echo "Pruebas: $PRUEBAS · Fallos: $FALLOS"
(( FALLOS == 0 )) || exit 1
