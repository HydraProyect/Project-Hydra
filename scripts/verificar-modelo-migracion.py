"""Guarda del modelo de las migraciones de EF Core.

Uso: verificar-modelo-migracion.py BASE HEAD

El snapshot es el modelo del que parte la siguiente migración, y el BuildTargetModel
del .Designer.cs es el modelo que EF Core cree tener tras aplicar la migración. Si
divergen, el siguiente `dotnet ef migrations add` calcula su diferencia contra un
modelo que no es el real. Por eso falla si:

- la última migración que HEAD añade respecto de BASE no tiene el mismo modelo que
  el snapshot de HEAD. Lo normal es que nazca de una migración sin snapshot, de un
  .Designer.cs escrito a mano o de dos PR cruzadas, y por eso CI compara también en
  la cola de fusión, contra lo que ya va delante;
- HEAD no añade migraciones y aun así cambia el modelo del snapshot;
- el inventario de HEAD está incompleto: hay más clases «: Migration» fuera de los
  *.Designer.cs que identificadores [Migration("...")] reconocidos (el atributo solo
  se reconoce en su forma canónica, y una migración escrita a mano con otra, como
  [Migration ("...")], quedaría fuera de la guarda sin avisar).

Solo se comparan las migraciones nuevas: las ya fusionadas no se pueden corregir sin
reescribirlas. ProductVersion no es modelo: `migrations add` lo sube en el .Designer.cs
y puede dejarlo sin subir en el snapshot si el modelo no cambia.
"""

import re
import subprocess
import sys

CARPETA = "src/CaeManager.Migrations.PostgreSQL/Migrations/"
SNAPSHOT = CARPETA + "CaeManagerDbContextModelSnapshot.cs"
ATRIBUTO = re.compile(r'\[Migration\("([^"\r\n]+)"\)\]')
# Conserva los literales completos; la separación entre tokens sí es irrelevante.
TOKEN = re.compile(r'\s+|//[^\n]*|/\*[\s\S]*?\*/|@"(?:""|[^"])*"|"(?:\\.|[^"\\])*"|\'(?:\\.|[^\'\\])*\'|[A-Za-z_][A-Za-z_0-9]*|[0-9]+|.', re.DOTALL)
VERSION = ['"ProductVersion"', ","]
# Misma detección que verificar-orden-migraciones.py y deploy/listar-migraciones-ef.sh.
CLASE = re.compile(r"\bclass\s+\w+\s*:\s*(?:global::)?(?:\w+\.)*Migration\b")


def git(*args):
    resultado = subprocess.run(["git", *args], capture_output=True)
    if resultado.returncode:
        raise ValueError(f"No se pudo ejecutar git {' '.join(args)}: {resultado.stderr.decode('utf-8', errors='replace').strip()}")
    return resultado.stdout.decode("utf-8")


def clases_migracion(ref):
    """Clases «: Migration» de los ficheros de la carpeta que no son *.Designer.cs."""
    listado = git("ls-tree", "-r", "-z", "--name-only", ref, "--", CARPETA)
    total = 0
    for ruta in listado.split("\0"):
        if ruta.endswith(".cs") and not ruta.endswith(".Designer.cs"):
            total += len(CLASE.findall(git("show", f"{ref}:{ruta}")))
    return total


def migraciones(ref, exigir_inventario_completo=False):
    resultado = subprocess.run(["git", "grep", "-z", "-oE", r'\[Migration\("[^"]+"\)\]', ref, "--", f"{CARPETA}*.Designer.cs"], capture_output=True)
    if resultado.returncode not in (0, 1):
        raise ValueError(f"No se pudieron leer las migraciones de {ref}.")
    encontrados = {}
    for entrada in resultado.stdout.decode("utf-8").splitlines():
        archivo, separador, atributo = entrada.partition("\0")
        ruta = archivo.removeprefix(ref + ":")
        ids = ATRIBUTO.findall(atributo)
        if not separador or len(ids) != 1 or ids[0] in encontrados or ruta in encontrados.values():
            raise ValueError(f"Atributo Migration ausente, duplicado o ambiguo en {ruta}. Deja un único identificador por migración.")
        encontrados[ids[0]] = ruta
    if exigir_inventario_completo:
        clases = clases_migracion(ref)
        if clases != len(encontrados):
            raise ValueError(
                f"El inventario de migraciones está incompleto: {clases} clases «: Migration» fuera de *.Designer.cs "
                f"frente a {len(encontrados)} identificadores [Migration(\"...\")] reconocidos. Una migración con el "
                "atributo escrito de otra forma (por ejemplo [Migration (\"...\")]) o sin su .Designer.cs queda fuera "
                "de las guardas. Escribe el atributo como [Migration(\"<identificador>\")] en su .Designer.cs, que es "
                "la forma que reconoce EF Core al generarla."
            )
    return encontrados


def cuerpo(contenido, metodo):
    contenido = contenido.replace("\r\n", "\n")  # Un literal multilínea no depende del fin de línea del blob.
    firma = re.compile(r'\bprotected\s+override\s+void\s+' + metodo + r'\s*\(\s*ModelBuilder\s+\w+\s*\)\s*\{')
    firmas = list(firma.finditer(contenido))
    if len(firmas) != 1:
        raise ValueError(f"No se encontró un único método {metodo}. Regenera el fichero con EF Core; si ya es el generado, la firma de esta guarda ha quedado vieja.")
    tokens = []
    profundidad = 1
    for match in TOKEN.finditer(contenido, firmas[0].end()):
        token = match.group()
        if token.isspace() or token.startswith(("//", "/*")):
            continue
        if token == "{":
            profundidad += 1
        elif token == "}":
            profundidad -= 1
            if profundidad == 0:
                return tokens
        tokens.append('"<versión>"' if tokens[-2:] == VERSION else token)
    raise ValueError(f"El cuerpo de {metodo} está incompleto. Revisa sus llaves; si el fichero es el generado, ajusta TOKEN en esta guarda.")


def contexto(tokens, indice):
    return " ".join(tokens[max(0, indice - 6):indice + 6]).replace("\n", " ")[:200]


def primera_diferencia(a, b):
    indice = next((i for i, (x, y) in enumerate(zip(a, b)) if x != y), min(len(a), len(b)))
    return f"el modelo de la migración dice «{contexto(a, indice)}» y el snapshot dice «{contexto(b, indice)}»"


def comprobar(base, head):
    anteriores = migraciones(base)
    actuales = migraciones(head, exigir_inventario_completo=True)
    nuevas = sorted(actuales.keys() - anteriores.keys())
    if not nuevas:
        antes = cuerpo(git("show", f"{base}:{SNAPSHOT}"), "BuildModel")
        despues = cuerpo(git("show", f"{head}:{SNAPSHOT}"), "BuildModel")
        if antes != despues:
            raise ValueError(
                f"{SNAPSHOT} cambia el modelo sin ninguna migración nueva. "
                "Genera la migración que le corresponde con dotnet ef migrations add, o revierte el cambio del snapshot."
            )
        print("Sin migraciones nuevas y sin cambios de modelo en el snapshot.")
        return
    ultima = nuevas[-1]
    ruta = actuales[ultima]
    designer = cuerpo(git("show", f"{head}:{ruta}"), "BuildTargetModel")
    snapshot = cuerpo(git("show", f"{head}:{SNAPSHOT}"), "BuildModel")
    if designer != snapshot:
        raise ValueError(
            f"El BuildTargetModel de {ruta} no coincide con BuildModel del snapshot: {primera_diferencia(designer, snapshot)}. "
            "Qué hacer: actualiza la rama con origin/main; si la migración señalada no es de esta PR, basta con empujar la rama actualizada. "
            f"Si la migración cambia el modelo, borra sus ficheros, restaura el snapshot con git checkout origin/main -- {SNAPSHOT} "
            "y vuelve a generarla con dotnet ef migrations add, que rehace los dos: regenerar sin restaurar el snapshot da una migración vacía. "
            "Si es solo SQL, copia el cuerpo de BuildModel en su BuildTargetModel. "
            "En la cola de fusión esto significa que otra PR con migración ha entrado antes: reencola tras corregirlo."
        )
    print(f"El modelo de {ultima} coincide con el snapshot.")


def main():
    if len(sys.argv) != 3:
        sys.exit("Uso: verificar-modelo-migracion.py BASE HEAD")
    sys.stdout.reconfigure(encoding="utf-8")  # Hay identificadores con «ñ».
    sys.stderr.reconfigure(encoding="utf-8")
    try:
        comprobar(*sys.argv[1:])
    except (ValueError, UnicodeError) as error:
        sys.exit(f"::error::{error}")


if __name__ == "__main__":
    main()
