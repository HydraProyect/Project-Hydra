"""Guarda de orden de las migraciones de EF Core.

Uso: verificar-orden-migraciones.py BASE HEAD

Falla si HEAD añade una migración cuya marca de tiempo (los 14 dígitos del
identificador) es menor o igual que la máxima de BASE. EF Core no descarta esas
migraciones, pero las aplica en un orden distinto en cada base y su modelo
histórico (.Designer.cs) deja de corresponder al esquema real. Lo normal es
que nazcan de dos PR paralelas, por eso CI compara también en la cola de fusión,
contra lo que ya va delante.

El identificador sale del atributo [Migration("...")], el mismo que usan EF Core
y deploy/listar-migraciones-ef.sh, y no del nombre del fichero: el fichero puede
cambiar de nombre sin que cambie la migración, y al revés. Por eso «añadida»
significa «identificador que está en HEAD y no en BASE»: mover o renombrar el
fichero sin tocar el atributo no añade nada, y cambiar el atributo equivale a
añadir una migración nueva, aunque git lo vea como un rename.

También falla si un identificador de BASE desaparece de HEAD: renombrar una
migración ya fusionada hace que las bases que la aplicaron la reejecuten, y si
desaparecen todas es que la carpeta se ha movido y esta guarda se quedaría sin
nada que comparar.
"""

import re
import subprocess
import sys


CARPETA = "src/CaeManager.Migrations.PostgreSQL/Migrations/"
ATRIBUTO = r'\[Migration\("[^"]*"\)\]'
MARCA = re.compile(r"[0-9]{14}(?=_)")


def identificadores(referencia):
    resultado = subprocess.run(
        ["git", "grep", "-hoE", ATRIBUTO, referencia, "--", f"{CARPETA}*.cs"],
        stdout=subprocess.PIPE,
    )
    # git grep devuelve 1 si no hay coincidencias; cualquier otro código es un
    # error real, y una referencia ilegible no puede leerse como «sin migraciones».
    if resultado.returncode not in (0, 1):
        sys.exit(f"::error::No se pudo leer las migraciones de {referencia}.")
    prefijo, sufijo = len('[Migration("'), len('")]')
    return {linea[prefijo:-sufijo] for linea in resultado.stdout.decode("utf-8").splitlines()}


def marca(identificador):
    coincidencia = MARCA.match(identificador)
    return coincidencia.group() if coincidencia else None


def main():
    if len(sys.argv) != 3:
        sys.exit("Uso: verificar-orden-migraciones.py BASE HEAD")
    sys.stdout.reconfigure(encoding="utf-8")  # Hay identificadores con «ñ».
    en_base = identificadores(sys.argv[1])
    en_head = identificadores(sys.argv[2])
    maximo = max(filter(None, map(marca, en_base)), default="")
    anadidas = sorted(en_head - en_base)
    incorrectas = [i for i in anadidas if marca(i) is None or marca(i) <= maximo]

    quitadas = sorted(en_base - en_head)

    if not en_base:
        print("::warning::La base no tiene migraciones: la guarda no compara con nada.")
    if quitadas:
        print(
            "::error::Estas migraciones de la base desaparecen, y una base que ya las aplicó "
            "reejecutaría la que las sustituya. Si la carpeta cambió de ruta, actualiza CARPETA "
            f"en esta guarda; si es una compactación deliberada, ajústala en la misma PR: {', '.join(quitadas)}."
        )
    for identificador in incorrectas:
        print(
            f"::error::La migración {identificador} no es posterior al máximo de la base "
            f"({maximo or 'ninguno'}). Dale un identificador posterior en el .cs, en el "
            ".Designer.cs y en el atributo [Migration]."
        )
    if incorrectas or quitadas:
        sys.exit(1)
    print(f"Orden de migraciones correcto: {len(anadidas)} nuevas, máximo de la base {maximo or 'ninguno'}.")


if __name__ == "__main__":
    main()
