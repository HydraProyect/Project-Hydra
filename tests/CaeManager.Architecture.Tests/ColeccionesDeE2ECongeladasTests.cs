using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Las colecciones xUnit de la suite E2E (2026-10-10). Dos propiedades, por separado:
///
/// <list type="number">
/// <item><b>El conjunto de <c>[CollectionDefinition]</c> es exactamente el congelado.</b> Cada colección con fixture de
/// aplicación arranca la aplicación entera otra vez (base de datos propia, migraciones, siembra y servidor): unos 32 s por
/// run del job E2E según la medición del 2026-10-09, y el job pasó de 8,5 a 24 minutos en cinco semanas. Una definición
/// nueva no entra sin que alguien la escriba aquí, que es el punto en que se pregunta si una colección existente servía.</item>
/// <item><b>Toda clase de test lleva <c>[Collection("Nombre")]</c> escrito sobre ella, con el nombre entre comillas, y
/// ese nombre es el de una colección definida.</b> Es la propiedad que más importa: un reparto del job E2E en bloques que
/// lea ese literal en el fuente no ve la colección que llega por otra vía —una constante, <c>nameof</c>, la clase base—
/// ni la clase que no tiene ninguna; la clase no entra en ningún bloque y sus tests dejan de ejecutarse <b>sin ningún
/// rojo</b>. Las que hoy no llevan colección están en <see cref="ClasesSinColeccionCongeladas"/>, con su motivo.</item>
/// </list>
///
/// <para>
/// <b>La unidad es la clase por nombre completo</b> (espacio de nombres, clases contenedoras, nombre y aridad genérica),
/// que es la que ve xUnit y la que cuenta un reparto por clases. Las declaraciones <c>partial</c> de una misma clase,
/// repartidas en los ficheros que sea, se unen: es clase de test si alguna parte declara o hereda tests, y cumple si entre
/// todas hay exactamente un <c>[Collection("Nombre")]</c> canónico. El atributo va en una sola parte (repetirlo no
/// compila, CS0579) y vale para las demás; el mensaje cita la parte que lo lleva —o la primera, si no lo lleva ninguna— y
/// enumera las otras. Una declaración sin <c>partial</c> no se une a ninguna otra, aunque coincida en nombre.
/// </para>
///
/// <para>
/// <b>Nombres por igualdad exacta</b> (ordinal), nunca por prefijo ni por <c>Contains</c>: <c>AppCollection</c> es prefijo
/// de las otras trece.
/// </para>
///
/// <para>
/// <b>Contrato efectivo, más estrecho que el nombre.</b> El análisis es sintáctico y solo de los <c>.cs</c> de
/// <c>tests/CaeManager.E2ETests</c> (<see cref="AnalisisDeSuiteE2E.Colecciones"/>). «Clase de test» es la que declara un
/// método con <c>[Fact]</c>, <c>[Theory]</c>, <c>[SkippableFact]</c>, <c>[SkippableTheory]</c> o un atributo de ese mismo
/// árbol que herede de ellos (<c>[TeoriaConMockups]</c>), o la que hereda de una clase del árbol que los declara. NO ve:
/// una clase base o un atributo de test definidos en otro ensamblado; la herencia cuando dos clases comparten nombre simple
/// (se toma por heredera, que es el lado seguro); un alias de <c>using</c> para el atributo; ni el texto dentro de un
/// <c>#if</c> cuyo símbolo no esté definido (el análisis no define ninguno: una clase de test o un atributo escritos ahí
/// no existen para la guarda, y sí los de la rama <c>#else</c>). Tampoco comprueba que la
/// colección elegida sea la adecuada, ni que el fixture de una definición arranque de verdad la aplicación: congela
/// nombres, no costes.
/// </para>
/// </summary>
public class ColeccionesDeE2ECongeladasTests
{
    /// <summary>
    /// Las <c>[CollectionDefinition]</c> de <c>tests/CaeManager.E2ETests</c>. Añadir un nombre aquí es aceptar un arranque
    /// más de la aplicación en cada run del job E2E: se justifica en la PR.
    /// </summary>
    private static readonly string[] ColeccionesCongeladas =
    [
        "AppCollection",
        "AppCollectionBajoRuntime",
        "AppCollectionConCatalan",
        "AppCollectionConsultasSql",
        "AppCollectionCuentaAMedioActivar",
        "AppCollectionEncargoAdministracion",
        "AppCollectionEscenariosDireccion",
        "AppCollectionFichas360",
        "AppCollectionGestorCaeCarteraMultiTenant",
        "AppCollectionMultiTenant",
        "AppCollectionRetencion",
        "AppCollectionRevalidacionRapida",
        "AppCollectionSoporte",
        "AppCollectionVentanaSoporte",
    ];

    /// <summary>
    /// Clases de test sin <c>[Collection]</c>: xUnit les da una colección implícita propia. Solo baja: una clase de test
    /// nueva lleva su <c>[Collection("…")]</c>; no se añade aquí. Las cuatro abren un Chromium por clase
    /// (<c>IClassFixture</c>) y no arrancan la aplicación, así que no cuestan un arranque; lo que les falta es un nombre que
    /// un reparto por colección pueda leer.
    /// </summary>
    private static readonly string[] ClasesSinColeccionCongeladas =
    [
        "AtajosSuperficiesTests",      // módulos JS de producción en Chromium, sin Blazor ni base de datos
        "ComparadorFidelidad360Tests", // el comparador de fidelidad contra dos páginas sintéticas, sin aplicación
        "KeyTipsSuperficieTests",      // keytips.js de producción en Chromium, sin Blazor ni base de datos
        "PortapapelesTests",           // JS real en Chromium con dobles de permisos, sin Blazor ni base de datos
    ];

    private const string GuiaDeDefiniciones =
        "Cada [CollectionDefinition] de E2E con fixture de aplicación arranca la aplicación entera otra vez (base de datos, " +
        "migraciones, siembra, servidor): unos 32 s más en CADA run del job E2E. NUEVA: reutiliza una colección existente " +
        "([Collection(\"AppCollection\")] u otra de la lista) y siembra en el test lo que le falte; solo si el test necesita " +
        "de verdad otra configuración de arranque, añade el nombre a ColeccionesCongeladas (ColeccionesDeE2ECongeladasTests) en " +
        "el mismo commit y justifica el coste en la PR. RETIRADA: borra el nombre de ColeccionesCongeladas. NO LITERAL o " +
        "REPETIDA: el nombre de una definición se escribe una vez y entre comillas.";

    private const string GuiaDeClases =
        "Toda clase de test de E2E lleva [Collection(\"Nombre\")] sobre la PROPIA clase, sola en sus corchetes, con el nombre " +
        "entre comillas (ni constante, ni nameof, ni heredado de la clase base) y con el nombre exacto de una " +
        "[CollectionDefinition] existente. En una clase partial va en UNA sola de sus partes y vale para todas (puede estar " +
        "en otro fichero que el de los tests: el mensaje enumera las partes). Un reparto del job E2E en bloques que lee ese literal no ve ninguna otra forma: la " +
        "clase no entra en ningún bloque y sus tests dejan de ejecutarse sin ningún rojo. OBSOLETA: borra la clase de " +
        "ClasesSinColeccionCongeladas (ColeccionesDeE2ECongeladasTests); esa lista solo baja.";

    [Fact]
    public void Las_colecciones_definidas_en_E2E_son_exactamente_las_congeladas()
    {
        var suite = AnalisisDeSuiteE2E.Colecciones(AnalisisDeSuiteE2E.LeerE2E());

        string.Join("\n", DesviosDeDefiniciones(suite, ColeccionesCongeladas)).Should().BeEmpty(GuiaDeDefiniciones);
    }

    [Fact]
    public void Toda_clase_de_test_de_E2E_lleva_su_coleccion_como_literal_sobre_la_propia_clase()
    {
        var suite = AnalisisDeSuiteE2E.Colecciones(AnalisisDeSuiteE2E.LeerE2E());

        string.Join("\n", DesviosDeClases(suite, ClasesSinColeccionCongeladas)).Should().BeEmpty(GuiaDeClases);
    }

    [Fact]
    public void El_escaner_ve_las_clases_de_test_de_E2E()
    {
        // Control positivo independiente de las listas: sin clases ni tests localizados, «todas llevan colección» valdría por vacío.
        var suite = AnalisisDeSuiteE2E.Colecciones(AnalisisDeSuiteE2E.LeerE2E());

        // Clases, no declaraciones: las partes de una partial cuentan una vez, y sus métodos se suman.
        suite.Clases.Should().HaveCountGreaterThan(60, "había 77 clases de test en E2E al escribirlo; si baja de golpe, dejó de mirar");
        suite.Clases.Sum(c => c.TestsPropios).Should().BeGreaterThan(150, "había 204 métodos de test al escribirlo");
        suite.Clases.Should().OnlyContain(c => c.Partes.Count >= 1 && c.Partes.Any(p => p.Ruta == c.Ruta && p.Linea == c.Linea));
        suite.Clases.Count(c => c.Via == ViaDeColeccion.Literal).Should().BeGreaterThan(60);
        suite.Definiciones.Should().HaveCountGreaterThan(10);
    }

    // ───────────── desvíos: funciones puras, probadas abajo con listas sintéticas ─────────────

    private static List<string> DesviosDeDefiniciones(ColeccionesDeSuite suite, IReadOnlyCollection<string> congeladas)
    {
        var desvios = new List<string>();
        var esperadas = congeladas.ToHashSet(StringComparer.Ordinal);
        var porNombre = suite.Definiciones.Where(d => d.Nombre is not null).ToLookup(d => d.Nombre!, StringComparer.Ordinal);

        foreach (var d in suite.Definiciones.Where(d => d.Nombre is null))
            desvios.Add($"NO LITERAL  {d.Ruta}:{d.Linea}  {d.Clase}: [{d.Texto}] no nombra la colección con un literal entre comillas");

        foreach (var grupo in porNombre.OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            if (!esperadas.Contains(grupo.Key))
                desvios.AddRange(grupo.Select(d => $"NUEVA       {d.Ruta}:{d.Linea}  [CollectionDefinition(\"{d.Nombre}\")] en {d.Clase}: un arranque más de la aplicación en cada run"));

            if (grupo.Count() > 1)
                desvios.Add($"REPETIDA    \"{grupo.Key}\" se define {grupo.Count()} veces: {string.Join(", ", grupo.Select(d => $"{d.Ruta}:{d.Linea}"))}");
        }

        foreach (var nombre in esperadas.Where(n => !porNombre.Contains(n)).OrderBy(n => n, StringComparer.Ordinal))
            desvios.Add($"RETIRADA    \"{nombre}\" está en ColeccionesCongeladas y ya no se define: borra el nombre de la lista");

        return desvios;
    }

    private static List<string> DesviosDeClases(ColeccionesDeSuite suite, IReadOnlyCollection<string> sinColeccionCongeladas)
    {
        var desvios = new List<string>();
        var definidas = suite.Definiciones.Select(d => d.Nombre).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var consentidas = sinColeccionCongeladas.ToHashSet(StringComparer.Ordinal);
        var sinAtributo = suite.Clases.Where(c => c.Via == ViaDeColeccion.SinAtributo).ToLookup(c => c.Nombre, StringComparer.Ordinal);

        foreach (var c in suite.Clases.OrderBy(c => c.Ruta, StringComparer.Ordinal).ThenBy(c => c.Linea))
        {
            var variasPartes = c.Partes.Count > 1;
            var donde = $"{c.Ruta}:{c.Linea}  {c.Nombre}"
                + (c.HeredaDe is null ? string.Empty : $" (hereda tests de {c.HeredaDe})")
                + (variasPartes
                    ? $" (clase partial en {c.Partes.Count} partes: {string.Join(", ", c.Partes.Select(p => $"{p.Ruta}:{p.Linea}"))}; el atributo va en una sola y vale para todas)"
                    : string.Empty);

            switch (c.Via)
            {
                case ViaDeColeccion.NoLiteral:
                    desvios.Add($"NO LITERAL    {donde}: escribe {c.Coleccion}; tiene que ser [Collection(\"Nombre\")], solo en sus corchetes y con el nombre entre comillas");
                    break;
                case ViaDeColeccion.Literal when !definidas.Contains(c.Coleccion!):
                    desvios.Add($"SIN DEFINIR   {donde}: [Collection(\"{c.Coleccion}\")] no es el nombre exacto de ninguna [CollectionDefinition] de E2E");
                    break;
                case ViaDeColeccion.SinAtributo when !consentidas.Contains(c.Nombre):
                    desvios.Add($"SIN COLECCIÓN {donde}: la clase no lleva [Collection(\"…\")] propio" +
                                (variasPartes ? " en ninguna de sus partes" : string.Empty) +
                                (c.HeredaDe is null ? string.Empty : "; el de la clase base no se lee en esta"));
                    break;
            }
        }

        foreach (var nombre in consentidas.OrderBy(n => n, StringComparer.Ordinal))
        {
            var n = sinAtributo[nombre].Count();
            if (n == 0)
                desvios.Add($"OBSOLETA      {nombre}: está en ClasesSinColeccionCongeladas y ya no es una clase de test sin colección: bórrala de la lista");
            else if (n > 1)
                desvios.Add($"AMBIGUA       {nombre}: {n} clases de test sin colección comparten ese nombre ({string.Join(", ", sinAtributo[nombre].Select(c => $"{c.Ruta}:{c.Linea}"))}); la lista consiente una");
        }

        return desvios;
    }

    // ───────────── control positivo del detector, con fuentes sintéticas y nunca con una entrada de las listas ─────────────

    private const string Definiciones =
        "[CollectionDefinition(\"Uno\")] public class ColUno : ICollectionFixture<Arranque>; " +
        "[CollectionDefinition(\"UnoLargo\")] public class ColUnoLargo : ICollectionFixture<Arranque>; ";

    private static ColeccionesDeSuite Analizar(params string[] textos) =>
        AnalisisDeSuiteE2E.Colecciones(textos.Select((t, i) => ($"sintetico/F{i}.cs", t)));

    private static ClaseDeTest Clase(string atributos, string cuerpo = "[Fact] public void M() { }") =>
        Analizar($"{atributos}\npublic class T(Arranque a)\n{{\n    {cuerpo}\n}}").Clases.Should().ContainSingle().Subject;

    [Theory]
    [InlineData("[Fact] public void M() { }")]
    [InlineData("[Fact(Skip = \"motivo\")] public void M() { }")]
    [InlineData("[Theory] [InlineData(1)] public void M(int n) { }")]
    [InlineData("[Theory, InlineData(1)] public void M(int n) { }")]
    [InlineData("[SkippableFact] public void M() { }")]
    [InlineData("[SkippableTheory] [InlineData(1)] public void M(int n) { }")]
    [InlineData("[Xunit.Fact] public async Task M() { await Task.Yield(); }")]
    [InlineData("[FactAttribute] public void M() { }")]
    public void Una_clase_con_un_metodo_de_test_y_su_coleccion_literal_se_lee_como_literal(string cuerpo)
    {
        var clase = Clase("[Collection(\"Uno\")]", cuerpo);

        clase.Via.Should().Be(ViaDeColeccion.Literal);
        clase.Coleccion.Should().Be("Uno");
        clase.Nombre.Should().Be("T");
        clase.TestsPropios.Should().Be(1);
        clase.Linea.Should().Be(2, "la línea es la del nombre de la clase");
    }

    [Theory]
    [InlineData("[Collection(Nombres.Uno)]")]
    [InlineData("[Collection(Uno)]")]
    [InlineData("[Collection(nameof(ColUno))]")]
    [InlineData("[Collection(\"U\" + \"no\")]")]
    [InlineData("[Collection(@\"Uno\")]")]
    [InlineData("[Collection(\"\"\"Uno\"\"\")]")]
    [InlineData("[Collection(\"U\\u006Eo\")]")]
    [InlineData("[Collection($\"Uno\")]")]
    [InlineData("[Collection(name: \"Uno\")]")]
    [InlineData("[CollectionAttribute(\"Uno\")]")]
    [InlineData("[Xunit.Collection(\"Uno\")]")]
    [InlineData("[Collection (\"Uno\")]")]
    [InlineData("[Collection( \"Uno\" )]")]
    [InlineData("[Trait(\"a\", \"b\"), Collection(\"Uno\")]")]
    [InlineData("[Collection(\"Uno\")] [Collection(\"UnoLargo\")]")]
    [InlineData("[Collection(typeof(ColUno))]")]
    [InlineData("[Collection]")]
    public void Una_coleccion_que_no_es_el_literal_canonico_se_detecta_como_no_literal(string atributo)
    {
        var clase = Clase(atributo);

        clase.Via.Should().Be(ViaDeColeccion.NoLiteral);
        clase.Coleccion.Should().Be(atributo, "el mensaje enseña lo que está escrito");
    }

    [Theory]
    [InlineData("")]
    [InlineData("// [Collection(\"Uno\")]")]
    [InlineData("/* [Collection(\"Uno\")] */")]
    [InlineData("/// <summary>Va en <c>[Collection(\"Uno\")]</c>.</summary>")]
    [InlineData("[Trait(\"Coleccion\", \"[Collection(\\\"Uno\\\")]\")]")]
    [InlineData("[CollectionDefinition(\"Uno\")]")]
    public void Una_clase_sin_atributo_de_coleccion_se_detecta_aunque_el_texto_lo_mencione(string encima)
    {
        var clase = Clase(encima);

        clase.Via.Should().Be(ViaDeColeccion.SinAtributo);
        clase.Coleccion.Should().BeNull();
    }

    [Fact]
    public void Dos_clases_en_un_mismo_texto_se_comprueban_cada_una_por_separado()
    {
        var suite = Analizar("""
            [Collection("Uno")]
            public class Primera(Arranque a)
            {
                [Fact] public void A() { }
                [Fact] public void B() { }
            }

            public class Segunda(Arranque a)
            {
                [Fact] public void C() { }
            }

            [Collection(Nombres.Uno)]
            public class Tercera
            {
                [Theory] [InlineData(1)] public void D(int n) { }

                [Collection("Uno")]
                public class Anidada
                {
                    [Fact] public void E() { }
                }
            }
            """);

        suite.Clases.Select(c => (c.Nombre, c.Via, c.Linea, c.TestsPropios)).Should().Equal(
            ("Primera", ViaDeColeccion.Literal, 2, 2),
            ("Segunda", ViaDeColeccion.SinAtributo, 8, 1),
            ("Tercera", ViaDeColeccion.NoLiteral, 14, 1),
            ("Tercera.Anidada", ViaDeColeccion.Literal, 19, 1));
    }

    [Fact]
    public void Lo_que_no_ejecuta_tests_no_es_una_clase_de_test()
    {
        var suite = Analizar("""
            public sealed class Arranque : IAsyncLifetime
            {
                public Task InitializeAsync() => Task.CompletedTask;
                public Task DisposeAsync() => Task.CompletedTask;
            }

            public static class Ayudas
            {
                [Obsolete] public static void Fact() { }
                // [Fact] public static void Comentado() { }
                public const string Texto = "[Fact] public void M() { }";
            }

            public interface IContrato { [Fact] void M(); }
            public struct Valor { [Fact] public void M() { } }
            public abstract class Base { [Fact] public void Heredado() { } }
            """);

        suite.Clases.Should().BeEmpty("fixtures, ayudantes, interfaces, structs y bases abstractas no son clases de test");
    }

    [Fact]
    public void Una_clase_que_solo_hereda_los_tests_es_una_clase_de_test_y_no_recibe_la_coleccion_de_su_base()
    {
        var suite = Analizar(
            Definiciones + "[Collection(\"Uno\")] public abstract class Base { [Fact] public void Heredado() { } }",
            "public class Intermedia : Base { }\npublic class Nieta : Intermedia, IDisposable { public void Dispose() { } }",
            "[Collection(\"Uno\")] public class ConLaSuya : Base { }",
            "public class Ajena : OtraCosa { }");

        suite.Clases.Select(c => (c.Nombre, c.Via, c.TestsPropios, c.HeredaDe)).Should().Equal(
            ("Intermedia", ViaDeColeccion.SinAtributo, 0, "Base"),
            ("Nieta", ViaDeColeccion.SinAtributo, 0, "Intermedia"),
            ("ConLaSuya", ViaDeColeccion.Literal, 0, "Base"));

        DesviosDeClases(suite, []).Should().HaveCount(2).And.OnlyContain(d => d.StartsWith("SIN COLECCIÓN", StringComparison.Ordinal) && d.Contains("hereda tests de"));
    }

    [Fact]
    public void Un_atributo_de_test_propio_que_hereda_de_los_de_xunit_tambien_hace_clase_de_test()
    {
        var suite = Analizar(
            "public sealed class TeoriaPropiaAttribute : TheoryAttribute { }\npublic sealed class TeoriaNietaAttribute : TeoriaPropiaAttribute { }",
            "public class ConPropia { [TeoriaPropia] [InlineData(1)] public void M(int n) { } }",
            "public class ConNieta { [TeoriaNieta] [InlineData(1)] public void M(int n) { } }",
            "public class ConOtroAtributo { [Obsolete] public void M() { } [NoEsDeTest] public void N() { } }");

        suite.AtributosDeTest.Should().Contain(["TeoriaPropia", "TeoriaNieta"]).And.NotContain("NoEsDeTest");
        suite.Clases.Select(c => c.Nombre).Should().Equal("ConPropia", "ConNieta");
    }

    // ───────────── clases partial: la unidad es la clase por nombre completo, no la declaración ─────────────

    [Fact]
    public void Las_partes_de_una_clase_parcial_son_una_sola_clase_con_el_atributo_de_cualquiera_de_ellas()
    {
        var suite = Analizar(
            Definiciones,
            """
            namespace E2E;

            public partial class Partida
            {
                [Fact] public void EnLaParteSinAtributo() { }
            }
            """,
            """
            namespace E2E;

            /// <summary>La parte que lleva el constructor y la colección.</summary>
            [Collection("Uno")]
            public partial class Partida(Arranque a)
            {
                [Fact] public void A() { }
                [Fact] public void B() { }
            }
            """);

        var clase = suite.Clases.Should().ContainSingle("dos declaraciones partial con el mismo nombre completo son una clase").Subject;

        clase.Via.Should().Be(ViaDeColeccion.Literal);
        clase.Coleccion.Should().Be("Uno");
        clase.TestsPropios.Should().Be(3, "los tests de todas las partes se suman");
        clase.Nombre.Should().Be("Partida");
        clase.NombreCompleto.Should().Be("E2E.Partida");
        (clase.Ruta, clase.Linea).Should().Be(("sintetico/F2.cs", 5), "se cita la parte que lleva el atributo, aunque no sea la primera");
        clase.Partes.Should().Equal(new ParteDeClase("sintetico/F1.cs", 3), new ParteDeClase("sintetico/F2.cs", 5));
        DesviosDeClases(suite, []).Should().BeEmpty("el atributo de una parte vale para la otra: ponerlo en las dos no compila");
    }

    [Fact]
    public void Una_clase_parcial_cumple_aunque_todos_sus_tests_esten_en_la_parte_sin_atributo()
    {
        var suite = Analizar(
            Definiciones,
            "[Collection(\"Uno\")] public partial class Partida(Arranque a) { private int _x; }",
            "public partial class Partida { [Fact] public void M() { } }");

        suite.Clases.Should().ContainSingle()
            .Which.Should().Match<ClaseDeTest>(c => c.Via == ViaDeColeccion.Literal && c.Coleccion == "Uno" && c.TestsPropios == 1 && c.Ruta == "sintetico/F1.cs" && c.Partes.Count == 2);
        DesviosDeClases(suite, []).Should().BeEmpty();
    }

    [Fact]
    public void Una_clase_parcial_sin_atributo_en_ninguna_parte_es_una_sola_clase_sin_coleccion()
    {
        var suite = Analizar(
            Definiciones,
            "public partial class Partida(Arranque a) { [Fact] public void A() { } }",
            "public partial class Partida { [Fact] public void B() { } }");

        suite.Clases.Should().ContainSingle()
            .Which.Should().Match<ClaseDeTest>(c => c.Via == ViaDeColeccion.SinAtributo && c.Coleccion == null && c.TestsPropios == 2 && c.Ruta == "sintetico/F1.cs");

        DesviosDeClases(suite, []).Should().ContainSingle("una clase, un desvío: no uno por parte")
            .Which.Should().StartWith("SIN COLECCIÓN")
            .And.Contain("sintetico/F1.cs:1  Partida", "sin atributo se cita la primera parte")
            .And.Contain("clase partial en 2 partes: sintetico/F1.cs:1, sintetico/F2.cs:1")
            .And.Contain("en ninguna de sus partes");
        DesviosDeClases(suite, ["Partida"]).Should().BeEmpty("la lista consiente la clase, y sus dos partes son una clase y no dos homónimas");
    }

    [Theory]
    [InlineData("[Collection(Nombres.Uno)]", "", "[Collection(Nombres.Uno)]")]
    [InlineData("[Collection(nameof(ColUno))]", "", "[Collection(nameof(ColUno))]")]
    [InlineData("[Collection(\"Uno\")]", "[Collection(\"Uno\")]", "[Collection(\"Uno\")] [Collection(\"Uno\")]")]
    [InlineData("[Collection(\"Uno\")]", "[Collection(\"UnoLargo\")]", "[Collection(\"Uno\")] [Collection(\"UnoLargo\")]")]
    [InlineData("[Collection(\"Uno\")]", "[Collection(Nombres.Uno)]", "[Collection(\"Uno\")] [Collection(Nombres.Uno)]")]
    public void Un_atributo_no_literal_o_repetido_en_cualquier_parte_de_una_parcial_se_detecta_como_no_literal(
        string sobreLaParteSinTests, string sobreLaParteConTests, string escrito)
    {
        var suite = Analizar(
            Definiciones,
            $"{sobreLaParteSinTests} public partial class Partida(Arranque a) {{ private int _x; }}",
            $"{sobreLaParteConTests} public partial class Partida {{ [Fact] public void M() {{ }} }}");

        var clase = suite.Clases.Should().ContainSingle().Subject;

        clase.Via.Should().Be(ViaDeColeccion.NoLiteral, "tiene que haber exactamente un atributo canónico entre todas las partes");
        clase.Coleccion.Should().Be(escrito);
        clase.Ruta.Should().Be("sintetico/F1.cs", "se cita la parte que lleva el atributo, aunque los tests estén en la otra");

        DesviosDeClases(suite, []).Should().ContainSingle()
            .Which.Should().StartWith("NO LITERAL").And.Contain("sintetico/F1.cs:1  Partida").And.Contain("sintetico/F2.cs:1").And.Contain(escrito);
    }

    [Fact]
    public void Las_partes_se_unen_por_espacio_de_nombres_clases_contenedoras_nombre_y_aridad_generica()
    {
        var suite = Analizar(
            "namespace A; [Collection(\"Uno\")] public partial class G<T> { [Fact] public void M() { } }",
            "namespace A { public partial class G<TOtroNombre> { [Fact] public void N() { } } }",
            "namespace A; public partial class G { [Fact] public void SinParametros() { } }",
            "namespace A; public partial class G<T, U> { [Fact] public void DosParametros() { } }",
            "namespace B; public partial class G<T> { [Fact] public void OtroEspacio() { } }",
            "namespace A.Sub; [Collection(\"Uno\")] public partial class Ext<T> { public partial class Int { [Fact] public void M() { } } }",
            "namespace A { namespace Sub { public partial class Ext<T> { public partial class Int { [Fact] public void N() { } } } } }",
            "namespace A.Sub; public partial class OtraExt { public partial class Int { [Fact] public void OtraContenedora() { } } }");

        suite.Clases.Select(c => (c.NombreCompleto, c.Nombre, c.Via, c.TestsPropios, c.Partes.Count)).Should().Equal(
            ("A.G`1", "G", ViaDeColeccion.Literal, 2, 2),
            ("A.G", "G", ViaDeColeccion.SinAtributo, 1, 1),
            ("A.G`2", "G", ViaDeColeccion.SinAtributo, 1, 1),
            ("B.G`1", "G", ViaDeColeccion.SinAtributo, 1, 1),
            ("A.Sub.Ext`1.Int", "Ext.Int", ViaDeColeccion.SinAtributo, 2, 2),
            ("A.Sub.OtraExt.Int", "OtraExt.Int", ViaDeColeccion.SinAtributo, 1, 1));
    }

    [Fact]
    public void Dos_clases_no_parciales_con_el_mismo_nombre_no_se_unen_y_la_lista_sigue_viendolas_ambiguas()
    {
        const string fact = "{ [Fact] public void M() { } }";
        var suite = Analizar(
            Definiciones,
            $"namespace A; [Collection(\"Uno\")] public class Doble {fact}",
            $"namespace B; public class Doble {fact}",
            $"namespace A; public class Doble {fact}",
            $"namespace A; public partial class Doble {fact}");

        suite.Clases.Select(c => (c.NombreCompleto, c.Ruta, c.Via, c.Partes.Count)).Should().Equal(
            ("A.Doble", "sintetico/F1.cs", ViaDeColeccion.Literal, 1),
            ("B.Doble", "sintetico/F2.cs", ViaDeColeccion.SinAtributo, 1),
            ("A.Doble", "sintetico/F3.cs", ViaDeColeccion.SinAtributo, 1),
            ("A.Doble", "sintetico/F4.cs", ViaDeColeccion.SinAtributo, 1));

        DesviosDeClases(suite, []).Should().HaveCount(3, "la colección de una Doble no cubre a ninguna de las otras tres")
            .And.OnlyContain(d => d.StartsWith("SIN COLECCIÓN", StringComparison.Ordinal) && !d.Contains("partial"));
        DesviosDeClases(suite, ["Doble"]).Should().ContainSingle()
            .Which.Should().StartWith("AMBIGUA").And.Contain("3 clases de test sin colección");
    }

    [Fact]
    public void En_una_clase_parcial_abstract_y_la_clase_base_valen_las_escriba_la_parte_que_las_escriba()
    {
        var suite = Analizar(
            Definiciones + "public abstract class Base { [Fact] public void Heredado() { } }",
            "public abstract partial class Molde { [Fact] public void M() { } }",
            "public partial class Molde { [Fact] public void N() { } }",
            "[Collection(\"Uno\")] public partial class Heredera { }",
            "public partial class Heredera : Base { }");

        suite.Clases.Select(c => (c.Nombre, c.Via, c.TestsPropios, c.HeredaDe, c.Ruta, c.Partes.Count)).Should().Equal(
            ("Heredera", ViaDeColeccion.Literal, 0, "Base", "sintetico/F3.cs", 2));
        DesviosDeClases(suite, []).Should().BeEmpty();
    }

    [Fact]
    public void Las_definiciones_se_leen_con_su_nombre_exacto_y_las_que_no_son_literales_se_marcan()
    {
        var suite = Analizar(Definiciones + """
            // [CollectionDefinition("Comentada")]
            [CollectionDefinition(Nombres.Uno)] public class ColConstante;
            [CollectionDefinition("Uno", DisableParallelization = true)] public class ColConOpciones;
            public class Otra { public const string Texto = "[CollectionDefinition(\"EnCadena\")]"; }
            """);

        suite.Definiciones.Select(d => (d.Clase, d.Nombre)).Should().Equal(
            ("ColUno", "Uno"),
            ("ColUnoLargo", "UnoLargo"),
            ("ColConstante", null),
            ("ColConOpciones", "Uno"));
        suite.Clases.Should().BeEmpty();
    }

    [Fact]
    public void Los_desvios_de_definiciones_distinguen_nueva_retirada_repetida_y_no_literal()
    {
        var suite = Analizar(Definiciones);

        DesviosDeDefiniciones(suite, ["Uno", "UnoLargo"]).Should().BeEmpty();
        DesviosDeDefiniciones(suite, ["UnoLargo", "Uno"]).Should().BeEmpty("es un conjunto: el orden no cuenta");

        DesviosDeDefiniciones(suite, ["Uno"]).Should().ContainSingle()
            .Which.Should().StartWith("NUEVA").And.Contain("[CollectionDefinition(\"UnoLargo\")]").And.Contain("sintetico/F0.cs:1");
        DesviosDeDefiniciones(suite, ["UnoLargo"]).Should().ContainSingle()
            .Which.Should().StartWith("NUEVA").And.Contain("[CollectionDefinition(\"Uno\")]", "que «Uno» sea prefijo de una congelada no lo consiente");
        DesviosDeDefiniciones(suite, ["Uno", "UnoLargo", "Tres"]).Should().ContainSingle()
            .Which.Should().StartWith("RETIRADA").And.Contain("\"Tres\"");
        DesviosDeDefiniciones(suite, ["Uno", "UnoLargo", "UnoLarg"]).Should().ContainSingle()
            .Which.Should().StartWith("RETIRADA").And.Contain("\"UnoLarg\"", "que una definida empiece por el nombre congelado no lo da por definido");
        DesviosDeDefiniciones(suite, ["uno", "UnoLargo"]).Should().HaveCount(2, "mayúsculas y minúsculas distinguen: una nueva y una retirada");

        DesviosDeDefiniciones(Analizar(Definiciones, "[CollectionDefinition(\"Uno\")] public class Otra;"), ["Uno", "UnoLargo"])
            .Should().ContainSingle().Which.Should().StartWith("REPETIDA").And.Contain("sintetico/F1.cs:1");
        DesviosDeDefiniciones(Analizar(Definiciones, "[CollectionDefinition(Nombres.Uno)] public class Otra;"), ["Uno", "UnoLargo"])
            .Should().ContainSingle().Which.Should().StartWith("NO LITERAL").And.Contain("Nombres.Uno");
        DesviosDeDefiniciones(Analizar("public class Nada;"), ["Uno"]).Should().ContainSingle("un escáner que no ve ninguna definición no da verde")
            .Which.Should().StartWith("RETIRADA");
    }

    [Fact]
    public void Los_desvios_de_clases_exigen_literal_definido_o_estar_en_la_lista_de_sin_coleccion()
    {
        const string fact = "{ [Fact] public void M() { } }";
        var suite = Analizar(
            Definiciones,
            $"[Collection(\"Uno\")] public class Bien {fact}",
            $"[Collection(\"UnoLargo\")] public class BienLarga {fact}",
            $"public class Consentida {fact}");

        DesviosDeClases(suite, ["Consentida"]).Should().BeEmpty();
        DesviosDeClases(suite, []).Should().ContainSingle()
            .Which.Should().StartWith("SIN COLECCIÓN").And.Contain("sintetico/F3.cs:1  Consentida");
        DesviosDeClases(suite, ["Consentid"]).Should().HaveCount(2, "un nombre que es prefijo no consiente a la clase, y además queda obsoleto");
        DesviosDeClases(suite, ["Consentida", "YaNoExiste", "Bien"]).Should().HaveCount(2)
            .And.OnlyContain(d => d.StartsWith("OBSOLETA", StringComparison.Ordinal), "una clase que ya lleva colección o que ya no existe sale de la lista");

        DesviosDeClases(Analizar(Definiciones, $"[Collection(\"Un\")] public class Prefijo {fact}"), []).Should().ContainSingle()
            .Which.Should().StartWith("SIN DEFINIR").And.Contain("[Collection(\"Un\")]", "«Un» es prefijo de «Uno», no una colección definida");
        DesviosDeClases(Analizar(Definiciones, $"[Collection(\"UnoLargoMas\")] public class Sufijo {fact}"), []).Should().ContainSingle()
            .Which.Should().StartWith("SIN DEFINIR");
        DesviosDeClases(Analizar(Definiciones, $"[Collection(\"uno\")] public class Minusculas {fact}"), []).Should().ContainSingle()
            .Which.Should().StartWith("SIN DEFINIR");
        DesviosDeClases(Analizar(Definiciones, $"[Collection(Nombres.Uno)] public class Constante {fact}"), []).Should().ContainSingle()
            .Which.Should().StartWith("NO LITERAL").And.Contain("[Collection(Nombres.Uno)]").And.Contain("sintetico/F1.cs:1  Constante");
        DesviosDeClases(Analizar(Definiciones, $"[Collection(Nombres.Uno)] public class Constante {fact}"), ["Constante"]).Should().HaveCount(2,
            "la lista de sin colección no consiente una colección no literal: sigue en rojo y la entrada queda obsoleta");
        DesviosDeClases(Analizar($"public class Doble {fact}", $"namespace Otro {{ public class Doble {fact} }}"), ["Doble"]).Should().ContainSingle()
            .Which.Should().StartWith("AMBIGUA");
    }
}
