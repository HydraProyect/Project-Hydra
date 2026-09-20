using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <b>Controles del instrumento de los trinquetes de texto</b> (P41c, seguimiento).
///
/// <para>
/// El limpiador de comentarios ya falló dos veces con el mismo modo de fallo —el peor que
/// puede tener un trinquete: <i>verde con la regla violada</i>—, y las dos veces los
/// controles positivos eran literales sintéticos de una o tres líneas que no contenían
/// la forma que lo rompía. Aquí el instrumento se alimenta con lo que de verdad hay:
/// <b>cada fuente real de <c>src</c></b>, con las formas de cadena que desincronizaban
/// un recorrido a mano antepuestas y pospuestas, y contrastado con Roslyn. Cerrar la clase
/// (un fuente real que lo desincroniza) en vez de los casos sueltos.
/// </para>
///
/// <para>
/// <b>Lo que sigue sin cubrir.</b> Roslyn contrasta el recorrido de C#; para Razor no hay
/// oráculo equivalente (no hay analizador de Razor en el proyecto), así que ahí la garantía
/// es la de las propiedades de abajo —centinelas y composición sobre los <c>.razor</c> reales— y
/// no la equivalencia. Las directivas de preprocesador no se resuelven: los ficheros con
/// <c>#if</c> quedan fuera del contraste con Roslyn (se cuentan, no se ocultan).
/// </para>
/// </summary>
public class LimpiadorDeComentariosTests
{
    /// <summary>
    /// Formas de cadena que un recorrido a mano desincroniza si no las distingue. Todas
    /// terminan en una sentencia completa, así que el código que las sigue vuelve a ser código.
    /// </summary>
    private static readonly string[] PrefijosCSharp =
    [
        "var a = $\"\"\"x\"\"\";\n",
        "var a = $$\"\"\"{{1}}\"\"\";\n",
        "var a = $\"\"\"\n    {x} // no es un comentario\n    \"\"\";\n",
        "var a = \"\"\" \" \"\" /* no */ \"\"\";\n",
        "var a = @$\"x \"\"q\"\" // no\";\n",
        "var a = $@\"{x} \\n /* no */\";\n",
        "var a = $\"{x:yyyy-MM-dd} {\"//\"}\";\n",
        "var a = $\"a {(b ? \"x\" : \"y\")} /* no */ {c}\";\n",
        "var a = $\"\"\"\n    {{x}} \"{{ y }}\" {{{z}}}\n    \"\"\";\n",
        "var a = '\"'; var b = '\\''; var c = \"'\";\n",
    ];

    /// <summary>Un comentario o una comilla suelta dentro de una cadena de Razor no abre ni cierra nada.</summary>
    private static readonly (string Antes, string Despues)[] EnvolturasRazor =
    [
        ("@(\"/*\")\n@(\"<!--\")\n", "\n@(\"*/\")\n@(\"-->\")\n"),
        ("@(\"/*\")\n", "\n@(\"*/\")\n"),
        ("@(\"<!--\")\n", "\n@(\"-->\")\n"),
        ("@{ var q = \"\"\"/* <!-- @* \"\"\"; }\n", "\n@{ var r = $\"\"\"*/ --> *@ {q}\"\"\"; }\n"),
    ];

    // ── Casos con nombre (la forma de cada hallazgo, una vez cada uno) ──────

    [Theory]
    [MemberData(nameof(PrefijosCSharpDatos))]
    public void Un_comentario_tras_cualquier_forma_de_cadena_se_quita_y_lo_de_dentro_de_la_cadena_no(string prefijo)
    {
        var resultado = LimpiadorDeComentarios.Quitar(
            prefijo + "x.AllowAnonymous(); // COMENTARIO\n/* COMENTARIO2 */ y();\n", razor: false);

        resultado.Should().StartWith(prefijo, "la cadena se copia entera, con lo que parece un comentario dentro");
        resultado.Should().Contain("x.AllowAnonymous();").And.Contain("y();");
        resultado.Should().NotContain("COMENTARIO", "el limpiador tiene que haber vuelto a código tras la cadena");
    }

    public static IEnumerable<object[]> PrefijosCSharpDatos() => PrefijosCSharp.Select(p => new object[] { p });

    [Fact]
    public void Una_cadena_cruda_interpolada_no_copia_el_resto_del_fichero_con_sus_comentarios()
    {
        // El hallazgo P1-1, tal cual: con `$"""` delante, el recorrido anterior tomaba el
        // `"""` de cierre por apertura y dejaba pasar el TODO que nombra el filtro.
        var texto = "var s = $\"\"\"x\"\"\";\n// TODO: falta .AddEndpointFilter<ActorIntegracionExternaEndpointFilter>()\nvar g = app.MapGroup(\"/x\").AllowAnonymous();\n";

        var limpio = LimpiadorDeComentarios.Quitar(texto, razor: false);

        SuperficiesAnonimasClasificadasPorActorTests.CadenaDeAnonimoLlevaElFiltro(limpio).Should().BeFalse(
            "el filtro está AUSENTE y solo lo nombra un comentario");
        limpio.Should().NotContain("TODO");
    }

    [Theory]
    [InlineData("@(\"/*\")\n@attribute [AllowAnonymous]\n@(\"*/\")\n")]
    [InlineData("@(\"<!--\")\n@attribute [AllowAnonymous]\n@(\"-->\")\n")]
    [InlineData("@{ var s = \"/*\"; }\n@attribute [AllowAnonymous]\n@{ var t = \"*/\"; }\n")]
    [InlineData("@if (x) { var s = \"<!--\"; }\n@attribute [AllowAnonymous]\n@if (y) { var t = \"-->\"; }\n")]
    [InlineData("<div class=\"a/*b\"></div>\n@attribute [AllowAnonymous]\n<i class=\"c*/\"></i>\n")]
    public void Un_apertura_de_comentario_dentro_de_una_cadena_de_Razor_no_borra_lo_que_hay_hasta_el_cierre(string razor)
    {
        // El hallazgo P1-2: el Regex de comentarios emparejaba `/*` o `<!--` con el siguiente
        // cierre real y borraba el AllowAnonymous del medio.
        SuperficiesAnonimasClasificadasPorActorTests
            .SuperficiesAnonimas([("Nueva.razor", razor)])
            .Should().BeEquivalentTo(new Dictionary<string, int> { ["Nueva.razor"] = 1 });
    }

    [Fact]
    public void Los_comentarios_de_Razor_se_quitan_en_el_marcado_y_en_los_bloques()
    {
        var razor = "@* @attribute [AllowAnonymous] *@\n<!-- @attribute [AllowAnonymous] -->\n" +
                    "@code {\n    // [AllowAnonymous] en un comentario\n    /* y en bloque AllowAnonymous */\n    string s = \"//no\";\n}\n" +
                    "@if (x)\n{\n    @* AllowAnonymous *@\n    <p>ok</p>\n}\n";

        SuperficiesAnonimasClasificadasPorActorTests.SuperficiesAnonimas([("Vieja.razor", razor)])
            .Should().BeEmpty("solo hay comentarios");
        LimpiadorDeComentarios.Quitar(razor, razor: true).Should().Contain("string s = \"//no\";").And.Contain("<p>ok</p>");
    }

    // ── El árbol real ──────────────────────────────────────────────────────

    [Fact]
    public void Ningun_fuente_real_de_src_deja_el_recorrido_desincronizado_al_acabar()
    {
        var fuentes = FuentesReales();

        // Control positivo: el barrido tiene que ver de verdad el árbol, no una lista vacía.
        fuentes.Count(f => !EsRazor(f.Ruta)).Should().BeGreaterThan(1000);
        fuentes.Count(f => EsRazor(f.Ruta)).Should().BeGreaterThan(100);

        // Un comentario añadido AL FINAL tiene que quitarse en todos: si el recorrido acabó
        // dentro de una cadena que no cerró, lo copia (el hallazgo de las ocho fuentes
        // desincronizadas de hoy).
        var desincronizados = fuentes
            .Where(f => LimpiadorDeComentarios.Quitar(
                f.Texto + (EsRazor(f.Ruta) ? "\n@* CENTINELA *@\n<!-- CENTINELA -->\n" : "\n// CENTINELA\n/* CENTINELA */\n"),
                EsRazor(f.Ruta)).Contains("CENTINELA", StringComparison.Ordinal))
            .Select(f => f.Ruta)
            .ToList();

        desincronizados.Should().BeEmpty(
            "en estos ficheros el limpiador termina dentro de una cadena o un bloque: copia el resto, " +
            "comentarios incluidos, y un comentario que nombre lo que un trinquete busca lo cegaría");
    }

    [Fact]
    public void Anteponer_formas_de_cadena_no_cambia_el_recorrido_de_ningun_fuente_real()
    {
        var fuentes = FuentesReales().Where(f => !EsRazor(f.Ruta)).ToList();
        fuentes.Count.Should().BeGreaterThan(1000, "control positivo: el barrido ve el árbol");

        // Composición: limpiar (prefijo + fichero) tiene que dar prefijo + limpiar(fichero). Si el
        // prefijo desincroniza el recorrido —lo que hacía `$"""`—, el resto del fichero cambia.
        var rotos = new List<string>();
        foreach (var f in fuentes)
        {
            var solo = LimpiadorDeComentarios.Quitar(f.Texto, razor: false);
            foreach (var prefijo in PrefijosCSharp)
                if (LimpiadorDeComentarios.Quitar(prefijo + f.Texto, razor: false) != prefijo + solo)
                {
                    rotos.Add($"{f.Ruta}  con  {prefijo.Trim().Replace("\n", "\\n")}");
                    break;
                }
        }

        rotos.Should().BeEmpty("una forma de cadena antepuesta no debe alterar cómo se lee el resto del fichero");
    }

    [Fact]
    public void Envolver_los_Razor_reales_en_cadenas_con_aperturas_de_comentario_no_cambia_su_recorrido_ni_esconde_una_superficie()
    {
        var fuentes = FuentesReales().Where(f => EsRazor(f.Ruta)).ToList();
        fuentes.Count.Should().BeGreaterThan(100, "control positivo: el barrido ve las páginas");

        var rotos = new List<string>();
        foreach (var f in fuentes)
        {
            var solo = LimpiadorDeComentarios.Quitar(f.Texto, razor: true);
            var cuantasAntes = SuperficiesAnonimasClasificadasPorActorTests.SuperficiesAnonimas([(f.Ruta, f.Texto)]).GetValueOrDefault(f.Ruta);

            foreach (var (antes, despues) in EnvolturasRazor)
            {
                if (LimpiadorDeComentarios.Quitar(antes + f.Texto + despues, razor: true) != antes + solo + despues)
                    rotos.Add($"{f.Ruta}: cambia el recorrido con {antes.Trim().Replace("\n", "\\n")}");

                // La violación fabricada sobre el fichero real: una página anónima MÁS, tras la envoltura.
                var conViolacion = antes + f.Texto + despues + "\n@attribute [AllowAnonymous]\n";
                var cuantasDespues = SuperficiesAnonimasClasificadasPorActorTests.SuperficiesAnonimas([(f.Ruta, conViolacion)]).GetValueOrDefault(f.Ruta);
                if (cuantasDespues != cuantasAntes + 1)
                    rotos.Add($"{f.Ruta}: la violación fabricada cuenta {cuantasDespues}, esperaba {cuantasAntes + 1}, con {antes.Trim().Replace("\n", "\\n")}");
            }
        }

        rotos.Should().BeEmpty();
    }

    [Fact]
    public void Quitar_el_filtro_de_un_webhook_real_lo_pone_en_rojo_aunque_el_fichero_lleve_cualquier_forma_de_cadena_delante()
    {
        var webhooks = FuentesReales().Where(f => f.Ruta.Contains("/Webhook", StringComparison.Ordinal) && f.Ruta.EndsWith("Endpoints.cs", StringComparison.Ordinal)).ToList();
        webhooks.Should().HaveCountGreaterThanOrEqualTo(3, "los tres webhooks de tercero tienen que estar en el barrido");

        var filtro = SuperficiesAnonimasClasificadasPorActorTests.Filtro;
        foreach (var w in webhooks)
        {
            w.Texto.Should().Contain(filtro, $"control previo: {w.Ruta} lleva el filtro");
            SuperficiesAnonimasClasificadasPorActorTests.CadenaDeAnonimoLlevaElFiltro(LimpiadorDeComentarios.Quitar(w.Texto, razor: false))
                .Should().BeTrue($"control positivo: {w.Ruta} sin tocar pasa");

            // La violación: el filtro se convierte en un comentario que lo nombra («// TODO»).
            var sinFiltro = w.Texto.Replace(filtro, "/* TODO " + filtro + " */");
            foreach (var prefijo in PrefijosCSharp)
                SuperficiesAnonimasClasificadasPorActorTests
                    .CadenaDeAnonimoLlevaElFiltro(LimpiadorDeComentarios.Quitar(prefijo + sinFiltro, razor: false))
                    .Should().BeFalse($"{w.Ruta} sin el filtro tiene que estar en rojo con «{prefijo.Trim().Replace("\n", "\\n")}» delante");
        }
    }

    [Fact]
    public void Un_IResult_propio_fabricado_al_final_de_cada_fuente_real_lo_ve_el_detector_en_todas_sus_formas()
    {
        var fuentes = FuentesReales().Where(f => !EsRazor(f.Ruta)).ToList();
        fuentes.Count.Should().BeGreaterThan(1000, "control positivo: el barrido ve el árbol");

        // La violación fabricada sobre el árbol real, en las formas que la lista cerrada anterior no veía.
        string[] formas =
        [
            "internal sealed partial class CentinelaResultado : IResult { }",
            "public abstract class CentinelaResultado : IResult { }",
            "public readonly struct CentinelaResultado : IResult { }",
            "file class CentinelaResultado : IResult { }",
            "public sealed class @CentinelaResultado : IResult { }",
            "[Tipo(typeof(int[]))] public sealed class CentinelaResultado : IResult { }",
        ];

        var invisibles = new List<string>();
        foreach (var f in fuentes)
            foreach (var forma in formas)
            {
                var limpio = LimpiadorDeComentarios.Quitar(f.Texto + "\n" + forma + "\n", razor: false);
                if (!SuperficiesAnonimasClasificadasPorActorTests.ImplementaIResult.Matches(limpio)
                        .Any(m => m.Groups["tipo"].Value.TrimStart('@') == "CentinelaResultado"))
                    invisibles.Add($"{f.Ruta}: {forma}");
            }

        invisibles.Should().BeEmpty("un IResult declarado al final de un fichero real tiene que verse, sea cual sea el fichero");
    }

    [Fact]
    public void Un_dos_puntos_de_primer_nivel_en_un_hueco_es_formato_y_no_un_ternario_porque_el_compilador_lo_trata_asi()
    {
        // Hallazgo de la pasada de Codex: `{c ? "a" : "b" /* x */}` hace que el recorrido copie el comentario
        // (para él, tras el `:` hay un especificador de formato). Es lo que hace el compilador, así que el
        // fragmento NO es C# válido y no puede llegar a `src`; la premisa se comprueba con Roslyn en vez de suponerse.
        var sinParentesis = "class C { string S(bool c) => $\"{c ? \"a\" : \"b\" /* x */}\"; }";
        var conParentesis = "class C { string S(bool c) => $\"{(c ? \"a\" : \"b\") /* x */}\"; }";

        CSharpSyntaxTree.ParseText(sinParentesis).GetDiagnostics().Should().Contain(d => d.Severity == DiagnosticSeverity.Error,
            "un ternario sin paréntesis dentro de un hueco interpolado no compila");
        CSharpSyntaxTree.ParseText(conParentesis).GetDiagnostics().Should().NotContain(d => d.Severity == DiagnosticSeverity.Error,
            "control positivo: con paréntesis sí compila");

        // Y con paréntesis el recorrido sí ve el comentario.
        LimpiadorDeComentarios.Quitar(conParentesis, razor: false).Should().NotContain("/* x */");
    }

    // ── Roslyn como oráculo (solo C#) ──────────────────────────────────────

    [Fact]
    public void El_recorrido_de_C_sharp_coincide_con_Roslyn_en_cada_fuente_real()
    {
        var fuentes = FuentesReales().Where(f => !EsRazor(f.Ruta)).ToList();
        var conDirectivasCondicionales = new Regex(@"^[ \t]*#[ \t]*(?:if|elif|else)\b", RegexOptions.Multiline);
        var comparables = fuentes.Where(f => !conDirectivasCondicionales.IsMatch(f.Texto)).ToList();

        comparables.Count.Should().BeGreaterThan(fuentes.Count * 9 / 10,
            $"control: el contraste tiene que cubrir casi todo el árbol ({fuentes.Count - comparables.Count} con #if quedan fuera)");

        var distintos = new List<string>();
        foreach (var f in comparables)
        {
            var esperado = SinBlancos(QuitarComentariosConRoslyn(f.Texto));
            var obtenido = SinBlancos(LimpiadorDeComentarios.Quitar(f.Texto, razor: false));
            if (esperado != obtenido) distintos.Add(f.Ruta);
        }

        distintos.Should().BeEmpty("el recorrido a mano tiene que quitar exactamente lo que Roslyn llama comentario");
    }

    [Fact]
    public void El_oraculo_de_Roslyn_ve_lo_que_el_recorrido_antiguo_no_veia()
    {
        // Control del propio oráculo: si Roslyn no distinguiera los comentarios de las cadenas
        // crudas, el contraste de arriba no probaría nada.
        var texto = "var s = $\"\"\"x\"\"\";\n// c\nvar t = \"// no\"; /* d */ var u = 1;\n";

        SinBlancos(QuitarComentariosConRoslyn(texto)).Should().Be(SinBlancos("var s = $\"\"\"x\"\"\";\nvar t = \"// no\";  var u = 1;\n"));
        SinBlancos(LimpiadorDeComentarios.Quitar(texto, razor: false)).Should().Be(SinBlancos(QuitarComentariosConRoslyn(texto)));
    }

    private static string QuitarComentariosConRoslyn(string texto)
    {
        var raiz = CSharpSyntaxTree.ParseText(texto).GetRoot();
        // Con descendIntoTrivia: el comentario que sigue a una directiva (`#pragma … // motivo`) vive dentro de ella.
        var spans = raiz.DescendantTrivia(descendIntoTrivia: true)
            .Where(t => t.Kind() is SyntaxKind.SingleLineCommentTrivia or SyntaxKind.MultiLineCommentTrivia
                                 or SyntaxKind.SingleLineDocumentationCommentTrivia or SyntaxKind.MultiLineDocumentationCommentTrivia)
            .Select(t => t.FullSpan)
            .OrderBy(s => s.Start).ThenByDescending(s => s.Length)
            .ToList();

        // Un comentario dentro de otro (el de documentación contiene trivia) se quita una sola vez.
        var quitar = new List<Microsoft.CodeAnalysis.Text.TextSpan>();
        foreach (var s in spans)
            if (quitar.Count == 0 || s.Start >= quitar[^1].End) quitar.Add(s);

        for (var k = quitar.Count - 1; k >= 0; k--) texto = texto.Remove(quitar[k].Start, quitar[k].Length);
        return texto;
    }

    private static string SinBlancos(string texto) => Regex.Replace(texto, @"\s+", string.Empty);

    private static bool EsRazor(string ruta) => ruta.EndsWith(".razor", StringComparison.Ordinal);

    private static List<(string Ruta, string Texto)> FuentesReales() => SuperficiesAnonimasClasificadasPorActorTests.FuentesDeSrc();
}
