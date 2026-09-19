#!/bin/bash
# Verifica que los secretos de CI con clave de proveedor externo (P13 IA, P18
# Stripe en modo prueba) solo se lean desde jobs que declaran el environment
# `ci-integraciones`, dentro de workflows con disparadores de confianza; que
# ningún workflow pueda alcanzar secretos de forma dinámica desde un disparador
# que un PR controla; y que ninguno lleve escrita una clave de Stripe de
# producción.
#
# QUÉ PROTEGE DE VERDAD, Y QUÉ NO (revisión de Codex sobre la primera versión):
#   La barrera contra un adversario con permiso de escritura NO es este guion.
#   Quien puede subir una rama puede editar este guion, el workflow que lo
#   llama, o añadir un workflow nuevo, y el workflow de su rama es el que corre
#   con los secretos de REPOSITORIO. La única barrera que el propio PR no puede
#   reescribir es un ENVIRONMENT de GitHub con política de ramas: sus secretos
#   solo se entregan a jobs que lo declaran Y cuya rama la política admite
#   (`main`). Por eso los secretos restringidos van SOLO como secretos del
#   environment `ci-integraciones` — nunca como secretos de repositorio — y este
#   guion existe para que un cambio de buena fe no rompa esa disposición sin que
#   nadie lo vea:
#     1. cada job que cita un secreto restringido declara `environment: ci-integraciones`;
#     2. el workflow solo se dispara con push, merge_group, workflow_dispatch o
#        schedule (nunca pull_request, pull_request_target, workflow_run, …);
#     3. un workflow con algún disparador que un PR controla no accede a los
#        secretos de forma dinámica (`toJSON(secrets)`, `secrets[...]`,
#        `secrets: inherit`), que es lo que un nombre literal no delata;
#     4. ningún workflow contiene una clave sk_live_/rk_live_ con forma real.
#
# Qué mira, y qué NO mira:
#   - el bloque `on:` en sus tres formas (`on: push`, `on: [a, b]`, bloque con
#     claves) y las líneas que NO son comentario;
#   - NO interpreta YAML de verdad: un `on:` construido con anclas o un job
#     definido por workflow reutilizable (`workflow_call`) queda fuera.
#     workflow_call NO está en la lista blanca justo por eso: hereda el
#     disparador del llamante.
#
# Uso: verificar-secretos-de-ci.sh [directorio-de-workflows]   (defecto: .github/workflows)
# Salida: 0 sin violaciones; 1 con al menos una (una línea `ERROR: ...` por cada una).
set -uo pipefail

DIR="${1:-.github/workflows}"

SECRETOS_RESTRINGIDOS='CI_ANTHROPIC_API_KEY|CI_STRIPE_TEST_API_KEY'
ENTORNO_EXIGIDO='ci-integraciones'
DISPARADORES_DE_CONFIANZA=" push merge_group workflow_dispatch schedule "
# Acceso a secretos que un nombre literal no delata: `secrets[...]`,
# `toJSON(secrets)`/`join(secrets, …)`/`format('{0}', secrets)`, `${{ secrets }}`
# y `secrets: inherit`. `secrets.NOMBRE` (acceso literal) NO casa.
ACCESO_DINAMICO_A_SECRETOS='secrets[[:space:]]*(\[|\)|,|\}\})|secrets:[[:space:]]*inherit'

if [ ! -d "$DIR" ]; then
  echo "ERROR: no existe el directorio de workflows '$DIR' — sin él no se comprobó nada." >&2
  exit 1
fi

# Un directorio existente sin ningún .yml es un instrumento ciego, no un verde.
mapfile -t FICHEROS < <(find "$DIR" -maxdepth 1 -type f \( -name '*.yml' -o -name '*.yaml' \) | sort)
if [ "${#FICHEROS[@]}" -eq 0 ]; then
  echo "ERROR: '$DIR' no contiene ningún workflow — sin ficheros que leer no se comprobó nada." >&2
  exit 1
fi

disparadores_de() {
  awk '
    /^("on"|'"'"'on'"'"'|on):/ {
      resto = $0
      sub(/^[^:]*:[ \t]*/, "", resto)
      sub(/[ \t]*#.*/, "", resto)
      if (resto != "") {
        gsub(/[\[\],]/, " ", resto)
        n = split(resto, a, " ")
        for (i = 1; i <= n; i++) print a[i]
        en = 0
      } else {
        en = 1
      }
      next
    }
    en && /^[^ \t#]/ { en = 0 }
    en && /^  [A-Za-z_]+:/ { k = $1; sub(/:.*/, "", k); print k }
  ' "$1"
}

# Nombres de los jobs que citan un secreto restringido SIN declarar el environment
# exigido como clave de job (`    environment: ci-integraciones`).
jobs_con_secreto_sin_entorno() {
  # sq lleva la comilla simple por variable: no cabe dentro del programa awk (que va
  # entre comillas simples de shell) y \x27 no es portable a mawk, el awk de
  # ubuntu-latest. Por lo mismo, este programa no debe contener comillas simples.
  awk -v re="$SECRETOS_RESTRINGIDOS" -v entorno="$ENTORNO_EXIGIDO" -v sq="'" '
    function cierra() { if (job != "" && usa && !tiene_entorno) print job }
    /^jobs:/ { en = 1; next }
    en && /^[^ \t#]/ { cierra(); job = ""; en = 0; next }
    # Clave de job con comillas dobles, con comillas simples o sin comillas.
    en && $0 ~ ("^  (\"[A-Za-z0-9_-]+\"|" sq "[A-Za-z0-9_-]+" sq "|[A-Za-z0-9_-]+):[ \t]*(#.*)?$") {
      cierra(); job = $1; gsub("[\":" sq "]", "", job); usa = 0; tiene_entorno = 0; next
    }
    en && /^[ \t]*#/ { next }
    # Secreto citado sin ningún job reconocido delante: fallo cerrado, no verde.
    en && $0 ~ re && job == "" { huerfano = 1 }
    en && $0 ~ re { usa = 1 }
    en && $0 ~ "^    environment:[ \t]*" entorno "[ \t]*(#.*)?$" { tiene_entorno = 1 }
    END { cierra(); if (huerfano) print "<job-no-reconocido>" }
  ' "$1"
}

VIOLACIONES=0
error() {
  echo "ERROR: $1" >&2
  VIOLACIONES=$((VIOLACIONES + 1))
}

for fichero in "${FICHEROS[@]}"; do
  nombre="$(basename "$fichero")"
  sin_comentarios="$(grep -v '^[[:space:]]*#' "$fichero")"

  # Una clave de producción de Stripe con forma real (sk_/rk_ + live + cuerpo).
  # El cuerpo mínimo de 10 evita marcar un prefijo citado en un comentario o en
  # un mensaje de error, que es como lo nombran los propios workflows.
  if grep -Eq '[sr]k_live_[A-Za-z0-9]{10,}' "$fichero"; then
    error "$nombre contiene algo con forma de clave de producción de Stripe (sk_live_/rk_live_) — en CI solo modo prueba."
  fi

  mapfile -t DISPARADORES < <(disparadores_de "$fichero")
  usa_restringidos=false
  if grep -Eq "$SECRETOS_RESTRINGIDOS" <<<"$sin_comentarios"; then
    usa_restringidos=true
  fi

  # Disparadores fuera de la lista blanca (los que un PR o un tercero controlan).
  no_confiables=()
  for d in "${DISPARADORES[@]}"; do
    case "$DISPARADORES_DE_CONFIANZA" in
      *" $d "*) ;;
      *) no_confiables+=("$d") ;;
    esac
  done

  if [ "$usa_restringidos" = true ]; then
    if [ "${#DISPARADORES[@]}" -eq 0 ]; then
      error "$nombre usa un secreto restringido pero no se pudo leer ningún disparador de su bloque 'on:' — no se puede demostrar que sea de confianza."
    fi
    for d in "${no_confiables[@]+"${no_confiables[@]}"}"; do
      error "$nombre usa un secreto restringido y se dispara con '$d' — solo push, merge_group, workflow_dispatch o schedule."
    done
    while IFS= read -r job; do
      [ -n "$job" ] || continue
      error "$nombre: el job '$job' cita un secreto restringido sin declarar 'environment: $ENTORNO_EXIGIDO' — sin la política de ramas del environment, cualquier rama con escritura los leería."
    done < <(jobs_con_secreto_sin_entorno "$fichero")
  fi

  # Un disparador que controla un PR (o ilegible) + acceso dinámico a secretos:
  # el nombre no aparece, pero el secreto llega igual.
  if [ "${#no_confiables[@]}" -gt 0 ] || [ "${#DISPARADORES[@]}" -eq 0 ]; then
    if grep -Eiq "$ACCESO_DINAMICO_A_SECRETOS" <<<"$sin_comentarios"; then
      error "$nombre tiene un disparador que un PR puede controlar (o ilegible) y accede a los secretos de forma dinámica (secrets[...], toJSON(secrets), secrets: inherit) — no se puede demostrar que no alcance CI_ANTHROPIC_API_KEY/CI_STRIPE_TEST_API_KEY."
    fi
  fi
done

if [ "$VIOLACIONES" -gt 0 ]; then
  exit 1
fi
echo "OK: ${#FICHEROS[@]} workflows revisados; los secretos restringidos solo se leen desde jobs con 'environment: $ENTORNO_EXIGIDO' y disparadores de confianza."
