#!/usr/bin/env python3
"""Genera la tabla del vocabulario de pantalla del contrato de terminología desde su fuente única (S1).

La fuente es tests/CaeManager.Architecture.Tests/Vocabulario/Vocabulario.json: la misma que aplica el test
de arquitectura VocabularioDePantallaTests a los .resx y .razor. Este guion produce, a partir de ella, el
bloque de tablas que Project-Hydra-Negocio/CONTRATO_TERMINOLOGIA.md incluye entre las marcas

    <!-- generado: no editar -->
    ...
    <!-- /generado -->

de modo que el contrato y el código no pueden contradecirse: no hay dos copias que mantener.

Uso:
    python3 scripts/vocabulario-tabla.py generar                       # el bloque, por la salida estándar
    python3 scripts/vocabulario-tabla.py aplicar  <documento>        # reescribe SOLO lo que hay entre las marcas
    python3 scripts/vocabulario-tabla.py verificar <documento>       # sale con 1 si el bloque del contrato no coincide
    python3 scripts/vocabulario-tabla.py ejemplos                      # comprueba los ejemplos de cada patrón con `re`
    (opcional, antes del subcomando: --json <ruta>  para otro Vocabulario.json)

`ejemplos` es el segundo consumidor del fichero: los patrones están escritos para valer tanto en .NET como
en Python (solo \\b, grupos y anticipaciones o retrospectivas de ancho fijo), y esta orden lo comprueba con el
motor de Python; el verificador del repositorio de negocio usa los mismos patrones.

Códigos de salida: 0 bien; 1 el bloque no coincide (verificar) o un ejemplo falla (ejemplos); 2 uso o
entrada incorrectos (fichero ausente, marcas ausentes o repetidas, JSON sin los campos obligatorios).
"""
import argparse
import difflib
import json
import re
import sys
from pathlib import Path

RUTA_JSON = Path(__file__).resolve().parent.parent / "tests" / "CaeManager.Architecture.Tests" / "Vocabulario" / "Vocabulario.json"
MARCA_INICIO = "<!-- generado: no editar -->"
MARCA_FIN = "<!-- /generado -->"
RUTA_EN_REPO = "tests/CaeManager.Architecture.Tests/Vocabulario/Vocabulario.json"


class EntradaIncorrecta(Exception):
    pass


def cargar(ruta: Path) -> dict:
    try:
        datos = json.loads(ruta.read_text(encoding="utf-8"))
    except FileNotFoundError:
        raise EntradaIncorrecta(f"no existe {ruta}")
    except json.JSONDecodeError as e:
        raise EntradaIncorrecta(f"{ruta} no es JSON válido: {e}")
    for campo in ("version", "canonicos", "prohibidos", "excepciones"):
        if campo not in datos:
            raise EntradaIncorrecta(f"{ruta} no tiene el campo obligatorio «{campo}»")
    return datos


def celda(texto) -> str:
    """Una celda de tabla Markdown: sin saltos de línea y con la barra vertical escapada."""
    if texto is None:
        return "—"
    t = str(texto).replace("\r", " ").replace("\n", " ").strip()
    return t.replace("|", "\\|") if t else "—"


def lista(valores) -> str:
    return _Cruda(celda(", ".join(valores))) if valores else "—"


def fila(*celdas) -> str:
    return "| " + " | ".join(celda(c) if not isinstance(c, _Cruda) else c.texto for c in celdas) + " |"


class _Cruda:
    """Una celda ya formateada (no se vuelve a escapar)."""

    def __init__(self, texto):
        self.texto = texto


def generar(datos: dict) -> str:
    por_id = {p["id"]: p for p in datos["prohibidos"]}
    salida = [
        f"Generado desde `{RUTA_EN_REPO}` (versión {datos['version']}) por `scripts/vocabulario-tabla.py`. "
        "No se edita a mano: se edita ese fichero y se regenera. El test de arquitectura `VocabularioDePantallaTests` "
        "aplica estas mismas reglas a los valores de los `.resx` (es-ES y ca-ES), al texto visible de los `.razor` y a los "
        "mensajes de `Error.Crear` y `.WithMessage` del código de `src` (Application, Infrastructure y Web).",
        "",
        "**Términos canónicos de pantalla**",
        "",
        "| Término canónico | Plural | Formas cortas permitidas | Significado | Contrato | No confundir con |",
        "|---|---|---|---|---|---|",
    ]
    for c in datos["canonicos"]:
        salida.append(fila(_Cruda(f"**{celda(c['forma'])}**"), c.get("plural"), lista(c.get("formasCortasPermitidas")),
                           c["significado"], c["contrato"], c.get("noConfundirCon")))

    salida += [
        "",
        "**Términos que la pantalla no dice a secas**",
        "",
        "| Prohibido a secas | Se sustituye por | Por qué | Sentido que cubre | Contrato |",
        "|---|---|---|---|---|",
    ]
    for p in datos["prohibidos"]:
        salida.append(fila(_Cruda(f"«{celda(p['termino'])}»"), p["sustitucion"], p["motivo"], p.get("sentido"), p["contrato"]))

    salida += [
        "",
        "**Excepciones por contexto** (el término prohibido SÍ puede aparecer ahí; cada una lleva su motivo)",
        "",
    ]
    if datos["excepciones"]:
        salida += [
            "| Contexto | Excepción | Términos que deja pasar | Dónde | Motivo |",
            "|---|---|---|---|---|",
        ]
        for e in datos["excepciones"]:
            donde = "; ".join(f"`{f}`" for f in e["ficheros"])
            if e.get("claves"):
                donde += " — claves: " + ", ".join(f"`{k}`" for k in e["claves"])
            terminos = ", ".join(f"«{por_id[i]['termino']}»" if i in por_id else f"«{i}»" for i in e["prohibidos"])
            salida.append(fila(e["contexto"], _Cruda(f"`{celda(e['id'])}`"), terminos, _Cruda(donde.replace("|", "\\|")), e["motivo"]))
    else:
        salida.append("Ninguna.")

    return "\n".join(salida) + "\n"


def partir(texto: str):
    """Devuelve (antes, bloque_actual, despues) respecto de las marcas; lanza si faltan, están repetidas o desordenadas."""
    if texto.count(MARCA_INICIO) != 1 or texto.count(MARCA_FIN) != 1:
        raise EntradaIncorrecta(
            f"el documento debe tener exactamente una marca «{MARCA_INICIO}» y una «{MARCA_FIN}» "
            f"(tiene {texto.count(MARCA_INICIO)} y {texto.count(MARCA_FIN)})")
    i = texto.index(MARCA_INICIO)
    f = texto.index(MARCA_FIN)
    if f < i:
        raise EntradaIncorrecta("la marca de fin está antes que la de inicio")
    return texto[:i + len(MARCA_INICIO)], texto[i + len(MARCA_INICIO):f], texto[f:]


def con_su_fin_de_linea(bloque: str, destino: str) -> str:
    """El bloque con el fin de línea del documento destino (CRLF si el documento lo usa)."""
    crlf = "\r\n" in destino
    return bloque.replace("\n", "\r\n") if crlf else bloque


def bloque_para(documento: str, datos: dict) -> str:
    return con_su_fin_de_linea("\n" + generar(datos), documento)


def aplicar(ruta: Path, datos: dict) -> bool:
    """Reescribe lo que hay entre las marcas. Devuelve True si cambió algo. Lo de fuera no se toca ni un byte."""
    original = leer_sin_normalizar(ruta)
    antes, actual, despues = partir(original)
    nuevo = bloque_para(original, datos)
    if actual == nuevo:
        return False
    with open(ruta, "w", encoding="utf-8", newline="") as f:
        f.write(antes + nuevo + despues)
    return True


def verificar(ruta: Path, datos: dict) -> list[str]:
    original = leer_sin_normalizar(ruta)
    _, actual, _ = partir(original)
    esperado = bloque_para(original, datos)
    if actual == esperado:
        return []
    return list(difflib.unified_diff(actual.splitlines(), esperado.splitlines(), "contrato (actual)", "Vocabulario.json (esperado)", lineterm="", n=0))


def leer_sin_normalizar(ruta: Path) -> str:
    try:
        with open(ruta, "r", encoding="utf-8", newline="") as f:
            return f.read()
    except FileNotFoundError:
        raise EntradaIncorrecta(f"no existe {ruta}")


def descartar(datos: dict, texto: str) -> str:
    """Lo que no es lenguaje de pantalla (una URL de ejemplo) se descarta antes de casar, como hace el test de arquitectura."""
    for patron in datos.get("descartarAntesDeCasar", []):
        texto = re.sub(patron, " ", texto)
    return texto


def comprobar_ejemplos(datos: dict) -> list[str]:
    fallos = []
    for p in datos["prohibidos"]:
        try:
            regex = re.compile(p["patron"], re.IGNORECASE if p.get("ignorarMayusculas") else 0)
        except re.error as e:
            fallos.append(f"{p['id']}: el patrón no compila en Python: {e}")
            continue
        if not p.get("casa"):
            fallos.append(f"{p['id']}: sin ejemplos que deba cazar (control positivo)")
        if not p.get("noCasa"):
            fallos.append(f"{p['id']}: sin ejemplos legítimos que no deba cazar")
        for ejemplo in p.get("casa", []):
            if not regex.search(descartar(datos, ejemplo)):
                fallos.append(f"{p['id']}: debía cazar «{ejemplo}» y no lo caza")
        for ejemplo in p.get("noCasa", []):
            if regex.search(descartar(datos, ejemplo)):
                fallos.append(f"{p['id']}: no debía cazar «{ejemplo}» y lo caza")
    return fallos


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--json", type=Path, default=RUTA_JSON)
    sub = ap.add_subparsers(dest="orden", required=True)
    sub.add_parser("generar")
    sub.add_parser("ejemplos")
    for nombre in ("aplicar", "verificar"):
        s = sub.add_parser(nombre)
        s.add_argument("documento", type=Path)
    args = ap.parse_args(argv)
    # La salida es siempre UTF-8 con LF (en Windows el valor por defecto es la página de códigos local).
    sys.stdout.reconfigure(encoding="utf-8", newline="\n")
    sys.stderr.reconfigure(encoding="utf-8")

    try:
        datos = cargar(args.json)
        if args.orden == "generar":
            sys.stdout.reconfigure(encoding="utf-8", newline="\n")
            sys.stdout.write(generar(datos))
            return 0
        if args.orden == "ejemplos":
            fallos = comprobar_ejemplos(datos)
            for f in fallos:
                print(f"FALLO {f}")
            print(f"{len(datos['prohibidos'])} términos prohibidos, {len(fallos)} fallos")
            return 1 if fallos else 0
        if args.orden == "aplicar":
            cambio = aplicar(args.documento, datos)
            print(f"{args.documento}: {'actualizado' if cambio else 'sin cambios'}")
            return 0
        diferencias = verificar(args.documento, datos)
        if diferencias:
            print(f"{args.documento}: el bloque generado NO coincide con Vocabulario.json. Regenera con: "
                  f"python3 scripts/vocabulario-tabla.py aplicar {args.documento}")
            for linea in diferencias[:40]:
                print(linea)
            return 1
        print(f"{args.documento}: el bloque generado coincide con Vocabulario.json")
        return 0
    except EntradaIncorrecta as e:
        print(f"ERROR {e}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
