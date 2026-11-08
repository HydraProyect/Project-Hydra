using System.Text.RegularExpressions;
using CaeManager.Web.Features.Gestiones.Components;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Los atajos de la pantalla de pasos (§ 4.5) pasan primero por <c>wwwroot/js/atajos-pasos-gestion.js</c>: su
/// <c>TECLAS</c> decide qué llega a <see cref="AtajosPasosGestion.RecibirAtajo"/>, y sus guardas deciden cuándo no
/// llega nada (campo de texto, select o textarea con el foco; un modificador; un diálogo modal). bUnit no ejecuta JS,
/// así que, como <see cref="CatalogoAtajosSincronizadoConJsTests"/>, este test lee el módulo como texto. Vigila que
/// las teclas de los dos lados coincidan y que ninguna guarda desaparezca; el recorrido real en un navegador lo
/// cubriría un E2E, que esta pantalla todavía no tiene.
/// </summary>
public class AtajosPasosGestionSincronizadoConJsTests
{
    /// <summary>El módulo sin comentarios: una guarda comentada no cuenta como guarda.</summary>
    private static readonly string Js = SinComentarios(LeerJs("atajos-pasos-gestion.js"));

    /// <summary>Cuerpo del manejador de keydown hasta el reenvío a C#: lo que tiene que pasar antes de actuar.</summary>
    private static string AntesDeActuar()
    {
        var inicio = Js.IndexOf("const manejador = (evento) => {", StringComparison.Ordinal);
        var actuar = Js.IndexOf("evento.preventDefault();", StringComparison.Ordinal);
        inicio.Should().BeGreaterThanOrEqualTo(0, "el módulo declara su manejador de keydown");
        actuar.Should().BeGreaterThan(inicio, "el manejador cancela la tecla y la reenvía después de sus guardas");
        Js.IndexOf("dotNetRef.invokeMethodAsync('RecibirAtajo', evento.key);", StringComparison.Ordinal)
            .Should().BeGreaterThan(actuar, "la tecla se reenvía a C# después de cancelarla, nunca antes");
        Regex.Matches(Js, @"preventDefault\(\)").Should().ContainSingle("solo se cancela la tecla que se atiende");
        return Js[inicio..actuar];
    }

    [Fact]
    public void Teclas_del_js_coinciden_con_las_que_atiende_el_componente()
    {
        var match = Regex.Match(Js, @"const TECLAS\s*=\s*\[(?<teclas>[^\]]*)\]");
        match.Success.Should().BeTrue("atajos-pasos-gestion.js debe declarar TECLAS como un array literal");

        var teclasJs = Regex.Matches(match.Groups["teclas"].Value, @"'(\w+)'").Select(m => m.Groups[1].Value);

        teclasJs.Should().BeEquivalentTo(AtajosPasosGestion.Teclas,
            "una tecla en un lado y no en el otro es un atajo anunciado en el pie que no funciona, o al revés");
        AtajosPasosGestion.Teclas.Should().NotContain("Tab", "los atajos no tocan la navegación por Tab");
    }

    [Fact]
    public void Una_tecla_que_no_es_de_la_pantalla_sale_lo_primero_sin_cancelarse_y_el_Tab_sigue_su_curso()
    {
        AntesDeActuar().TrimStart().Should().StartWith("const manejador = (evento) => {")
            .And.Subject.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)[1]
            .Should().Be("if (!TECLAS.includes(evento.key)) return;", "es la primera comprobación del manejador");
    }

    [Theory]
    [InlineData("evento.ctrlKey")]
    [InlineData("evento.metaKey")]
    [InlineData("evento.altKey")]
    [InlineData("evento.shiftKey")]
    [InlineData("evento.defaultPrevented")]
    [InlineData("evento.isComposing")]
    [InlineData("evento.repeat")]
    public void Con_un_modificador_o_una_tecla_ya_atendida_compuesta_o_mantenida_no_se_actua(string condicion)
    {
        Regex.IsMatch(AntesDeActuar(), @"if \((?:evento\.\w+ \|\| )*" + Regex.Escape(condicion) + @"(?: \|\| evento\.\w+)*\) return;")
            .Should().BeTrue($"el manejador debe salir sin actuar con {condicion}, antes de cancelar la tecla");
    }

    [Fact]
    public void Con_el_foco_en_un_campo_de_texto_un_select_o_un_textarea_la_tecla_es_del_campo()
    {
        AntesDeActuar().Should().Contain("if (esCampoEditable(activo)) return;");
        var funcion = Regex.Match(Js, @"function esCampoEditable\(activo\) \{(?<cuerpo>[^}]*)\}").Groups["cuerpo"].Value;
        funcion.Should().Contain("activo.isContentEditable")
            .And.Contain("activo.tagName === 'TEXTAREA'")
            .And.Contain("activo.tagName === 'SELECT'")
            .And.Contain("activo.tagName === 'INPUT' && !TIPOS_INPUT_NO_TEXTO.has(activo.type)");
        var noTexto = Regex.Match(Js, @"TIPOS_INPUT_NO_TEXTO = new Set\(\[(?<t>[^\]]*)\]\)").Groups["t"].Value;
        Regex.Matches(noTexto, @"'(\w+)'").Select(m => m.Groups[1].Value)
            .Should().NotContain(["text", "date", "search", "email", "number", "tel", "url", "password"],
                "un campo donde se escribe nunca cuenta como «no texto»");
    }

    [Fact]
    public void Con_un_dialogo_modal_o_un_menu_abierto_la_tecla_no_es_de_la_pantalla()
    {
        var antes = AntesDeActuar();
        antes.Should().Contain("if (hayDialogoModalAbierto()) return;");
        antes.Should().Contain("if (document.querySelector(SELECTOR_MENU_ABIERTO)) return;");
        antes.Should().Contain("if (activo.closest?.(SELECTOR_SUPERFICIE_AJENA)) return;");
        Regex.Match(Js, @"const SELECTOR_SUPERFICIE_AJENA\s*=\s*'(?<s>[^']*)'").Groups["s"].Value
            .Should().Contain("[role=\"menu\"]").And.Contain("[role=\"dialog\"]");
        Regex.Match(Js, @"const SELECTOR_MENU_ABIERTO\s*=\s*'(?<s>[^']*)'").Groups["s"].Value
            .Should().Be("[aria-haspopup=\"menu\"][aria-expanded=\"true\"]");
    }

    [Fact]
    public void El_modulo_no_mueve_el_foco()
    {
        Js.Should().NotContain(".focus(", "un atajo cambia el paso o la opción, no el orden de Tab ni el foco");
    }

    [Fact]
    public void El_registro_devuelve_como_retirar_el_mismo_manejador()
    {
        Js.Should().Contain("document.addEventListener('keydown', manejador);");
        Js.Should().Contain("dispose: () => document.removeEventListener('keydown', manejador)");
    }

    private static string SinComentarios(string js) =>
        Regex.Replace(Regex.Replace(js, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline), @"^\s*//.*$|\s+//.*$", string.Empty, RegexOptions.Multiline)
            .Replace("\r\n", "\n");
    private static string LeerJs(string fichero)
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);
        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;
        actual.Should().NotBeNull("el test necesita la raíz del repositorio (CaeManager.slnx)");

        var ruta = Path.Combine(actual!.FullName, "src", "CaeManager.Web", "wwwroot", "js", fichero);
        File.Exists(ruta).Should().BeTrue($"{fichero} debería existir");
        return File.ReadAllText(ruta);
    }
}
