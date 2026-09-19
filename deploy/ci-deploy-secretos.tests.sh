#!/bin/bash
# Prueba de CLAVES_PERMITIDAS_SECRETOS_PRODUCCION y
# actualizar_secretos_produccion, ambas definidas y ejecutadas TAL CUAL en
# ci-deploy.sh — sin copiar su lógica (hallazgo de Codex sobre la primera
# versión de esta prueba: una copia mantenida a mano podía desincronizarse
# en silencio y quedar en verde mientras el VPS corría otra cosa).
#
# `source` funciona sin disparar un despliegue real gracias a la guarda
# `BASH_SOURCE[0] = $0` al final de ci-deploy.sh, y
# FICHERO_ENV_SECRETOS_PRODUCCION (ver ese fichero) redirige la función a un
# directorio temporal en vez de /opt/talveg/deploy/local/.env — las dos
# existen solo para que este test pueda ejercitar la función real de forma
# aislada, y ninguna de las dos cambia el comportamiento en el VPS (ahí
# nunca se define esa variable).
#
# No sustituye probar contra un VPS real: resolve-deploy-sha.sh y
# liberar-disco.sh siguen sin arnés por el mismo motivo — el propio comando
# forzado de SSH no se puede ejercitar entero fuera de una sesión real.
set -euo pipefail

DIR_GUION="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$DIR_GUION/ci-deploy.sh"

DIR="$(mktemp -d)"
trap 'rm -rf "$DIR"' EXIT
export FICHERO_ENV_SECRETOS_PRODUCCION="$DIR/.env"

cat > "$FICHERO_ENV_SECRETOS_PRODUCCION" <<'ENVEOF'
DOMINIO=app.talveg.es
POSTGRES_PASSWORD=viejo123
AdministradorInicial__Email=admin@talveg.es
AdministradorInicial__Contrasena=
DatosPrueba__Activo=true
ENVEOF

leer_env() { cat "$FICHERO_ENV_SECRETOS_PRODUCCION"; }

echo "=== Caso 1: stdin vacío (no-op) ==="
ANTES="$(leer_env)"
actualizar_secretos_produccion < /dev/null
DESPUES="$(leer_env)"
if [ "$ANTES" != "$DESPUES" ]; then
  echo "FALLO: .env cambió con stdin vacío" >&2
  exit 1
fi
echo "OK: .env sin cambios"

echo "=== Caso 2: upsert (clave existente + clave nueva + valor con '=' dentro) ==="
printf 'AdministradorInicial__Contrasena=nuevo456\nIntegraciones__Microsoft365__ClientSecret=Host=x;Password=a=b=c\nAnthropic__ApiKey=sk-test-123\n' \
  | actualizar_secretos_produccion
leer_env

grep -qx "AdministradorInicial__Contrasena='nuevo456'" "$FICHERO_ENV_SECRETOS_PRODUCCION" || { echo "FALLO: no sustituyó una clave existente" >&2; exit 1; }
grep -qx "Integraciones__Microsoft365__ClientSecret='Host=x;Password=a=b=c'" "$FICHERO_ENV_SECRETOS_PRODUCCION" || { echo "FALLO: valor con '=' truncado" >&2; exit 1; }
grep -qx "Anthropic__ApiKey='sk-test-123'" "$FICHERO_ENV_SECRETOS_PRODUCCION" || { echo "FALLO: no añadió clave nueva" >&2; exit 1; }
grep -qx 'DOMINIO=app.talveg.es' "$FICHERO_ENV_SECRETOS_PRODUCCION" || { echo "FALLO: perdió una clave no tocada" >&2; exit 1; }
grep -qx 'POSTGRES_PASSWORD=viejo123' "$FICHERO_ENV_SECRETOS_PRODUCCION" || { echo "FALLO: perdió otra clave no tocada" >&2; exit 1; }
[ "$(wc -l < "$FICHERO_ENV_SECRETOS_PRODUCCION")" -eq 7 ] || { echo "FALLO: número de líneas inesperado ($(wc -l < "$FICHERO_ENV_SECRETOS_PRODUCCION"))" >&2; exit 1; }
echo "OK: upsert correcto, entrecomillado, resto de .env intacto"

echo "=== Caso 3: construcción del blob tal y como lo hace deploy.yml (regresión) ==="
# Pin del bug real que se detectó al escribir esto: un salto de línea LITERAL
# dentro de una cadena bash escrita en un bloque YAML `run: |` arrastra la
# indentación de la línea siguiente como espacios delante de la próxima
# clave, y esos espacios le rompen a awk el patrón
# `^[A-Za-z_][A-Za-z0-9_]*=` — la clave deja de reconocerse. El fix es
# `$'\n'` explícito (ver .github/workflows/deploy.yml, paso "Actualizar
# secretos de producción"); esta prueba fija ESE formato exacto de
# construcción, no solo el resultado final.
construir_blob_como_deploy_yml() {
    # Recibe pares NOMBRE VALOR como argumentos — misma agregar() que
    # .github/workflows/deploy.yml, incluida la guarda de salto de línea
    # (hallazgo revisado tras la fusión de REC-014/P37, PR #707: la primera
    # versión de esta prueba no la replicaba y no la habría detectado si
    # alguien la quitaba del guion real por error).
    local blob=""
    agregar() {
        local nombre="$1" valor="$2"
        if [ -n "$valor" ]; then
            case "$valor" in
                *$'\n'*)
                    echo "::error::el secreto $nombre trae un salto de línea — este mecanismo solo admite valores de una sola línea (formato KEY=VALOR en .env)" >&2
                    exit 1
                    ;;
            esac
            blob="${blob}${nombre}=${valor}"$'\n'
        fi
    }
    while [ "$#" -ge 2 ]; do
        agregar "$1" "$2"
        shift 2
    done
    printf '%s' "$blob"
}

export FICHERO_ENV_SECRETOS_PRODUCCION="$DIR/.env-caso3"
cat > "$FICHERO_ENV_SECRETOS_PRODUCCION" <<'ENVEOF'
DOMINIO=app.talveg.es
Anthropic__ApiKey=viejo123
ENVEOF
construir_blob_como_deploy_yml "Serilog__Seq__ApiKey" "seq-key-xyz" "Anthropic__ApiKey" "otro789" "Smtp__Contrasena" "" | actualizar_secretos_produccion
grep -qx "Anthropic__ApiKey='otro789'" "$FICHERO_ENV_SECRETOS_PRODUCCION" || { echo "FALLO: regresión del salto de línea literal — Anthropic__ApiKey no se actualizó" >&2; exit 1; }
grep -qx "Serilog__Seq__ApiKey='seq-key-xyz'" "$FICHERO_ENV_SECRETOS_PRODUCCION" || { echo "FALLO: regresión del salto de línea literal — clave nueva no se añadió limpia" >&2; exit 1; }
! grep -q '^ ' "$FICHERO_ENV_SECRETOS_PRODUCCION" || { echo "FALLO: hay líneas con espacio inicial en .env (indentación arrastrada)" >&2; exit 1; }
echo "OK: construcción del blob de deploy.yml no arrastra indentación"

echo "=== Caso 4: clave fuera de la lista blanca — rechazo total (hallazgo de Codex) ==="
# Escenario del hallazgo: si la clave SSH se filtrara, sin esta lista blanca
# alguien podría mandar Rls__PermitirIdentidadAdministrativaInsegura=true (o
# vaciar ConnectionStrings__CaeManagerDbRuntime) y, en el siguiente redeploy
# de un commit ya legítimo, dejar a producción sirviendo tráfico sin RLS.
# Esta prueba comprueba que el intento se rechaza ENTERO, .env no se toca ni
# siquiera para las claves buenas que venían en el mismo envío.
export FICHERO_ENV_SECRETOS_PRODUCCION="$DIR/.env-caso4"
cat > "$FICHERO_ENV_SECRETOS_PRODUCCION" <<'ENVEOF'
DOMINIO=app.talveg.es
ENVEOF
ANTES4="$(leer_env)"
if printf 'Anthropic__ApiKey=bueno\nRls__PermitirIdentidadAdministrativaInsegura=true\n' | actualizar_secretos_produccion 2>/tmp/caso4-stderr.txt; then
  echo "FALLO: aceptó una clave fuera de la lista blanca" >&2
  exit 1
fi
grep -q "clave no permitida" /tmp/caso4-stderr.txt || { echo "FALLO: no avisó por qué rechazó" >&2; cat /tmp/caso4-stderr.txt >&2; exit 1; }
DESPUES4="$(leer_env)"
if [ "$ANTES4" != "$DESPUES4" ]; then
  echo "FALLO: .env cambió pese al rechazo (incluso la clave 'buena' del mismo envío se aplicó)" >&2
  exit 1
fi
rm -f /tmp/caso4-stderr.txt
echo "OK: rechazo total, .env intacto, aviso claro"

echo "=== Caso 5: POSTGRES_PASSWORD y CaeManagerDbRuntime siguen fuera de la lista (hallazgo de Codex) ==="
for clave_prohibida in POSTGRES_PASSWORD ConnectionStrings__CaeManagerDbRuntime; do
  if printf '%s=loquesea\n' "$clave_prohibida" | actualizar_secretos_produccion 2>/dev/null; then
    echo "FALLO: $clave_prohibida ya no está excluida — revisa si la exclusión sigue siendo intencional" >&2
    exit 1
  fi
done
echo "OK: las dos claves de PostgreSQL siguen fuera de la lista blanca"

echo "=== Caso 6: valor con un carácter no admitido se rechaza ENTERO (hallazgos sucesivos de Codex) ==="
# Historia de este caso: la primera versión escapaba '$' entrecomillando con
# comillas simples; la segunda escapaba también la comilla simple y la barra
# invertida (\ y \'); Codex encontró que duplicar \ altera un valor real, y
# después que un valor que TERMINA en \ hace que la comilla de cierre quede
# leída como "comilla escapada" y deje el resto de .env sin parsear —
# docs.docker.com/reference/compose-file/services/#env_file-format no
# documenta ninguna forma no ambigua de representar eso con comillas
# simples. La solución final no persigue más casos de escape: rechaza
# ENTERO cualquier valor con ', ", `, \ o $ — ninguno de los secretos
# reales de la lista blanca los necesita.
export FICHERO_ENV_SECRETOS_PRODUCCION="$DIR/.env-caso6"
cat > "$FICHERO_ENV_SECRETOS_PRODUCCION" <<'ENVEOF'
DOMINIO=app.talveg.es
ENVEOF
for valor_prohibido in 'abc$HOME' "con'comilla" 'con"comilla' 'con`acento' 'termina\' 'en\medio' ; do
  ANTES6="$(leer_env)"
  if printf 'Smtp__Contrasena=%s\n' "$valor_prohibido" | actualizar_secretos_produccion 2>/tmp/caso6-stderr.txt; then
    echo "FALLO: aceptó un valor con carácter no admitido: '$valor_prohibido'" >&2
    exit 1
  fi
  grep -q "carácter no admitido" /tmp/caso6-stderr.txt || { echo "FALLO: no avisó por qué rechazó '$valor_prohibido'" >&2; cat /tmp/caso6-stderr.txt >&2; exit 1; }
  DESPUES6="$(leer_env)"
  [ "$ANTES6" = "$DESPUES6" ] || { echo "FALLO: .env cambió pese al rechazo de '$valor_prohibido'" >&2; exit 1; }
done
rm -f /tmp/caso6-stderr.txt
echo "OK: los seis valores con carácter no admitido se rechazaron enteros, .env intacto"

echo "=== Caso 7: valor con caracteres seguros variados sí se acepta ==="
# Contraparte del caso 6: el rechazo es del CARÁCTER concreto, no de
# cualquier símbolo — un valor con puntuación habitual en API keys/tokens
# (":", "/", "+", "=", "_", "-", ".", "@", espacio) se acepta y se
# entrecomilla tal cual, sin alterarlo.
export FICHERO_ENV_SECRETOS_PRODUCCION="$DIR/.env-caso7"
: > "$FICHERO_ENV_SECRETOS_PRODUCCION"
printf 'Anthropic__ApiKey=sk-ant_api03.AB+cd/EF=: [email protected] con espacio\n' | actualizar_secretos_produccion
grep -qxF "Anthropic__ApiKey='sk-ant_api03.AB+cd/EF=: [email protected] con espacio'" "$FICHERO_ENV_SECRETOS_PRODUCCION" \
  || { echo "FALLO: un valor con caracteres seguros no se aceptó tal cual" >&2; cat "$FICHERO_ENV_SECRETOS_PRODUCCION" >&2; exit 1; }
echo "OK: valor con puntuación segura aceptado y entrecomillado sin alterar"

echo "=== Caso 8: secreto vacío en GitHub conserva el valor YA EXISTENTE en .env ==="
# Hueco señalado en la revisión posterior a la fusión de REC-014/P37 (#707):
# el caso 2 solo prueba "todo vacío = no-op total"; ninguno probaba el caso
# real de un despliegue con secretos MIXTOS — algunos rotados, otros que el
# propietario aún no cargó en el environment de GitHub. agregar() en
# deploy.yml omite del blob cualquier valor vacío (secreto sin definir),
# así que esa clave nunca llega a actualizar_secretos_produccion y su línea
# en .env debe quedar EXACTAMENTE como estaba, sin tocar ni entrecomillar.
export FICHERO_ENV_SECRETOS_PRODUCCION="$DIR/.env-caso8"
cat > "$FICHERO_ENV_SECRETOS_PRODUCCION" <<'ENVEOF'
DOMINIO=app.talveg.es
Anthropic__ApiKey=valor-preexistente-sin-comillas
ENVEOF
construir_blob_como_deploy_yml "Anthropic__ApiKey" "" "Serilog__Seq__ApiKey" "nuevo-valor" | actualizar_secretos_produccion
grep -qx 'Anthropic__ApiKey=valor-preexistente-sin-comillas' "$FICHERO_ENV_SECRETOS_PRODUCCION" \
  || { echo "FALLO: un secreto vacío no conservó el valor ya existente en .env" >&2; cat "$FICHERO_ENV_SECRETOS_PRODUCCION" >&2; exit 1; }
grep -qx "Serilog__Seq__ApiKey='nuevo-valor'" "$FICHERO_ENV_SECRETOS_PRODUCCION" \
  || { echo "FALLO: la clave con valor sí enviada no se actualizó" >&2; exit 1; }
echo "OK: secreto vacío no toca la clave existente; secreto con valor sí se actualiza"

echo "=== Caso 9: agregar() rechaza un valor con salto de línea interno (igual que en deploy.yml) ==="
# Contraparte del caso 3: fija que la GUARDA de deploy.yml contra saltos de
# línea (no el bug de formato del propio YAML, ya cubierto arriba) también
# está presente en la copia de agregar() que usa esta prueba — si alguien la
# quita de deploy.yml por error, este caso debe dejar de pasar.
if SALIDA9="$(construir_blob_como_deploy_yml "Anthropic__ApiKey" "$(printf 'linea1\nlinea2')" 2>&1)"; then
  echo "FALLO: agregar() aceptó un valor con salto de línea interno" >&2
  exit 1
fi
printf '%s' "$SALIDA9" | grep -q "trae un salto de línea" || { echo "FALLO: no avisó por qué rechazó el salto de línea" >&2; printf '%s' "$SALIDA9" >&2; exit 1; }
echo "OK: valor con salto de línea interno rechazado con aviso claro"

echo "=== Caso 10: CLAVES_PERMITIDAS_SECRETOS_PRODUCCION (ci-deploy.sh) sincronizada con agregar() (deploy.yml) ==="
# Hueco señalado en la revisión posterior a la fusión de #707: nada
# comprobaba que las dos listas coincidieran. Si divergen, el primer secreto
# fuera de la lista blanca hace que actualizar_secretos_produccion rechace
# el envío ENTERO (caso 4) — y como este paso corre en el job
# "aprobacion-produccion", del que depende "produccion", una divergencia
# bloquearía TODOS los despliegues a producción siguientes, no solo el de
# ese secreto, hasta que alguien lo detecte y corrija a mano.
FICHERO_DEPLOY_YML="$DIR_GUION/../.github/workflows/deploy.yml"
CLAVES_DEPLOY_YML="$(grep -oE '^ *agregar "[A-Za-z0-9_]+"' "$FICHERO_DEPLOY_YML" | sed -E 's/^ *agregar "//; s/"$//' | sort)"
CLAVES_CI_DEPLOY="$(printf '%s' "$CLAVES_PERMITIDAS_SECRETOS_PRODUCCION" | sed '/^$/d' | sort)"
if [ "$CLAVES_DEPLOY_YML" != "$CLAVES_CI_DEPLOY" ]; then
  echo "FALLO: CLAVES_PERMITIDAS_SECRETOS_PRODUCCION (ci-deploy.sh) y las claves de agregar() en deploy.yml han divergido:" >&2
  diff <(printf '%s\n' "$CLAVES_CI_DEPLOY") <(printf '%s\n' "$CLAVES_DEPLOY_YML") >&2 || true
  exit 1
fi
echo "OK: las dos listas coinciden exactamente ($(printf '%s\n' "$CLAVES_CI_DEPLOY" | grep -c .) claves)"

echo "=== Caso 11: el trap RETURN de actualizar_secretos_produccion no rompe un 'source' posterior ==="
# Corrección de un error de esta misma PR: la primera versión afirmaba (y
# "demostró" con un experimento insuficiente) que `trap ... RETURN` es local
# a la función que lo define. Es falso — persiste GLOBALMENTE tras el
# `return`, y vuelve a dispararse en el retorno de un `source`/`.`
# posterior en el mismo proceso (no en una llamada de función normal, que
# fue el único caso que aquel experimento probó). En ese disparo tardío,
# `fichero_nuevo` ya no es la variable local de la invocación que definió
# el trap, y bajo `set -u` el proceso aborta con "unbound variable" — el
# fix es que el propio trap se desarme con `trap - RETURN` tras ejecutarse
# una vez (ver ci-deploy.sh). Este caso es el control positivo: falla sin
# ese desarme (comprobado a mano quitándolo), pasa con él.
export FICHERO_ENV_SECRETOS_PRODUCCION="$DIR/.env-caso11"
: > "$FICHERO_ENV_SECRETOS_PRODUCCION"
# Invocación DIRECTA, sin pipe — como hace main() en producción (línea con
# `actualizar_secretos_produccion` a secas, heredando el stdin del propio
# proceso de ci-deploy.sh). Con un pipe (`printf ... | actualizar_...`) el
# lado derecho corre en su propio SUBSHELL en bash, y el trap RETURN que
# define moriría con ese subshell sin propagarse nunca al proceso del test
# — un primer intento de este caso usó un pipe y por eso NO detectaba la
# ausencia de `trap - RETURN` (el "control positivo" nunca podía fallar).
actualizar_secretos_produccion <<< "Anthropic__ApiKey=valor-caso11"

# Comprobación directa (lo que Codex y el coordinador midieron con
# `trap -p RETURN`): tras retornar, el trap debe estar desarmado. Sin esto,
# el caso de abajo (source posterior) podía dar un falso verde si el `rm -f`
# usara "${fichero_nuevo:-}" en vez de "$fichero_nuevo" — el operador ":-"
# suprime el error de `set -u` y enmascara justo la regresión que este caso
# existe para detectar (error real de la primera versión de este arreglo).
TRAP_TRAS_RETORNO="$(trap -p RETURN)"
[ -z "$TRAP_TRAS_RETORNO" ] || { echo "FALLO: el trap RETURN sigue instalado tras actualizar_secretos_produccion: $TRAP_TRAS_RETORNO" >&2; exit 1; }

FICHERO_TRIVIAL="$DIR/trivial.sh"
echo 'echo "fichero trivial cargado"' > "$FICHERO_TRIVIAL"
# shellcheck disable=SC1090
source "$FICHERO_TRIVIAL"
echo "OK: trap RETURN desarmado tras retornar; 'source' posterior no abortó bajo set -u"

echo "TODAS LAS PRUEBAS PASARON"
