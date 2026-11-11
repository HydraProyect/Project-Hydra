#!/usr/bin/env bash
# Imprime la URL base del primer archivo de Ubuntu que responde desde este
# runner para el codename dado, y termina con 0. Si ninguno responde, no
# imprime nada y termina con 1.
#
# Uso: elegir-espejo.sh <codename>     (p. ej. noble, resolute)
#
# Orden: archive.ubuntu.com y, detrás, espejos oficiales de Ubuntu alojados
# fuera de ubuntu.com. Desde el 2026-11-11 hay runners de GitHub que no
# resuelven *.ubuntu.com (ni azure.archive ni archive ni security) mientras
# resuelven NuGet, npm y el resto: un espejo de otro dominio sigue llegando.
#
# Un espejo solo sirve los ficheros; no aporta confianza. Las fuentes siguen
# con su Signed-By y apt verifica la firma de cada InRelease con las claves de
# Ubuntu de la imagen. Ningún consumidor de este guion debe añadir
# trusted=yes ni desactivar esa verificación (AptAcotadoEnCiTests lo vigila).
#
# Cada candidato se sondea en el InRelease del pocket principal y en el de
# security, porque el espejo elegido sirve los dos: una base sin -security
# dejaría la imagen o el runner sin las correcciones de seguridad.
set -uo pipefail

codename="${1:?Uso: elegir-espejo.sh <codename>}"

candidatos=(
  https://archive.ubuntu.com/ubuntu/
  https://mirrors.edge.kernel.org/ubuntu/
  https://mirror.init7.net/ubuntu/
  https://mirrors.mit.edu/ubuntu/
)

for base in "${candidatos[@]}"; do
  if curl -fsS --max-time 10 -o /dev/null "${base}dists/${codename}/InRelease" \
    && curl -fsS --max-time 10 -o /dev/null "${base}dists/${codename}-security/InRelease"; then
    echo "$base"
    exit 0
  fi
  echo "El archivo ${base} no responde para ${codename}." >&2
done

exit 1
