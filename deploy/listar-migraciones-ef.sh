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
# Comparado en bash, sin proceso externo: con `printf | grep -q` bajo
# pipefail, el SIGPIPE de un inventario largo hacía que la guarda no saltara
# justo cuando había un identificador malo, y con `grep <<<` un fichero
# temporal que no se puede crear daba 1 («ninguno malo»). Los saltos de línea
# separan identificadores: se quitan antes de buscar espacios, y una línea
# vacía se busca aparte.
if [[ "${ids//$'\n'/}" == *[,[:space:]]* || $'\n'"$ids"$'\n' == *$'\n\n'* ]]; then
    echo "listar-migraciones-ef: identificador de migración vacío o con coma o espacio" >&2
    exit 1
fi
repetidos="$(printf '%s\n' "$ids" | uniq -d)"
if [ -n "$repetidos" ]; then
    echo "listar-migraciones-ef: identificadores repetidos: $repetidos" >&2
    exit 1
fi

# El atributo solo se reconoce en su forma canónica (la que EF Core escribe en el
# .Designer.cs). Una migración escrita a mano con otra forma válida, como
# [Migration ("...")], tendría clase pero ningún identificador, y la imagen
# saldría con una etiqueta incompleta. Se cuentan las clases «: Migration» fuera
# de los *.Designer.cs, que son las que EF Core descubre, y tienen que coincidir.
# -z: cada fichero es un solo registro, así que la declaración puede ir en varias líneas.
clases="$(LC_ALL=C grep -rhzoE --include='*.cs' --exclude='*.Designer.cs' \
    '\bclass[[:space:]]+[^[:space:]:<{(]+[[:space:]]*:[[:space:]]*(global::)?([A-Za-z0-9_]+\.)*Migration\b' "$dir" \
    | tr -cd '\0' | wc -c || true)"
clases=$((clases + 0))
total="$(printf '%s\n' "$ids" | wc -l)"
if [ "$clases" -ne "$total" ]; then
    echo "listar-migraciones-ef: $clases clases ': Migration' fuera de *.Designer.cs frente a $total identificadores [Migration(\"...\")]; hay una migración con el atributo escrito de otra forma o sin su .Designer.cs" >&2
    exit 1
fi

printf '%s\n' "$ids" | paste -sd, -
