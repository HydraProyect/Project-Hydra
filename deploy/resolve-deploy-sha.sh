#!/bin/bash
# Resuelve de forma determinista qué commit debe quedar en el working tree de
# un despliegue: exactamente el SHA que se pide, nunca "lo que haya en main
# ahora mismo". Sin esto, un job de despliegue que espera horas por la
# aprobación manual de `environment: produccion` puede terminar desplegando
# un commit posterior al que fue aprobado, si main avanzó mientras tanto —
# incidente real visto el 2026-08-26 con `git merge --ff-only origin/main`.
#
# No conoce el concepto de "entorno" (staging/producción): solo dos
# responsabilidades — validar el SHA y dejarlo exacto en el disco. Separado
# de ci-deploy.sh para poder probarse en CI sin Docker ni el VPS real.
#
# Uso: resolve-deploy-sha.sh <ruta-repo> <sha>
set -euo pipefail

REPO_DIR="${1:?uso: resolve-deploy-sha.sh <ruta-repo> <sha>}"
SHA="${2:?uso: resolve-deploy-sha.sh <ruta-repo> <sha>}"

# Formato estricto ANTES de tocar git: un SSH_ORIGINAL_COMMAND controlado por
# quien tenga la clave de deploy no debe poder inyectar nada que no sea un
# hash de 40 caracteres hexadecimales.
if [[ ! "$SHA" =~ ^[0-9a-f]{40}$ ]]; then
  echo "SHA inválido: '$SHA' — se esperaba un hash completo de 40 caracteres hexadecimales" >&2
  exit 1
fi

cd "$REPO_DIR"
git fetch origin main --quiet

if ! git cat-file -e "${SHA}^{commit}" 2>/dev/null; then
  echo "El commit $SHA no existe en el repositorio (comprobado tras git fetch)" >&2
  exit 1
fi

# No basta con que el objeto exista tras el fetch (podría ser un commit de
# una rama nunca mergeada, o de una historia reescrita). Solo se despliega lo
# que de verdad forma parte de la historia aprobada de main.
if ! git merge-base --is-ancestor "$SHA" origin/main; then
  echo "El commit $SHA no es ancestro de origin/main — rechazado" >&2
  exit 1
fi

git checkout --detach "$SHA" --quiet
echo "Commit fijado para el despliegue: $SHA"
