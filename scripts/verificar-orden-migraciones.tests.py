"""Tabla de verdad de verificar-orden-migraciones.py sobre un repositorio temporal.

Los árboles se escriben directamente en el índice: no hace falta identidad de git
ni tocar el disco. Sale con 1 si algún caso no da el resultado esperado.
"""

import functools
import pathlib
import subprocess
import sys
import tempfile


GUARDA = pathlib.Path(__file__).with_name("verificar-orden-migraciones.py").resolve()
CARPETA = "src/CaeManager.Migrations.PostgreSQL/Migrations/"
MAXIMA = "20260101000000_Maxima"
INVERTIDA = "20250101000000_Invertida"


def migracion(identificador, fichero=None):
    fichero = fichero or identificador
    return {
        f"{fichero}.cs": "public partial class M : Migration { }\n",
        f"{fichero}.Designer.cs": f'[DbContext(typeof(C))]\n[Migration("{identificador}")]\npartial class M {{ }}\n',
    }


BASE = {
    **migracion(MAXIMA),
    **migracion(INVERTIDA),
    "CaeManagerDbContextModelSnapshot.cs": "partial class S { }\n",
}
SIN_MAXIMA = {k: v for k, v in BASE.items() if MAXIMA not in k}

CASOS = [
    ("Sin migraciones nuevas", BASE, 0, "0 nuevas, máximo de la base 20260101000000"),
    ("Posterior", {**BASE, **migracion("20270101000000_Nueva")}, 0, "1 nuevas"),
    ("Posterior con eñe", {**BASE, **migracion("20270101000000_AñadeAlgo")}, 0, "1 nuevas"),
    (
        "Anterior",
        {**BASE, **migracion("20251201000000_Anterior")},
        1,
        "20251201000000_Anterior no es posterior al máximo de la base (20260101000000)",
    ),
    ("Misma marca y otro nombre posterior", {**BASE, **migracion("20260101000000_Zeta")}, 1, "no es posterior"),
    ("Misma marca y otro nombre anterior", {**BASE, **migracion("20260101000000_Alfa")}, 1, "no es posterior"),
    ("Identificador sin marca de 14 dígitos", {**BASE, **migracion("2027_Nueva")}, 1, "2027_Nueva no es posterior"),
    (
        "En una subcarpeta",
        {**BASE, **migracion("20251201000000_Anterior", "Sub/20251201000000_Anterior")},
        1,
        "20251201000000_Anterior no es posterior",
    ),
    (
        "El fichero parece posterior pero el atributo es anterior",
        {**BASE, **migracion("20251201000000_Anterior", "20270101000000_Anterior")},
        1,
        "20251201000000_Anterior no es posterior",
    ),
    (
        "El fichero parece anterior pero el atributo es posterior",
        {**BASE, **migracion("20270101000000_Nueva", "20251201000000_Nueva")},
        0,
        "1 nuevas",
    ),
    (
        "Rename del fichero sin cambiar el atributo",
        {**SIN_MAXIMA, **migracion(MAXIMA, "20260101000000_NombreNuevo")},
        0,
        "0 nuevas",
    ),
    (
        "Rename que conserva la marca y cambia el nombre",
        {**SIN_MAXIMA, **migracion("20260101000000_NombreNuevo")},
        1,
        "20260101000000_NombreNuevo no es posterior",
    ),
    (
        "Rename a una marca anterior",
        {**SIN_MAXIMA, **migracion("20251201000000_Movida")},
        1,
        "20251201000000_Movida no es posterior",
    ),
    (
        "Rename a una marca posterior de una migración ya fusionada",
        {**SIN_MAXIMA, **migracion("20270101000000_Movida")},
        1,
        "::error::Estas migraciones de la base desaparecen",
    ),
]


def main():
    with tempfile.TemporaryDirectory(prefix="orden-migraciones-", ignore_cleanup_errors=True) as directorio:
        def ejecutar(*orden, entrada=None):
            return subprocess.run(
                orden, cwd=directorio, input=entrada, capture_output=True, encoding="utf-8", errors="replace"
            )

        def git(*argumentos, entrada=None):
            resultado = ejecutar("git", *argumentos, entrada=entrada)
            if resultado.returncode:
                sys.exit(f"git {' '.join(argumentos)}: {resultado.stderr}")
            return resultado.stdout.strip()

        @functools.cache
        def objeto(contenido):
            return git("hash-object", "-w", "--stdin", entrada=contenido)

        def arbol(ficheros, carpeta=CARPETA):
            git("read-tree", "--empty")
            entradas = [f"100644 {objeto(contenido)}\t{carpeta}{nombre}\0" for nombre, contenido in ficheros.items()]
            git("update-index", "-z", "--index-info", entrada="".join(entradas))
            return git("write-tree")

        def comprobar(titulo, base, head, codigo, texto):
            resultado = ejecutar(sys.executable, str(GUARDA), base, head)
            salida = resultado.stdout + resultado.stderr
            correcto = resultado.returncode == codigo and texto in salida
            print(f"{'ok' if correcto else 'FALLA'} - {titulo} (salida {resultado.returncode})")
            if not correcto:
                print(f"  esperado {codigo} con «{texto}»; salida:\n{salida}")
            return correcto

        git("init", "-q")
        base = arbol(BASE)
        resultados = [comprobar(titulo, base, arbol(head), codigo, texto) for titulo, head, codigo, texto in CASOS]
        resultados += [
            comprobar("Referencia ilegible", base, "referencia-inexistente", 1, "::error::No se pudo leer"),
            comprobar(
                "La carpeta desaparece de HEAD",
                base,
                arbol({"Otro.cs": ""}, carpeta=""),
                1,
                "::error::Estas migraciones de la base desaparecen",
            ),
            comprobar(
                "Repositorio sin la carpeta de migraciones",
                arbol({"Otro.cs": ""}, carpeta=""),
                arbol({"Otro.cs": ""}, carpeta=""),
                0,
                "::warning::La base no tiene migraciones",
            ),
        ]

    if not all(resultados):
        sys.exit(f"{resultados.count(False)} de {len(resultados)} casos fallan.")
    print(f"{len(resultados)} casos correctos.")


if __name__ == "__main__":
    main()
