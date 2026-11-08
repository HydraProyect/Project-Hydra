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
    private static readonly string Js = LeerJs("atajos-pasos-gestion.js");

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

    [Theory]
    [InlineData("evento.ctrlKey")]
    [InlineData("evento.metaKey")]
    [InlineData("evento.altKey")]
    [InlineData("evento.shiftKey")]
    public void Con_un_modificador_pulsado_la_tecla_no_es_de_la_pantalla(string modificador)
    {
        Regex.IsMatch(Js, @"if \((?:evento\.\w+ \|\| )*" + Regex.Escape(modificador) + @"(?: \|\| evento\.\w+)*\) return;")
            .Should().BeTrue($"el manejador debe salir sin hacer nada con {modificador}");
    }

    [Fact]
    public void Con_el_foco_en_un_campo_de_texto_un_select_o_un_textarea_la_tecla_es_del_campo()
    {
        var selector = Regex.Match(Js, @"const SELECTOR_CAMPO\s*=\s*'(?<s>[^']*)'");
        selector.Success.Should().BeTrue();
        selector.Groups["s"].Value.Split(',', StringSplitOptions.TrimEntries).Should().Contain(["input", "textarea", "select"]);
        Js.Should().Contain("if (activo.isContentEditable || activo.matches?.(SELECTOR_CAMPO)) return;");
    }

    [Fact]
    public void Con_un_dialogo_modal_o_un_menu_abierto_la_tecla_no_es_de_la_pantalla()
    {
        Js.Should().Contain("if (hayDialogoModalAbierto()) return;");
        Js.Should().Contain("if (activo.closest?.(SELECTOR_SUPERFICIE_AJENA)) return;");
        Regex.Match(Js, @"const SELECTOR_SUPERFICIE_AJENA\s*=\s*'(?<s>[^']*)'").Groups["s"].Value
            .Should().Contain("[role=\"menu\"]").And.Contain("[role=\"dialog\"]");
    }

    [Fact]
    public void Las_flechas_dentro_de_un_grupo_de_radios_o_de_pestanas_siguen_siendo_del_grupo()
    {
        Js.Should().Contain("if (FLECHAS.includes(evento.key) && activo.closest?.(SELECTOR_FLECHAS_PROPIAS)) return;");
        Regex.Match(Js, @"const SELECTOR_FLECHAS_PROPIAS\s*=\s*'(?<s>[^']*)'").Groups["s"].Value
            .Should().Contain("[role=\"radiogroup\"]").And.Contain("[role=\"tablist\"]");
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
