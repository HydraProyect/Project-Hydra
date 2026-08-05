using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;

namespace CaeManager.Web.Tests;

/// <summary>
/// Fase A4 — crear inline desde cualquier selector, sin tener que abandonar
/// la pantalla. A diferencia de CampoBuscarSelect (que usa &lt;datalist&gt;),
/// SelectorEntidad pinta su propia lista para poder ofrecer una fila de
/// acción ("+ Crear «texto»"). El punto que este archivo protege de verdad:
/// las opciones se seleccionan con @@onmousedown:preventDefault, así que el
/// clic nunca se pierde por un blur de por medio — ver el comentario de
/// cabecera del propio componente.
/// </summary>
public class SelectorEntidadTests : BunitContext
{
    private static readonly IReadOnlyList<OpcionBuscable> TresClientes =
    [
        new("1", "Cadena Industrial Iberia S.A."),
        new("2", "Bebidas del Norte S.A."),
        new("3", "Retail Iberia S.A.")
    ];

    [Fact]
    public void Escribir_filtra_las_opciones_por_coincidencia_de_texto()
    {
        var cut = Render<SelectorEntidad>(parametros => parametros
            .Add(p => p.Opciones, TresClientes)
            .Add(p => p.Valor, string.Empty));

        cut.Find("input").Input("iberia");

        var filas = cut.FindAll(".selector-entidad-opcion");
        filas.Should().HaveCount(2);
        cut.Markup.Should().Contain("Cadena Industrial Iberia").And.Contain("Retail Iberia");
        cut.Markup.Should().NotContain("Bebidas del Norte");
    }

    [Fact]
    public void Clicar_una_opcion_notifica_su_Id_y_muestra_su_texto()
    {
        string? valorNotificado = null;

        var cut = Render<SelectorEntidad>(parametros => parametros
            .Add(p => p.Opciones, TresClientes)
            .Add(p => p.Valor, string.Empty)
            .Add(p => p.ValorChanged, v => valorNotificado = v));

        cut.Find("input").Input("Bebidas");
        cut.Find(".selector-entidad-opcion").Click();

        valorNotificado.Should().Be("2");
        cut.Find("input").GetAttribute("value").Should().Be("Bebidas del Norte S.A.");
    }

    [Fact]
    public void Sin_PermiteCrear_no_aparece_ninguna_fila_de_creacion()
    {
        var cut = Render<SelectorEntidad>(parametros => parametros
            .Add(p => p.Opciones, TresClientes)
            .Add(p => p.Valor, string.Empty)
            .Add(p => p.PermiteCrear, false));

        cut.Find("input").Input("Empresa que no existe");

        cut.FindAll(".selector-entidad-opcion-crear").Should().BeEmpty();
    }

    [Fact]
    public void Con_PermiteCrear_y_sin_coincidencia_exacta_aparece_la_fila_de_creacion()
    {
        var cut = Render<SelectorEntidad>(parametros => parametros
            .Add(p => p.Opciones, TresClientes)
            .Add(p => p.Valor, string.Empty)
            .Add(p => p.PermiteCrear, true));

        cut.Find("input").Input("Grúas del Levante");

        cut.Find(".selector-entidad-opcion-crear").TextContent.Should().Contain("Grúas del Levante");
    }

    [Fact]
    public void Con_coincidencia_exacta_no_se_ofrece_crear_de_nuevo()
    {
        var cut = Render<SelectorEntidad>(parametros => parametros
            .Add(p => p.Opciones, TresClientes)
            .Add(p => p.Valor, string.Empty)
            .Add(p => p.PermiteCrear, true));

        cut.Find("input").Input("Bebidas del Norte S.A.");

        cut.FindAll(".selector-entidad-opcion-crear").Should().BeEmpty();
    }

    [Fact]
    public void Clicar_crear_dispara_OnCrearSolicitado_con_el_texto_escrito()
    {
        string? textoSolicitado = null;

        var cut = Render<SelectorEntidad>(parametros => parametros
            .Add(p => p.Opciones, TresClientes)
            .Add(p => p.Valor, string.Empty)
            .Add(p => p.PermiteCrear, true)
            .Add(p => p.OnCrearSolicitado, v => textoSolicitado = v));

        cut.Find("input").Input("  Grúas del Levante  ");
        cut.Find(".selector-entidad-opcion-crear").Click();

        textoSolicitado.Should().Be("Grúas del Levante");
    }

    [Fact]
    public void FlechaAbajo_resalta_la_primera_opcion_y_Enter_la_selecciona()
    {
        string? valorNotificado = null;

        var cut = Render<SelectorEntidad>(parametros => parametros
            .Add(p => p.Opciones, TresClientes)
            .Add(p => p.Valor, string.Empty)
            .Add(p => p.ValorChanged, v => valorNotificado = v));

        var input = cut.Find("input");
        input.Input("Iberia");
        input.KeyDown(new KeyboardEventArgs { Key = "ArrowDown" });
        input.KeyDown(new KeyboardEventArgs { Key = "Enter" });

        valorNotificado.Should().Be("1");
    }

    [Fact]
    public void Escape_cierra_la_lista_y_restaura_el_texto_del_valor_actual()
    {
        var cut = Render<SelectorEntidad>(parametros => parametros
            .Add(p => p.Opciones, TresClientes)
            .Add(p => p.Valor, "2"));

        var input = cut.Find("input");
        input.Input("algo escrito a medias");
        input.KeyDown(new KeyboardEventArgs { Key = "Escape" });

        cut.FindAll(".selector-entidad-lista").Should().BeEmpty();
        cut.Find("input").GetAttribute("value").Should().Be("Bebidas del Norte S.A.");
    }

    [Fact]
    public void Perder_el_foco_sin_elegir_nada_restaura_el_texto_del_valor_actual()
    {
        var cut = Render<SelectorEntidad>(parametros => parametros
            .Add(p => p.Opciones, TresClientes)
            .Add(p => p.Valor, "3"));

        var input = cut.Find("input");
        input.Input("texto sin confirmar");
        input.Blur();

        cut.Find("input").GetAttribute("value").Should().Be("Retail Iberia S.A.");
    }

    [Fact]
    public void Cuando_el_padre_cambia_Valor_externamente_el_texto_se_resincroniza()
    {
        // Caso real: se crea la entidad desde el modal inline y el padre
        // fija Valor al nuevo Id — el texto visible debe seguirlo sin que el
        // usuario tenga que volver a buscar.
        var cut = Render<SelectorEntidad>(parametros => parametros
            .Add(p => p.Opciones, TresClientes)
            .Add(p => p.Valor, string.Empty));

        cut.Render(parametros => parametros.Add(p => p.Valor, "1"));

        cut.Find("input").GetAttribute("value").Should().Be("Cadena Industrial Iberia S.A.");
    }
}
