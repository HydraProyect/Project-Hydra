#!/bin/sh
set -e

# El volumen persistente de Railway (/data) puede venir de antes de que la
# imagen empezara a correr como usuario no-root (P2 #25 de
# docs/business/MATURITY_REVIEW.md, ver también DEPLOY.md § 2). Sin este
# chown, dataprotection-keys/ y documentos/ quedan sin permiso de escritura
# para $APP_UID y el arranque falla en cascada: el keyring de DataProtection
# no se puede leer -> el antiforgery token no se puede cifrar/descifrar ->
# excepción sin capturar en cualquier página con formulario (incluido el
# login). Reproducido en producción el 2026-08-02.
#
# El chequeo de propietario antes de correr chown evita repetir un chown -R
# recursivo (potencialmente caro si documentos/ tiene muchos PDFs locales)
# en cada arranque una vez que el volumen ya quedó corregido.
if [ -d /data ]; then
  propietario_actual="$(stat -c '%u' /data)"
  if [ "$propietario_actual" != "$APP_UID" ]; then
    chown -R "$APP_UID":"$APP_UID" /data
  fi
fi

exec gosu "$APP_UID":"$APP_UID" dotnet CaeManager.Web.dll --urls "http://+:${PORT:-8080}"
