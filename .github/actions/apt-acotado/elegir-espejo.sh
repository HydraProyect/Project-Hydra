#!/usr/bin/env bash
# Imprime la URL base del primer archivo de Ubuntu que responde desde este
# runner para el codename dado, y termina con 0. Si ninguno responde, no
# imprime nada y termina con 1.
#
# Uso: elegir-espejo.sh <codename> [solo-reserva]     (p. ej. noble, resolute)
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

codename="${1:?Uso: elegir-espejo.sh <codename> [solo-reserva]}"

candidatos=(
  https://archive.ubuntu.com/ubuntu/
  https://mirrors.edge.kernel.org/ubuntu/
  https://mirror.init7.net/ubuntu/
  https://mirrors.mit.edu/ubuntu/
)

# solo-reserva: quien llama ya sabe que *.ubuntu.com no le sirve (el job de la
# imagen Docker, que sondea antes las fuentes http de la base) y solo quiere un
# espejo de fuera de ubuntu.com.
case "${2:-}" in
  "") ;;
  solo-reserva)
    candidatos=("${candidatos[@]:1}") ;;
  *)
    echo "elegir-espejo.sh: segundo argumento desconocido «$2» (solo se admite solo-reserva)." >&2
    exit 2 ;;
esac

# Solo vale un 200 final del propio candidato con la descarga completa: sin -L
# curl no sigue un 3xx, y con -f lo daría por bueno sin haber alcanzado el
# InRelease. El código de salida de curl también cuenta: un 200 que se corta o
# se cuelga a mitad del cuerpo no es una respuesta.
responde_200() {
  local codigo
  codigo="$(curl -sS --max-time 10 --max-redirs 0 -o /dev/null -w '%{http_code}' "$1")" || return 1
  [ "$codigo" = "200" ]
}

for base in "${candidatos[@]}"; do
  if responde_200 "${base}dists/${codename}/InRelease" \
    && responde_200 "${base}dists/${codename}-security/InRelease"; then
    echo "$base"
    exit 0
  fi
  echo "El archivo ${base} no responde para ${codename}." >&2
done

exit 1
