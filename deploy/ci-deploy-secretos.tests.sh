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

echo "=== Caso 12: Stripe entra por la inyección (forma válida) y queda entrecomillada ==="
# P18b: hasta este cambio Stripe no estaba en la lista blanca y solo podía llegar a
# producción por una edición manual del .env. Los valores sintéticos se arman por
# concatenación para que este fichero no contenga nada con forma de clave real.
CLAVE_STRIPE_LIVE="rk_""live_""SINTETICA0000000000"
SECRETO_WEBHOOK="whsec_""SINTETICO0000000000"
export FICHERO_ENV_SECRETOS_PRODUCCION="$DIR/.env-caso12"
printf 'DOMINIO=app.talveg.es\n' > "$FICHERO_ENV_SECRETOS_PRODUCCION"
printf 'Stripe__ApiKey=%s\nStripe__WebhookSecret=%s\n' "$CLAVE_STRIPE_LIVE" "$SECRETO_WEBHOOK" \
  | actualizar_secretos_produccion 2>/dev/null
grep -qx "Stripe__ApiKey='$CLAVE_STRIPE_LIVE'" "$FICHERO_ENV_SECRETOS_PRODUCCION" || { echo "FALLO: Stripe__ApiKey no se escribió" >&2; exit 1; }
grep -qx "Stripe__WebhookSecret='$SECRETO_WEBHOOK'" "$FICHERO_ENV_SECRETOS_PRODUCCION" || { echo "FALLO: Stripe__WebhookSecret no se escribió" >&2; exit 1; }
grep -qx 'DOMINIO=app.talveg.es' "$FICHERO_ENV_SECRETOS_PRODUCCION" || { echo "FALLO: perdió una clave no tocada" >&2; exit 1; }
echo "OK: las dos claves de Stripe se escriben; el resto del .env queda intacto"

echo "=== Caso 13: un valor en la variable equivocada se rechaza ENTERO, sin imprimirlo ==="
# Mutación del caso 12 que cambia UNA cosa: el secreto de firma (whsec_) va a
# Stripe__ApiKey. Debe rechazarse el envío completo (.env sin tocar, también la
# clave buena que venía con él) y el mensaje nombra la CLAVE, nunca el valor.
export FICHERO_ENV_SECRETOS_PRODUCCION="$DIR/.env-caso13"
printf 'DOMINIO=app.talveg.es\n' > "$FICHERO_ENV_SECRETOS_PRODUCCION"
ANTES13="$(cat "$FICHERO_ENV_SECRETOS_PRODUCCION")"
if SALIDA13="$(printf 'Anthropic__ApiKey=sk-buena-123\nStripe__ApiKey=%s\n' "$SECRETO_WEBHOOK" | actualizar_secretos_produccion 2>&1)"; then
  echo "FALLO: se aceptó un whsec_ en Stripe__ApiKey" >&2
  exit 1
fi
printf '%s' "$SALIDA13" | grep -qF "'Stripe__ApiKey' no tiene la forma esperada" || { echo "FALLO: el rechazo no fue por la forma del valor" >&2; printf '%s\n' "$SALIDA13" >&2; exit 1; }
printf '%s' "$SALIDA13" | grep -qF "SINTETICO" && { echo "FALLO: el mensaje de error imprimió (parte de) el valor" >&2; exit 1; }
[ "$ANTES13" = "$(cat "$FICHERO_ENV_SECRETOS_PRODUCCION")" ] || { echo "FALLO: .env cambió pese al rechazo" >&2; exit 1; }
echo "OK: rechazo entero, .env intacto, el mensaje no contiene el valor"

echo "=== Caso 14: la clave de API tampoco vale como secreto de webhook ==="
export FICHERO_ENV_SECRETOS_PRODUCCION="$DIR/.env-caso14"
printf 'DOMINIO=app.talveg.es\n' > "$FICHERO_ENV_SECRETOS_PRODUCCION"
if SALIDA14="$(printf 'Stripe__WebhookSecret=%s\n' "$CLAVE_STRIPE_LIVE" | actualizar_secretos_produccion 2>&1)"; then
  echo "FALLO: se aceptó una clave rk_ en Stripe__WebhookSecret" >&2
  exit 1
fi
printf '%s' "$SALIDA14" | grep -qF "'Stripe__WebhookSecret' no tiene la forma esperada" || { echo "FALLO: el rechazo no fue por la forma del valor" >&2; exit 1; }
printf '%s' "$SALIDA14" | grep -qF "SINTETICA" && { echo "FALLO: el mensaje de error imprimió (parte de) el valor" >&2; exit 1; }
echo "OK: whsec_ solo vale como secreto de webhook"

echo "=== Caso 15: verificar_secretos_de_stripe informa de presencia y forma, y NUNCA del valor ==="
# El requisito duro de P18b: comprobar que la clave existe y tiene el prefijo
# esperado sin imprimirla. El control positivo de cada categoría es la salida
# esperada; el negativo, que ningún fragmento del valor aparece en NINGUNA salida.
CLAVE_STRIPE_PRUEBA="sk_""test_""SINTETICA1111111111"
PRUEBA_FICHERO="$DIR/.env-caso15"

comprobar() {  # <etiqueta> <entorno> <fragmento-esperado> ; el .env ya está en $PRUEBA_FICHERO
  local etiqueta="$1" entorno="$2" esperado="$3" salida
  salida="$(verificar_secretos_de_stripe "$PRUEBA_FICHERO" "$entorno" 2>&1)" || { echo "FALLO [$etiqueta]: la comprobación devolvió error" >&2; exit 1; }
  printf '%s' "$salida" | grep -qF -- "$esperado" || { echo "FALLO [$etiqueta]: falta '$esperado' en:" >&2; printf '%s\n' "$salida" >&2; exit 1; }
  for fragmento in SINTETICA SINTETICO 1111111 0000000; do
    printf '%s' "$salida" | grep -qF -- "$fragmento" && { echo "FALLO [$etiqueta]: la salida contiene un fragmento del valor ($fragmento)" >&2; exit 1; }
  done
  echo "OK   [$etiqueta]"
}

printf "Stripe__ApiKey='%s'\nStripe__WebhookSecret='%s'\n" "$CLAVE_STRIPE_LIVE" "$SECRETO_WEBHOOK" > "$PRUEBA_FICHERO"
comprobar "producción con clave live entrecomillada" produccion "Stripe__ApiKey: modo producción"
comprobar "webhook presente" produccion "Stripe__WebhookSecret: presente (whsec_…)"

printf 'Stripe__ApiKey=%s\n' "$CLAVE_STRIPE_LIVE" > "$PRUEBA_FICHERO"
comprobar "sin comillas" produccion "Stripe__ApiKey: modo producción"
comprobar "webhook ausente si no está la línea" produccion "Stripe__WebhookSecret: ausente"

printf 'Stripe__ApiKey="%s"\r\n' "$CLAVE_STRIPE_LIVE" > "$PRUEBA_FICHERO"
comprobar "comillas dobles y CRLF" produccion "Stripe__ApiKey: modo producción"

printf 'Stripe__ApiKey=%s\nStripe__ApiKey=%s\n' "$CLAVE_STRIPE_LIVE" "$CLAVE_STRIPE_PRUEBA" > "$PRUEBA_FICHERO"
comprobar "gana la última aparición (prueba tras live)" produccion "Stripe__ApiKey: modo prueba"
comprobar "producción con clave de prueba avisa" produccion "::warning::Stripe__ApiKey de PRODUCCIÓN es una clave de modo prueba"

printf 'Stripe__ApiKey=\nOtra=cosa\n' > "$PRUEBA_FICHERO"
comprobar "clave vacía = ausente" produccion "Stripe__ApiKey: ausente"

printf 'Stripe__ApiKey=%s\n' "$SECRETO_WEBHOOK" > "$PRUEBA_FICHERO"
comprobar "valor de otra variable = forma inesperada" produccion "Stripe__ApiKey: forma inesperada"
comprobar "…con aviso" produccion "::warning::Stripe__ApiKey en el .env de produccion tiene una forma inesperada"

printf 'Stripe__ApiKey=%s\n' "$CLAVE_STRIPE_LIVE" > "$PRUEBA_FICHERO"
comprobar "staging con clave live avisa" staging "::warning::Stripe__ApiKey de STAGING es una clave de MODO PRODUCCIÓN"

printf 'Stripe__ApiKey=%s\n' "$CLAVE_STRIPE_PRUEBA" > "$PRUEBA_FICHERO"
comprobar "staging con clave de prueba: sin aviso de producción" staging "Stripe__ApiKey: modo prueba"
if verificar_secretos_de_stripe "$PRUEBA_FICHERO" staging 2>&1 | grep -qF "::warning::"; then
  echo "FALLO: staging con clave de prueba no debe avisar" >&2
  exit 1
fi

# Un prefijo parecido pero distinto no cuenta (`Stripe__ApiKeyX=` no es `Stripe__ApiKey=`).
printf 'Stripe__ApiKeyX=%s\n' "$CLAVE_STRIPE_LIVE" > "$PRUEBA_FICHERO"
comprobar "clave con sufijo distinto no cuenta" produccion "Stripe__ApiKey: ausente"

# Fichero ilegible: informa y sigue (nunca aborta con set -e).
SALIDA15="$(verificar_secretos_de_stripe "$DIR/no-existe" produccion 2>&1)" || { echo "FALLO: falló con un fichero inexistente" >&2; exit 1; }
printf '%s' "$SALIDA15" | grep -qF "no se pudo leer" || { echo "FALLO: no avisó de que no pudo leer el fichero" >&2; exit 1; }
echo "OK: fichero inexistente se informa y no aborta"

echo "=== Caso 16: la llamada está cableada DESPUÉS del despliegue sano y no puede tumbarlo ==="
# volcar_diagnostico_si_falla vive dentro de main() y no se ejecuta al hacer
# `source` (ver ci-deploy-diagnostico-memoria.tests.sh, caso 2): se comprueba por
# lectura del fuente, anclando al `exit 1` del `up -d --wait` no sano.
FUENTE_CI_DEPLOY="$DIR_GUION/ci-deploy.sh"
LINEA_UP16="$(grep -n 'docker compose "\${args\[@\]}" up -d --wait' "$FUENTE_CI_DEPLOY" | head -1 | cut -d: -f1)"
[ -n "$LINEA_UP16" ] || { echo "FALLO: no se encontró la línea de 'up -d --wait'" >&2; exit 1; }
LINEA_EXIT16="$(tail -n "+$LINEA_UP16" "$FUENTE_CI_DEPLOY" | grep -n '^        exit 1$' | head -1 | cut -d: -f1)"
LINEA_EXIT16=$((LINEA_UP16 + LINEA_EXIT16 - 1))
mapfile -t LLAMADAS16 < <(grep -n '^        \*staging\*) verificar_secretos_de_stripe\|^        \*) verificar_secretos_de_stripe' "$FUENTE_CI_DEPLOY" | cut -d: -f1)
[ "${#LLAMADAS16[@]}" -eq 2 ] || { echo "FALLO: se esperaban 2 llamadas (staging y producción), hay ${#LLAMADAS16[@]}" >&2; exit 1; }
for linea in "${LLAMADAS16[@]}"; do
  [ "$linea" -gt "$LINEA_EXIT16" ] || { echo "FALLO: una llamada (línea $linea) está antes del exit 1 del despliegue no sano (línea $LINEA_EXIT16)" >&2; exit 1; }
done
grep -q 'verificar_secretos_de_stripe "\${env_file:-.env}" staging || true' "$FUENTE_CI_DEPLOY" \
  && grep -q 'verificar_secretos_de_stripe "\${env_file:-.env}" produccion || true' "$FUENTE_CI_DEPLOY" \
  || { echo "FALLO: falta el '|| true' que impide que la comprobación tumbe un despliegue sano (set -e)" >&2; exit 1; }
echo "OK: las dos llamadas están tras el despliegue sano y protegidas con '|| true'"

echo "=== Caso 17: Caddy (único servicio con puertos públicos) NO carga el .env entero ==="
# Hallazgo de la revisión de Codex de P18b: el servicio caddy cargaba `env_file: .env`
# y recibía TODOS los secretos del stack sin usar ninguno; su Caddyfile solo
# interpola DOMINIO y ACME_EMAIL. Un secreto más en `.env` (Stripe, en este caso)
# ampliaba a un contenedor expuesto a Internet quién podía leerlo.
COMPOSE_PRODUCCION="$DIR_GUION/local/docker-compose.produccion.yml"
bloque_de_servicio() {  # <servicio>: imprime el bloque `  servicio:` hasta el siguiente servicio
  awk -v s="$1" '$0 == "  " s ":" { f = 1; next } f && /^  [A-Za-z0-9_-]+:/ { f = 0 } f' "$COMPOSE_PRODUCCION"
}
# Control positivo del extractor: en `app` SÍ debe verse env_file (si no lo viera, el
# «Caddy no lo tiene» de abajo no significaría nada).
bloque_de_servicio app | grep -q '^    env_file: \.env' || { echo "FALLO: el extractor no ve el env_file de 'app' — el caso no observa lo que dice observar" >&2; exit 1; }
BLOQUE_CADDY="$(bloque_de_servicio caddy)"
[ -n "$BLOQUE_CADDY" ] || { echo "FALLO: no se encontró el bloque del servicio caddy" >&2; exit 1; }
if printf '%s\n' "$BLOQUE_CADDY" | grep -q '^    env_file:'; then
  echo "FALLO: el servicio caddy vuelve a cargar un env_file entero" >&2
  exit 1
fi
printf '%s\n' "$BLOQUE_CADDY" | grep -q '^      DOMINIO: \${DOMINIO}' || { echo "FALLO: caddy no recibe DOMINIO" >&2; exit 1; }
printf '%s\n' "$BLOQUE_CADDY" | grep -q '^      ACME_EMAIL: \${ACME_EMAIL}' || { echo "FALLO: caddy no recibe ACME_EMAIL" >&2; exit 1; }
echo "OK: caddy recibe solo DOMINIO y ACME_EMAIL; app conserva su env_file"

echo "TODAS LAS PRUEBAS PASARON"
