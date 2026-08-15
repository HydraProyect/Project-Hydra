#!/usr/bin/env bash
# Capa determinista de "vigilancia de deriva documental ampliada" (§6.5 del
# MACRO_PLAN de negocio). Extrae de un conjunto de docs las referencias a
# código entre backticks (rutas de archivo, nombres de clase/tipo, claves de
# configuración) y comprueba si existen de verdad en este árbol de repo.
#
# Es el filtro barato ANTES de la pasada semántica de un agente — encuentra
# el caso mecánico ("esta doc nombra un archivo que ya no existe") con grep,
# gratis. No encuentra el caso semántico ("esta doc dice que corre en
# Railway pero el código y DEPLOY.md ya dicen Hetzner") — eso lo hace la
# lectura del agente, ver tecnico/RUNBOOK-VIGILANCIA-DERIVA-DOCUMENTAL.md en
# el repo de negocio (Project-Hydra-Negocio), que también trae la lista
# curada de qué docs pasar aquí y por qué esas y no "todas".
#
# Una referencia "no encontrada" NO es automáticamente un hallazgo: puede ser
# (a) drift real, (b) un identificador renombrado que la doc no siguió, o (c)
# un falso positivo (la heurística de qué es "código" es aproximada). El
# script solo reduce el conjunto que un humano/agente tiene que mirar; el
# runbook de negocio explica cómo triarlo.
#
# Uso:
#   ./scripts/detectar-referencias-docs.sh <doc1.md> [doc2.md ...]
#
# Ejemplo real (ejecutado 2026-08-15 durante la construcción de esta
# herramienta, contra el repo de negocio en local):
#   ./scripts/detectar-referencias-docs.sh \
#     ../Project-Hydra-Negocio/tecnico/DESIGN_DECISION_LOG.md
#   -> señaló `scripts/validar-gobernanza-docs.py` y `docs/README.md` como no
#      encontrados en este repo — confirmado a mano: ese script y esa ruta no
#      existen en Project-Hydra (grep -r del repo entero, cero resultados).
#      La afirmación de DDL-055 sobre un gate de CI de "frontera de autoridad
#      documental" no corresponde a nada ejecutable hoy. No se ha corregido
#      el documento en esta sesión (no es el alcance de 6.5) — queda anotado
#      aquí como el hallazgo real que demuestra que la herramienta funciona.

set -euo pipefail

if [ "$#" -eq 0 ]; then
  echo "Uso: $0 <doc1.md> [doc2.md ...]" >&2
  exit 1
fi

RAIZ_REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ENCONTRADOS_SIN_RESOLVER=0
DIR_CACHE="$(mktemp -d)"
trap 'rm -rf "$DIR_CACHE"' EXIT

# Una sola pasada de `git ls-files` + `git grep` POR REPO (no por token): la
# primera versión de este script invocaba git una vez por cada referencia
# encontrada (cientos, sobre repos de cientos de archivos) y tardaba minutos.
# Aquí se vuelca una vez el nombre de cada archivo versionado y una vez el
# contenido completo de los archivos de texto versionados a un archivo en
# disco por repo, y las comprobaciones posteriores son `grep -F` en local
# contra ese volcado — sin más llamadas a git.
volcar_repo() {
  local raiz="$1" clave="$2"
  [ -f "$DIR_CACHE/$clave.nombres" ] && return 0
  if git -C "$raiz" rev-parse --show-toplevel >/dev/null 2>&1; then
    git -C "$raiz" ls-files > "$DIR_CACHE/$clave.nombres" 2>/dev/null || : > "$DIR_CACHE/$clave.nombres"
    git -C "$raiz" grep -I -F -h -- '' > "$DIR_CACHE/$clave.contenido" 2>/dev/null || : > "$DIR_CACHE/$clave.contenido"
  else
    : > "$DIR_CACHE/$clave.nombres"
    : > "$DIR_CACHE/$clave.contenido"
  fi
}

# Comprobación completa (nombre de archivo + contenido) — solo segura para
# RAIZ_REPO (el repo de código, siempre distinto del repo donde vive el
# doc que se está auditando).
existe_en_volcado() {
  local clave="$1" token="$2" nombre_base="$3"
  grep -qF "$nombre_base" "$DIR_CACHE/$clave.nombres" 2>/dev/null && return 0
  grep -qF -- "$token" "$DIR_CACHE/$clave.contenido" 2>/dev/null && return 0
  return 1
}

# Comprobación SOLO por nombre de archivo — es la que se usa contra el repo
# donde vive el propio doc auditado (RAIZ_DOC). Comprobar por CONTENIDO ahí
# sería circular: el doc que se está auditando forma parte de ese mismo
# volcado, así que cualquier token que el doc cita SIEMPRE "aparece en el
# contenido de RAIZ_DOC" — es el propio doc citándose a sí mismo. Este bug
# se encontró y corrigió al construir el script (primera versión daba 0
# referencias sin resolver en todos los docs de prueba, incluida la que sí
# es drift real confirmado a mano — ver el ejemplo de cabecera).
existe_en_volcado_solo_nombre() {
  local clave="$1" nombre_base="$2"
  grep -qF "$nombre_base" "$DIR_CACHE/$clave.nombres" 2>/dev/null && return 0
  return 1
}

for doc in "$@"; do
  if [ ! -f "$doc" ]; then
    echo "AVISO: no existe $doc, se omite" >&2
    continue
  fi
  echo "=== $doc ==="
  # Repo propio del doc (para resolver referencias doc-a-doc, p.ej. un
  # runbook que nombra otro archivo del mismo repositorio de negocio) además
  # del repo de código (RAIZ_REPO) — sin este segundo repo, cualquier
  # referencia cruzada entre docs del mismo directorio sale como falso
  # positivo (ruido dominante en la primera versión de este script).
  RAIZ_DOC="$(git -C "$(dirname "$doc")" rev-parse --show-toplevel 2>/dev/null || dirname "$doc")"
  volcar_repo "$RAIZ_REPO" "codigo"
  CLAVE_DOC="doc_$(echo "$RAIZ_DOC" | md5sum | cut -c1-8)"
  volcar_repo "$RAIZ_DOC" "$CLAVE_DOC"
  HUBO_ALGUNO=0
  # Referencias entre backticks con pinta de código: contienen '/', '.', '_'
  # o ':' (rutas, nombres de archivo, claves de configuración tipo A:B).
  while IFS=: read -r linea ref; do
    [ -z "${ref:-}" ] && continue
    token="${ref#\`}"
    token="${token%\`}"
    # Descarta prosa en minúsculas sin separadores y números/fechas puras.
    if [[ "$token" =~ ^[a-z-]+$ ]] || [[ "$token" =~ ^[0-9.,/-]+$ ]]; then
      continue
    fi
    nombre_base="${token##*/}"
    if existe_en_volcado "codigo" "$token" "$nombre_base" \
       || existe_en_volcado_solo_nombre "$CLAVE_DOC" "$nombre_base"; then
      continue
    fi
    echo "  [línea $linea] no encontrado en $RAIZ_REPO ni en $RAIZ_DOC: $token"
    HUBO_ALGUNO=1
    ENCONTRADOS_SIN_RESOLVER=$((ENCONTRADOS_SIN_RESOLVER + 1))
  done < <(grep -noP '`[A-Za-z0-9_./:-]{3,}`' "$doc" 2>/dev/null || true)
  [ "$HUBO_ALGUNO" -eq 0 ] && echo "  (todas las referencias entre backticks resuelven en alguno de los dos repos)"
done

echo
echo "Total de referencias sin resolver: ${ENCONTRADOS_SIN_RESOLVER}"
echo "Siguiente paso: pasar esta lista a un agente con el prompt de"
echo "tecnico/RUNBOOK-VIGILANCIA-DERIVA-DOCUMENTAL.md (repo de negocio) para"
echo "triarla (drift real vs. falso positivo) y, si procede, abrir el issue"
echo "con evidencia — el script no abre nada por sí solo."
