#!/bin/bash
# Inventario de migraciones de EF Core que lleva una imagen (P1-F1).
#
# Uso: listar-migraciones-ef.sh <raíz del repositorio>
#
# Imprime, en una sola línea y separados por comas, los identificadores de
# `[Migration("...")]` de CaeManager.Migrations.PostgreSQL, ordenados. El job
# `imagen` de .github/workflows/deploy.yml los graba en la etiqueta
# `es.talveg.migraciones-ef` de caemanager:<sha>, y deploy/volver-atras.sh la
# compara con `__EFMigrationsHistory` antes de arrancar una imagen anterior: si
# la base tiene una migración que la imagen no conoce, volver a ella exigiría
# deshacer esquema, y eso no se hace nunca.
#
# Se lee del código fuente y no del ensamblado porque es el mismo atributo que
# EF Core usa para descubrir migraciones, y el build compila exactamente estos
# ficheros (el .csproj no excluye ninguno). La etiqueta va dentro del tarball
# firmado, así que la firma la cubre igual que al resto de la imagen.
#
# Falla (código 1, sin salida) si no encuentra ninguna migración o si hay un
# identificador repetido o con una coma o un espacio (hay identificadores con
# «ñ», así que no se restringe a ASCII): una etiqueta vacía o ambigua dejaría
# a volver-atras.sh sin nada fiable con que comparar.

set -euo pipefail

raiz="${1:?uso: listar-migraciones-ef.sh <raíz del repositorio>}"
dir="$raiz/src/CaeManager.Migrations.PostgreSQL/Migrations"

if [ ! -d "$dir" ]; then
    echo "listar-migraciones-ef: no existe $dir" >&2
    exit 1
fi

ids="$(grep -rhoE '\[Migration\("[^"]*"\)\]' --include='*.cs' "$dir" \
    | sed -E 's/^\[Migration\("([^"]*)"\)\]$/\1/' | LC_ALL=C sort || true)"

if [ -z "$ids" ]; then
    echo "listar-migraciones-ef: ninguna migración en $dir" >&2
    exit 1
fi
if printf '%s\n' "$ids" | grep -qE '^$|[,[:space:]]'; then
    echo "listar-migraciones-ef: identificador de migración vacío o con coma o espacio" >&2
    exit 1
fi
repetidos="$(printf '%s\n' "$ids" | uniq -d)"
if [ -n "$repetidos" ]; then
    echo "listar-migraciones-ef: identificadores repetidos: $repetidos" >&2
    exit 1
fi

printf '%s\n' "$ids" | paste -sd, -
