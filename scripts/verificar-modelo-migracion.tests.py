import importlib.util
import os
import pathlib
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

SCRIPT = pathlib.Path(__file__).with_name("verificar-modelo-migracion.py")
spec = importlib.util.spec_from_file_location("guarda", SCRIPT)
guarda = importlib.util.module_from_spec(spec)
spec.loader.exec_module(guarda)


def modelo(metodo, contenido):
    return f'partial class NombreGenerado {{ protected override void {metodo}(ModelBuilder modelBuilder) {{ {contenido} }} }}'


class GuardaTests(unittest.TestCase):
    def test_cuerpos_ignoran_clase_formato_y_comentarios(self):
        designer = modelo("BuildTargetModel", 'modelBuilder.X("a b"); // comentario\n')
        snapshot = modelo("BuildModel", 'modelBuilder . X ( "a b" ) ;')
        self.assertEqual(guarda.cuerpo(designer, "BuildTargetModel"), guarda.cuerpo(snapshot, "BuildModel"))

    def test_literales_y_llaves_en_literales_se_conservan(self):
        a = modelo("BuildTargetModel", 'modelBuilder.X("{ a b }");')
        b = modelo("BuildModel", 'modelBuilder.X("{ ab }");')
        self.assertNotEqual(guarda.cuerpo(a, "BuildTargetModel"), guarda.cuerpo(b, "BuildModel"))

    def test_llaves_sin_pareja_en_literales_y_comentarios_no_cierran_el_cuerpo(self):
        contenido = modelo("BuildModel", 'b.X("{"); b.Y(\'}\'); b.Z(@"}""{"); // }\n b.Final();')
        tokens = guarda.cuerpo(contenido, "BuildModel")
        self.assertIn('"{"', tokens)
        self.assertIn("'}'", tokens)
        self.assertIn('@"}""{"', tokens)
        self.assertIn("Final", tokens)

    def test_product_version_no_es_modelo(self):
        a = modelo("BuildTargetModel", 'modelBuilder.HasAnnotation("ProductVersion", "10.0.14");')
        b = modelo("BuildModel", 'modelBuilder.HasAnnotation("ProductVersion", "10.0.13");')
        self.assertEqual(guarda.cuerpo(a, "BuildTargetModel"), guarda.cuerpo(b, "BuildModel"))

    def test_otra_anotacion_si_es_modelo(self):
        a = modelo("BuildTargetModel", 'modelBuilder.HasAnnotation("Relational:MaxIdentifierLength", "63");')
        b = modelo("BuildModel", 'modelBuilder.HasAnnotation("Relational:MaxIdentifierLength", "64");')
        self.assertNotEqual(guarda.cuerpo(a, "BuildTargetModel"), guarda.cuerpo(b, "BuildModel"))

    def test_fin_de_linea_del_blob_no_cuenta(self):
        a = modelo("BuildTargetModel", 'modelBuilder.X(@"uno\r\ndos");')
        b = modelo("BuildModel", 'modelBuilder.X(@"uno\ndos");')
        self.assertEqual(guarda.cuerpo(a, "BuildTargetModel"), guarda.cuerpo(b, "BuildModel"))

    def test_metodo_incompleto_falla(self):
        with self.assertRaises(ValueError):
            guarda.cuerpo('protected override void BuildModel(ModelBuilder b) { b.X();', "BuildModel")

    def ejecutar(self, base, head, snapshot, designer=""):
        with patch.object(guarda, "migraciones", side_effect=[base, head]), patch.object(guarda, "git", side_effect=[designer, snapshot]) as git:
            guarda.comprobar("base", "head")
            return git

    def test_solo_compara_ultima_migracion_nueva(self):
        ultimo = modelo("BuildTargetModel", 'modelBuilder.X("ultimo");')
        snapshot = modelo("BuildModel", 'modelBuilder.X("ultimo");')
        self.ejecutar({}, {"20270101000000_A": "a", "20270102000000_B": "b"}, snapshot, ultimo)

    def test_detecta_diferencia_en_ultima_migracion(self):
        designer = modelo("BuildTargetModel", 'modelBuilder.X("anterior");')
        snapshot = modelo("BuildModel", 'modelBuilder.X("nuevo");')
        with self.assertRaisesRegex(ValueError, "no coincide"):
            self.ejecutar({}, {"20270101000000_A": "a.Designer.cs"}, snapshot, designer)

    def test_error_git_no_se_interpreta_como_ausencia(self):
        with patch.object(guarda.subprocess, "run", return_value=subprocess.CompletedProcess([], 128, b"", b"error")):
            with self.assertRaisesRegex(ValueError, "No se pudieron leer"):
                guarda.migraciones("inexistente")

    def test_migraciones_reales_se_pueden_leer(self):
        actuales = guarda.migraciones("HEAD")
        self.assertTrue(actuales)
        ruta = actuales[max(actuales)]
        contenido = guarda.git("show", f"HEAD:{ruta}")
        self.assertTrue(ruta.endswith(".Designer.cs"))
        self.assertTrue(guarda.cuerpo(contenido, "BuildTargetModel"))


class RepositorioRealTests(unittest.TestCase):
    """Recorre el script entero sobre commits de verdad, como lo hace CI."""

    def setUp(self):
        carpeta = tempfile.TemporaryDirectory(ignore_cleanup_errors=True)
        self.addCleanup(carpeta.cleanup)
        self.repo = pathlib.Path(carpeta.name)
        self.orden("init", "-q")
        self.escribir_snapshot('modelBuilder.X("uno");')
        self.anadir_migracion("20270101000000_Inicial", 'modelBuilder.X("uno");')
        self.base = self.confirmar()

    def orden(self, *args):
        comando = ["git", "-c", "user.name=t", "-c", "user.email=t@t", "-c", "core.autocrlf=false", *args]
        return subprocess.run(comando, cwd=self.repo, check=True, capture_output=True, text=True).stdout.strip()

    def escribir_snapshot(self, contenido):
        self.fichero(guarda.SNAPSHOT, modelo("BuildModel", contenido))

    def anadir_migracion(self, identificador, contenido):
        cuerpo = modelo("BuildTargetModel", contenido)
        self.fichero(f"{guarda.CARPETA}{identificador}.Designer.cs", f'[Migration("{identificador}")] {cuerpo}')

    def fichero(self, ruta, contenido):
        destino = self.repo / ruta
        destino.parent.mkdir(parents=True, exist_ok=True)
        destino.write_text(contenido, encoding="utf-8", newline="\n")

    def confirmar(self):
        self.orden("add", "-A")
        self.orden("commit", "-q", "--allow-empty", "-m", "c")
        return self.orden("rev-parse", "HEAD")

    def guarda(self, base=None):
        entorno = {**os.environ, "PYTHONUTF8": "1"}
        return subprocess.run([sys.executable, str(SCRIPT), base or self.base, "HEAD"], cwd=self.repo, capture_output=True, encoding="utf-8", env=entorno)

    def test_sin_migraciones_y_sin_cambios(self):
        self.fichero("otro.txt", "x")
        self.confirmar()
        self.assertEqual(self.guarda().returncode, 0)

    def test_migracion_sin_tocar_snapshot_es_rojo(self):
        self.anadir_migracion("20270102000000_B", 'modelBuilder.X("uno"); modelBuilder.X("dos");')
        self.confirmar()
        salida = self.guarda()
        self.assertEqual(salida.returncode, 1)
        self.assertIn("no coincide", salida.stderr)

    def test_snapshot_sin_migracion_es_rojo(self):
        self.escribir_snapshot('modelBuilder.X("uno"); modelBuilder.X("dos");')
        self.confirmar()
        salida = self.guarda()
        self.assertEqual(salida.returncode, 1)
        self.assertIn("sin ninguna migración nueva", salida.stderr)

    def test_snapshot_reformateado_sin_migracion_es_verde(self):
        self.escribir_snapshot('modelBuilder . X ( "uno" ) ; // reformateado\n')
        self.confirmar()
        self.assertEqual(self.guarda().returncode, 0)

    def test_migracion_con_snapshot_al_dia_es_verde(self):
        self.anadir_migracion("20270102000000_B", 'modelBuilder.X("dos");')
        self.escribir_snapshot('modelBuilder.X("dos");')
        self.confirmar()
        self.assertEqual(self.guarda().returncode, 0)

    def test_con_varias_nuevas_solo_cuenta_la_ultima(self):
        self.anadir_migracion("20270102000000_B", 'modelBuilder.X("intermedia");')
        self.anadir_migracion("20270103000000_C", 'modelBuilder.X("final");')
        self.escribir_snapshot('modelBuilder.X("final");')
        self.confirmar()
        self.assertEqual(self.guarda().returncode, 0)

    def test_con_varias_nuevas_la_ultima_desalineada_es_rojo(self):
        self.anadir_migracion("20270102000000_B", 'modelBuilder.X("final");')
        self.anadir_migracion("20270103000000_C", 'modelBuilder.X("intermedia");')
        self.escribir_snapshot('modelBuilder.X("final");')
        self.confirmar()
        salida = self.guarda()
        self.assertEqual(salida.returncode, 1)
        self.assertIn("20270103000000_C.Designer.cs", salida.stderr)

    def test_historica_desalineada_no_cuenta(self):
        self.anadir_migracion("20270102000000_Historica", 'modelBuilder.X("desalineada");')
        base = self.confirmar()
        self.fichero("otro.txt", "x")
        self.confirmar()
        self.assertEqual(self.guarda(base).returncode, 0)

    def test_dos_pr_cruzadas_en_la_cola_dan_rojo_con_instrucciones(self):
        # La PR A va delante en la cola: su punta es el base_sha de la PR B.
        self.anadir_migracion("20270102000000_A", 'modelBuilder.X("uno"); modelBuilder.X("a");')
        self.escribir_snapshot('modelBuilder.X("uno"); modelBuilder.X("a");')
        cola = self.confirmar()
        # La PR B nació sin A: el snapshot se fusiona limpio, su Designer no sabe de A.
        self.anadir_migracion("20270103000000_B", 'modelBuilder.X("uno"); modelBuilder.X("b");')
        self.escribir_snapshot('modelBuilder.X("uno"); modelBuilder.X("a"); modelBuilder.X("b");')
        self.confirmar()
        salida = self.guarda(cola)
        self.assertEqual(salida.returncode, 1)
        self.assertTrue(salida.stderr.startswith("::error::"))
        self.assertIn("20270103000000_B.Designer.cs", salida.stderr)
        self.assertIn("actualiza la rama con origin/main", salida.stderr)
        self.assertIn(f"git checkout origin/main -- {guarda.SNAPSHOT}", salida.stderr)
        self.assertIn('"a"', salida.stderr)


if __name__ == "__main__":
    unittest.main()
