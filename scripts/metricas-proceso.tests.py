#!/usr/bin/env python3
"""Pruebas de scripts/metricas-proceso.py (S15), con datos sintéticos de resultado conocido.

La propiedad que importa no es «el guion corre»: es que cada métrica reproduce la cifra exacta de un
caso construido a mano, que una fuente ausente se dice NO_MEDIDA (nunca un 0), y que el guion no cuenta
lo que no debe (una mención fuera de la primera sección, un comentario Razor, un <BotonPlus>).

Se ejecuta con scripts/metricas-proceso.tests.sh, que además lo lanza contra copias MUTADAS del guion
y exige que fallen.  METRICAS_SCRIPT apunta al guion bajo prueba (por defecto, el de al lado).
"""
import datetime as dt
import importlib.util
import json
import os
import stat
import sys
import tempfile
import unittest
from pathlib import Path

RUTA = Path(os.environ.get("METRICAS_SCRIPT", Path(__file__).resolve().parent / "metricas-proceso.py"))
spec = importlib.util.spec_from_file_location("metricas_proceso", RUTA)
mp = importlib.util.module_from_spec(spec)
sys.modules["metricas_proceso"] = mp
spec.loader.exec_module(mp)


def escribir(raiz: Path, ruta: str, texto: str):
    p = raiz / ruta
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(texto, encoding="utf-8")


INFORME = """# Recorrido
## 5. Defectos (tanda 1)
| ID | Sev. | Pantalla | Observado |
|---|---|---|---|
| D-01 | ALTA | Documentos | algo |
| D-02 | MEDIA | Documentos | otro |
| D-03 | BAJA | Mi trabajo | otro |
## 6. Defectos (Tanda 2)
| ID | Pantalla | Sev. | Descripción |
|---|---|---|---|
| D-12 | Menú | ALTA | x |
| D-13 | Visión | MEDIA | y |
| D-14 | Búsqueda | MEDIA | z |
| D-99 | Fila rara | SIN_SEVERIDAD | ignorada |
"""


class M1(unittest.TestCase):
    def test_cuenta_por_severidad_y_por_tanda_aunque_la_columna_cambie_de_sitio(self):
        ids, sev, por_tanda = mp.m1_defectos(INFORME)
        self.assertEqual(ids, ["D-01", "D-02", "D-03", "D-12", "D-13", "D-14"])
        self.assertEqual(sev, {"ALTA": 2, "MEDIA": 3, "BAJA": 1})
        self.assertEqual(por_tanda, {"1": 3, "2": 3})

    def test_sin_informe_es_NO_MEDIDA_no_cero(self):
        with tempfile.TemporaryDirectory() as t:
            f, ids = mp.metrica_m1(Path(t))
        self.assertEqual(f["clase"], "NO_MEDIDA")
        self.assertEqual(ids, [])

    def test_informe_sin_filas_es_NO_MEDIDA(self):
        with tempfile.TemporaryDirectory() as t:
            escribir(Path(t), mp.INFORME_RECORRIDO, "# vacio\n")
            f, _ = mp.metrica_m1(Path(t))
        self.assertEqual(f["clase"], "NO_MEDIDA")


def pr(n, titulo, cuerpo="", creado="2026-10-01T00:00:00Z", fusionado="2026-10-01T01:00:00Z", ficheros=3, rama=None, autor="humano"):
    return {"number": n, "title": titulo, "body": cuerpo, "createdAt": creado, "mergedAt": fusionado,
            "changedFiles": ficheros, "headRefName": rama or f"rama-{n}", "author": {"login": autor}}


class M2M3M9(unittest.TestCase):
    def test_M2_cuenta_defectos_con_dos_o_mas_PR_y_solo_mira_titulo_y_primera_seccion(self):
        prs = [
            pr(1, "fix: D-07", "## Defectos\nD-07 y D-08\n## Evidencia\nsin nada"),
            pr(2, "fix: otra cosa", "## Defectos\nD-07 de nuevo\n## Revisión\nD-08 mencionado en la 2.ª sección"),
            pr(3, "fix: D-09", "## Defectos\nD-09\n"),
        ]
        mult, denom, lista, distintos = mp.calcular_m2(prs, 33)
        # D-07 en #1 y #2 (2 PR). D-08 en #1; en #2 solo aparece tras el 2.º encabezado: no cuenta.
        self.assertEqual((mult, denom, lista, distintos), (1, 33, ["D-07"], 3))

    def test_M2_sin_total_usa_los_defectos_distintos_citados(self):
        prs = [pr(1, "D-01"), pr(2, "D-01"), pr(3, "D-02")]
        mult, denom, _, distintos = mp.calcular_m2(prs, None)
        self.assertEqual((mult, denom, distintos), (1, 2, 2))

    def test_M3_distingue_alto_medio_y_sin_seccion(self):
        prs = [
            pr(1, "a", "## Revisión Codex\nHallazgo alto: fuga entre Tenants"),
            pr(2, "b", "## Revisión Codex\nUn hallazgo medio, corregido"),
            pr(3, "c", "## Revisión Codex\nHallazgos bajos: ninguno relevante"),
            pr(4, "d", "## Otra cosa\nalto"),
        ]
        self.assertEqual(mp.calcular_m3(prs), (3, 2, 1))

    def test_M3_la_negacion_de_una_gravedad_no_es_un_hallazgo(self):
        prs = [
            pr(1, "a", "## Revisión Codex\nSin hallazgos altos. Medio 1 aceptado."),
            pr(2, "b", "## Revisión Codex\nSin hallazgos altos ni medios; bajos corregidos."),
            pr(3, "c", "## Revisión Codex\nNingún alto, 0 medios."),
            pr(4, "d", "## Revisión Codex\nSin hallazgos altos. Alta: fuga entre Tenants."),
        ]
        # #1 medio (no alto); #2 y #3 ninguno; #4 alto (la negación solo cubre su frase)
        self.assertEqual(mp.calcular_m3(prs), (4, 2, 1))

    def test_M3_la_negacion_admite_la_barra_como_separador(self):
        prs = [pr(1, "a", "## Revisión Codex\nSin ALTA/MEDIA; solo bajos.")]
        self.assertEqual(mp.calcular_m3(prs), (1, 0, 0))

    def test_M3_la_seccion_termina_en_el_siguiente_encabezado(self):
        prs = [pr(1, "a", "## Revisión Codex\nsin hallazgos\n## Huecos\nhallazgo alto en otra sección")]
        self.assertEqual(mp.calcular_m3(prs), (1, 0, 0))

    def test_M9_continuaciones_por_titulo(self):
        prs = [pr(1, "fix(ux): restos de D-30"), pr(2, "feat: nueva cosa"), pr(3, "feat: Nueva visita (2ª parte)"), pr(4, "fix: residuos")]
        self.assertEqual(mp.calcular_m9(prs), [1, 3, 4])


class CI(unittest.TestCase):
    def test_M4_M5_por_evento_rama_y_lote(self):
        def run(i, ev, rama, conc):
            return {"databaseId": i, "event": ev, "headBranch": rama, "conclusion": conc, "createdAt": "2026-10-01T00:00:00Z"}
        runs = [run(1, "pull_request", "a", "success"), run(2, "pull_request", "a", "failure"), run(3, "pull_request", "b", "success"),
                run(4, "merge_group", "q1", "success"), run(5, "merge_group", "q2", "failure"),
                run(6, "push", "main", "failure"), run(7, "push", "main", "success")]
        por_evento, ramas, lote = mp.calcular_ci(runs, {"a"})
        self.assertEqual(por_evento["pull_request"], (1, 3))
        self.assertEqual(por_evento["merge_group"], (1, 2))
        self.assertEqual(ramas, (1, 2))          # ramas con >=1 rojo / ramas
        self.assertEqual(lote, (1, 2, 1))        # rojos del lote, runs del lote, ramas del lote

    def test_gh_que_falla_es_NO_MEDIDA_con_motivo_no_cero(self):
        with tempfile.TemporaryDirectory() as t:
            falso = Path(t) / ("gh_falso.bat" if os.name == "nt" else "gh_falso")
            falso.write_text("@echo off\r\nexit /b 1\r\n" if os.name == "nt" else "#!/bin/sh\necho 'rate limit' >&2\nexit 1\n")
            falso.chmod(falso.stat().st_mode | stat.S_IEXEC)
            os.environ["METRICAS_GH"] = str(falso)
            try:
                class A:  # argumentos mínimos
                    limite_runs = 10
                filas = mp.metricas_de_ci(A, set())
            finally:
                del os.environ["METRICAS_GH"]
        self.assertTrue(all(f["clase"] == "NO_MEDIDA" and f["valor"] == "NO_MEDIDA" for f in filas))
        self.assertEqual({f["id"] for f in filas}, {"M4", "M5"})
        self.assertTrue(all("salió con 1" in f["detalle"] for f in filas), "el motivo debe ser el fallo de gh, no «0 runs»")


class Etiquetas(unittest.TestCase):
    def test_no_se_cierra_en_una_flecha_ni_en_una_cadena_y_no_casa_por_prefijo(self):
        texto = (
            '<Boton Variante="Primario" OnClick="@(() => Guardar())">Guardar</Boton>\n'
            '<Boton\n   Deshabilitado="@(x > 3)"\n   title="a > b" />\n'
            '<BotonPlus Variante="X" />\n'
            '<Boton OnClick="Algo">Otro</Boton>\n'
            '<Boton Deshabilitado=@(x > 3) Variante="Primario" />\n'
        )
        tags = mp.etiquetas(texto, "Boton")
        self.assertEqual(len(tags), 4)
        self.assertIn("Variante", tags[3], "un > dentro de @( ) sin comillas no cierra la etiqueta")
        self.assertIn("Deshabilitado", tags[1])
        self.assertIn('title="a > b"', tags[1])
        self.assertFalse(any("BotonPlus" in t for t in tags))

    def test_un_comentario_razor_no_cuenta(self):
        sin = mp.quitar_comentarios_razor('@* <Boton /> *@ <!-- <Boton /> --> <Boton />')
        self.assertEqual(len(mp.etiquetas(sin, "Boton")), 1)


class Repo(unittest.TestCase):
    def arbol(self, raiz: Path):
        escribir(raiz, "src/CaeManager.Web/A.razor",
                 '<Boton Variante="Primario" OnClick="@(() => X())">a</Boton>\n'
                 '<Boton OnClick="x">b</Boton>\n'
                 '<Boton Deshabilitado="true" Variante="Secundario">c</Boton>\n'
                 '<Boton Deshabilitado="true" Motivo="porque" Variante="Secundario">d</Boton>\n'
                 '@* <Boton /> comentado *@\n'
                 '<div role="alert">uno</div><div role="alert">dos</div>\n'
                 '<p>Cliente a secas</p><p>Cliente empresarial no cuenta</p>\n'
                 '@code { string t = "Cliente en C# no cuenta"; }\n')
        escribir(raiz, "src/CaeManager.Web/B.razor", '<Drawer><CampoTexto /></Drawer>\n')
        escribir(raiz, "src/CaeManager.Web/C.razor", '<Drawer HayCambios="x"><CampoTexto /></Drawer>\n')
        escribir(raiz, "src/CaeManager.Web/D.razor", '<Modal><p>sin campos</p></Modal>\n')
        escribir(raiz, "src/CaeManager.Web/Recursos/T.resx",
                 '<root><data name="K"><value>Cliente suelto y otro Cliente</value></data></root>')
        escribir(raiz, "src/CaeManager.Web/Recursos/T.ca-ES.resx", '<root><data name="K"><value>Cliente</value></data></root>')
        escribir(raiz, "src/CaeManager.Web/Migrations/M.razor", '<Boton />')
        escribir(raiz, "tests/CaeManager.E2ETests/X.cs",
                 'p.GetByText("a"); p.GetByText("b"); l.First.ClickAsync(); l.Nth(2); var x = lista.First(); p.GetByTestId("z");')
        escribir(raiz, "tests/CaeManager.Architecture.Tests/UnoTests.cs", "[Fact]\npublic void A() {}\n[Theory]\n[InlineData(1)]\npublic void B(int x) {}\n")
        escribir(raiz, "tests/CaeManager.Architecture.Tests/TerminologiaCanonicaTests.cs",
                 'class T {\n    private static readonly Dictionary<string, int> Congelado = new()\n    {\n        ["Hydra"] = 10,\n        ["Delegacion"] = 340,\n    };\n}\n')
        escribir(raiz, "tests/CaeManager.Architecture.Tests/Congelados/ClienteId-ubicaciones.txt",
                 "a.cs :: ClienteId = 3\nb.cs :: clienteId = 2\nb.cs :: ClienteId = 1\n")

    def filas(self):
        with tempfile.TemporaryDirectory() as t:
            raiz = Path(t)
            self.arbol(raiz)
            return {f["id"]: f for f in mp.metricas_de_repo(raiz)}

    def test_M11_M12_botones_sin_variante_y_deshabilitados_sin_motivo(self):
        f = self.filas()
        self.assertEqual(f["M11"]["valor"], "1 de 4")     # los 4 de A.razor (el comentado y el de Migrations no cuentan), 1 sin Variante
        self.assertEqual(f["M12"]["valor"], "1 de 2")     # 2 deshabilitados, 1 sin Motivo

    def test_M13_drawer_con_campos_sin_guardian(self):
        self.assertEqual(self.filas()["M13"]["valor"], "1 de 2")

    def test_M14_role_alert(self):
        self.assertEqual(self.filas()["M14"]["valor"], "1 ficheros / 2")

    def test_M15_locators_e2e_sin_contar_el_First_de_LINQ(self):
        v = self.filas()["M15"]["valor"]
        self.assertEqual(v, "GetByText 2 + posicionales 2; GetByTestId 1")

    def test_M16_cliente_a_secas_en_resx_y_marcado_pero_no_en_codigo_ni_el_satelite(self):
        self.assertEqual(self.filas()["M16"]["valor"], "2 valores .resx + 1 en marcado")

    def test_M16_toma_el_patron_de_Vocabulario_json_cuando_existe(self):
        # Con S1 en el árbol, la cifra la manda el patrón del JSON y no el regex local: aquí, uno que SOLO caza «suelto».
        with tempfile.TemporaryDirectory() as t:
            raiz = Path(t)
            self.arbol(raiz)
            vocab = {"prohibidos": [{"id": "cliente-a-secas", "patron": r"\bsuelto\b", "ignorarMayusculas": True}]}
            escribir(raiz, "tests/CaeManager.Architecture.Tests/Vocabulario/Vocabulario.json", json.dumps(vocab))
            f = {x["id"]: x for x in mp.metricas_de_repo(raiz)}["M16"]
        self.assertEqual(f["valor"], "1 valores .resx + 0 en marcado")
        self.assertIn("Vocabulario.json", f["detalle"])

    def test_M16_con_Vocabulario_json_ilegible_es_NO_MEDIDA_y_no_un_cero(self):
        with tempfile.TemporaryDirectory() as t:
            raiz = Path(t)
            self.arbol(raiz)
            escribir(raiz, "tests/CaeManager.Architecture.Tests/Vocabulario/Vocabulario.json", "{ roto")
            f = {x["id"]: x for x in mp.metricas_de_repo(raiz)}["M16"]
        self.assertEqual(f["clase"], "NO_MEDIDA")

    def test_M17_M18_M21_leen_los_trinquetes(self):
        f = self.filas()
        self.assertEqual(f["M17"]["valor"], "Hydra 10 · Delegacion 340")
        self.assertEqual(f["M18"]["valor"], "6 apariciones / 2 ficheros")
        self.assertEqual(f["M21"]["valor"], "2 ficheros / 2 métodos [Fact]/[Theory]")
        self.assertEqual(f["M20"]["clase"], "NO_MEDIDA")

    def test_directorios_existentes_pero_vacios_dan_NO_MEDIDA_no_cero(self):
        with tempfile.TemporaryDirectory() as t:
            raiz = Path(t)
            (raiz / "src").mkdir()
            (raiz / "tests" / "CaeManager.E2ETests").mkdir(parents=True)
            (raiz / "tests" / "CaeManager.Architecture.Tests").mkdir(parents=True)
            escribir(raiz, "tests/CaeManager.Architecture.Tests/Congelados/ClienteId-ubicaciones.txt", "basura sin el formato esperado\n")
            f = {x["id"]: x for x in mp.metricas_de_repo(raiz)}
        for m in ("M11", "M12", "M13", "M14", "M15", "M16", "M18", "M21"):
            self.assertEqual(f[m]["clase"], "NO_MEDIDA", m)
            self.assertEqual(f[m]["valor"], "NO_MEDIDA", m)

    def test_sin_arbol_las_filas_son_NO_MEDIDA_no_cero(self):
        with tempfile.TemporaryDirectory() as t:
            f = {x["id"]: x for x in mp.metricas_de_repo(Path(t))}
        for m in ("M11", "M12", "M15", "M17", "M18", "M19", "M21"):
            self.assertEqual(f[m]["clase"], "NO_MEDIDA", m)


STUB_GH = '''
import json, os, sys
d = os.environ["METRICAS_FIXTURES"]
a = sys.argv[1:]
def sirve(nombre):
    print(open(os.path.join(d, nombre), encoding="utf-8").read())
    sys.exit(0)
if a[:2] == ["pr", "list"]:
    sirve("pr_list.json")
if a[:2] == ["run", "list"]:
    sirve("run_list.json")
if a[:2] == ["repo", "view"]:
    sirve("repo.json")
if a[:1] == ["api"] and "/pulls/" in a[1] and a[1].endswith("/files"):
    sirve("files_" + a[1].split("/pulls/")[1].split("/")[0] + ".json")
sys.stderr.write("stub gh: no reconocido: " + " ".join(a))
sys.exit(1)
'''


class ConGhFalso(unittest.TestCase):
    """metricas_de_pr y metricas_de_ci con un `gh` falso de resultado conocido: M2, M5, M6, M7, M8, M9, M10."""

    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.dir = Path(self._tmp.name)
        (self.dir / "stub_gh.py").write_text(STUB_GH, encoding="utf-8")
        os.environ["METRICAS_GH"] = f'"{sys.executable}" "{self.dir / "stub_gh.py"}"'
        os.environ["METRICAS_FIXTURES"] = str(self.dir)

    def tearDown(self):
        os.environ.pop("METRICAS_GH", None)
        os.environ.pop("METRICAS_FIXTURES", None)
        self._tmp.cleanup()

    def sirve(self, nombre, datos):
        (self.dir / nombre).write_text(json.dumps(datos), encoding="utf-8")

    def args(self, **kw):
        class A:
            pr_min = 10
            pr_max = 12
            desde = "2026-09-12"
            limite_pr = 100
            limite_runs = 100
        for k, v in kw.items():
            setattr(A, k, v)
        return A

    def prs(self):
        def p(n, titulo, cuerpo, creado, minutos_hasta_fusion, ficheros, autor="humano"):
            c = dt.datetime.fromisoformat(creado.replace("Z", "+00:00"))
            f = (c + dt.timedelta(minutes=minutos_hasta_fusion)).isoformat().replace("+00:00", "Z")
            return {"number": n, "title": titulo, "body": cuerpo, "createdAt": creado, "mergedAt": f,
                    "changedFiles": ficheros, "headRefName": f"r{n}", "author": {"login": autor}}
        return [
            p(5, "viejo", "", "2026-09-01T00:00:00Z", 600, 1),                     # antes de --desde (desplazaría las medianas)
            p(8, "bump", "", "2026-09-20T00:00:00Z", 10, 2, autor="dependabot[bot]"),
            p(9, "fuera del lote", "", "2026-09-20T00:00:00Z", 30, 5),
            p(10, "fix D-01", "## Defectos\nD-01\n## Revisión Codex\nSin hallazgos altos/medios.", "2026-10-01T00:00:00Z", 60, 3),
            p(11, "fix D-01 resto", "## Defectos\nD-01\n## Revisión Codex\nMedio 1 corregido.", "2026-10-01T00:00:00Z", 30, 5),
            p(12, "feat D-02", "## Defectos\nD-02\n## Revisión Codex\nAlta: fuga.", "2026-10-01T00:00:00Z", 120, 10),
            p(13, "fuera del lote por arriba, resto D-01", "## Defectos\nD-01\n", "2026-10-01T00:00:00Z", 10, 4),
        ]

    def preparar(self):
        self.sirve("repo.json", {"nameWithOwner": "o/r"})
        self.sirve("pr_list.json", self.prs())
        self.sirve("files_10.json", [{"filename": f} for f in "abc"])
        self.sirve("files_11.json", [{"filename": f} for f in "bdefg"])
        self.sirve("files_12.json", [{"filename": f} for f in "ahijklmnop"])

    def valores(self, **kw):
        self.preparar()
        filas, lote = mp.metricas_de_pr("o/r", self.args(**kw), 33)
        return {f["id"]: f for f in filas}, lote

    def test_M2_M3_M6_M7_M8_M9_M10_con_cifras_conocidas(self):
        f, lote = self.valores()
        self.assertEqual([p["number"] for p in lote], [10, 11, 12])
        self.assertEqual(f["M2"]["valor"], "1 de 33")                      # D-01 está en #10 y #11
        self.assertEqual(f["M3"]["valor"], "2 de 3 (67 %); altos 1")       # #10 «sin hallazgos altos/medios» no cuenta
        self.assertEqual(f["M6"]["valor"], "9.0 toques (8.0 únicos)")      # (3+5+10)/2 defectos; 16 únicos/2
        self.assertEqual(f["M7"]["valor"], "5 (lote 5)")                   # medianas de [5,3,5,10,4] y [3,5,10]
        self.assertEqual(f["M8"]["valor"], "mediana 30 min (lote 60)")     # [30,60,30,120,10] y [60,30,120]
        self.assertEqual(f["M9"]["valor"], "1 de 3 (33 %)")                # solo #11 dice «resto»
        self.assertEqual(f["M10"]["valor"], "2 de 16 (12 %)")              # a y b se repiten; 16 ficheros distintos

    def test_M7_M8_excluyen_dependabot_y_lo_anterior_a_desde(self):
        f, _ = self.valores()
        self.assertIn("5 PR no-Dependabot desde 2026-09-12", f["M7"]["detalle"])

    def test_el_tope_de_limite_pr_se_avisa_en_vez_de_recortar_en_silencio(self):
        # gh devuelve exactamente 7 (el tope) y la más antigua es la #5: si el lote empezara en #3, estaría recortado
        f, _ = self.valores(limite_pr=7, pr_min=3)
        self.assertIn("ATENCIÓN", f["M2"]["detalle"])
        # con el tope alcanzado pero el lote (#10..#12) bien dentro de lo leído, no hay nada que avisar
        f, _ = self.valores(limite_pr=7)
        self.assertNotIn("ATENCIÓN", f["M2"]["detalle"])
        f, _ = self.valores(limite_pr=100, pr_min=3)
        self.assertNotIn("ATENCIÓN", f["M2"]["detalle"])

    def test_el_tope_que_corta_la_ventana_desde_se_avisa_en_M7_y_M8(self):
        f, _ = self.valores(limite_pr=7, desde="2026-08-01")      # la más antigua leída es de 2026-09-01
        self.assertIn("ATENCIÓN", f["M7"]["detalle"])
        self.assertIn("ATENCIÓN", f["M8"]["detalle"])
        f, _ = self.valores(limite_pr=7)                            # desde 2026-09-12: cubierto
        self.assertNotIn("ATENCIÓN", f["M7"]["detalle"])

    def test_comparar_lee_una_linea_base_con_BOM_de_PowerShell(self):
        import contextlib
        import io
        with tempfile.TemporaryDirectory() as t:
            raiz = Path(t)
            Repo().arbol(raiz)
            base = raiz / "base.json"
            base.write_bytes(b"\xef\xbb\xbf" + json.dumps({"ventana_lote": [1015, 1036], "metricas": []}).encode("utf-8"))
            with contextlib.redirect_stdout(io.StringIO()):
                rc = mp.main(["--solo", "repo", "--raiz", str(raiz), "--negocio", str(raiz / "no"), "--comparar", str(base)])
        self.assertEqual(rc, 0)

    def test_una_linea_base_de_otra_ventana_avisa_de_que_no_es_comparable(self):
        import contextlib
        import io
        with tempfile.TemporaryDirectory() as t:
            raiz = Path(t)
            Repo().arbol(raiz)
            base = raiz / "base.json"
            base.write_text(json.dumps({"ventana_lote": [1, 2], "metricas": []}), encoding="utf-8")
            err = io.StringIO()
            with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(err):
                mp.main(["--solo", "repo", "--raiz", str(raiz), "--negocio", str(raiz / "no"), "--comparar", str(base)])
        self.assertIn("no son comparables", err.getvalue())

    def test_sin_PR_en_la_ventana_es_NO_MEDIDA_no_cero(self):
        f, lote = self.valores(pr_min=900, pr_max=910)
        self.assertEqual(lote, [])
        for m in ("M2", "M3", "M6", "M9", "M10"):
            self.assertEqual(f[m]["clase"], "NO_MEDIDA", m)

    def test_M5_cuenta_los_rojos_de_push_a_main_y_solo_ellos(self):
        def run(i, ev, rama, conc):
            return {"databaseId": i, "event": ev, "headBranch": rama, "conclusion": conc, "createdAt": "2026-10-01T00:00:00Z"}
        self.sirve("run_list.json", [run(1, "push", "main", "success"), run(2, "push", "main", "failure"), run(3, "push", "main", "success"),
                                     run(4, "push", "otra", "failure"), run(5, "pull_request", "main", "failure")])
        filas = {f["id"]: f for f in mp.metricas_de_ci(self.args(), set())}
        self.assertEqual(filas["M5"]["valor"], "1 de 3 (33.3 %)")
        self.assertIn("re-run", filas["M4"]["detalle"])

    def test_M5_sin_pushes_a_main_es_NO_MEDIDA(self):
        self.sirve("run_list.json", [{"databaseId": 1, "event": "pull_request", "headBranch": "x", "conclusion": "success", "createdAt": "2026-10-01T00:00:00Z"}])
        self.assertEqual({f["id"]: f for f in mp.metricas_de_ci(self.args(), set())}["M5"]["clase"], "NO_MEDIDA")


class Util(unittest.TestCase):
    def test_percentil(self):
        self.assertEqual(mp.percentil([1, 2, 3, 4, 5], 50), 3)
        self.assertEqual(mp.percentil([10], 90), 10)
        self.assertIsNone(mp.percentil([], 50))

    def test_minutos(self):
        self.assertEqual(mp.minutos("2026-10-01T00:00:00Z", "2026-10-01T01:30:00Z"), 90)

    def test_comparar_pone_antes_y_delta(self):
        filas = [{"id": "M7", "valor": "8", "numerico": 8}, {"id": "M9", "valor": "NO_MEDIDA", "numerico": None}]
        base = {"metricas": [{"id": "M7", "valor": "10", "numerico": 10}, {"id": "M9", "valor": "3", "numerico": 3}]}
        r = mp.comparar(filas, base)
        self.assertEqual((r[0]["antes"], r[0]["delta"]), ("10", -2))
        self.assertEqual(r[1]["antes"], "3")
        self.assertNotIn("delta", r[1])

    def test_main_solo_repo_json(self):
        import contextlib
        import io
        with tempfile.TemporaryDirectory() as t:
            raiz = Path(t)
            Repo().arbol(raiz)
            buf = io.StringIO()
            with contextlib.redirect_stdout(buf):
                rc = mp.main(["--solo", "repo", "--raiz", str(raiz), "--negocio", str(raiz / "no-existe"), "--json"])
        self.assertEqual(rc, 0)
        datos = json.loads(buf.getvalue())
        ids = [m["id"] for m in datos["metricas"]]
        self.assertEqual(ids, sorted(ids, key=lambda i: int(i[1:])))
        self.assertIn("M11", ids)
        self.assertNotIn("M4", ids)   # --solo repo no toca GitHub


if __name__ == "__main__":
    unittest.main(verbosity=2)
