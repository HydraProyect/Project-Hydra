using Bunit;
using CaeManager.Application.Integraciones.Queries.ObtenerConexionesIntegracion;
using CaeManager.Domain.Integraciones;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Features.Comunicaciones.Pages;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// «Redactar correo» de la cabecera y rótulos de su cajón (lienzo «Pulida: bandeja de la cartera», L-2 y L-48; líneas 535 y 986-994 del
/// tablero). La Bandeja no pinta banda propia para el Rol Consulta (decisión del 2026-11-21): el aviso lo da AvisoSoloConsulta del layout, y lo que
/// Consulta no puede hacer lo quitan SoloConEscritura y el behavior de autorización de escritura.
/// </summary>
public partial class ComunicacionesGen2Tests
{
    private static AngleSharp.Dom.IElement BotonRedactarCorreo(IRenderedComponent<Bandeja> cut) =>
        cut.Find(".bandeja-cabecera-pagina button");

    [Fact]
    public void Redactar_correo_es_un_boton_secundario_pequeno_con_lapiz_y_el_mismo_rotulo_en_aria_label_y_title()
    {
        var (cut, _) = Renderizar(new Escenario());

        var boton = BotonRedactarCorreo(cut);
        boton.TextContent.Trim().Should().Be("Redactar correo");
        boton.GetAttribute("aria-label").Should().Be("Redactar correo");
        boton.GetAttribute("title").Should().Be("Redactar correo");
        boton.ClassList.Should().Contain(["boton", "boton-secundario", "boton-pequeno"], "el lienzo no lleva primario en la cabecera");
        boton.ClassList.Should().NotContain("boton-primario");
        boton.QuerySelector("svg")!.GetAttribute("aria-hidden").Should().Be("true", "el lápiz es decorativo: el rótulo ya lo dice");
    }

    [Theory]
    [InlineData(Roles.Administrador)]
    [InlineData(Roles.DireccionCae)]
    [InlineData(Roles.CoordinadorCae)]
    [InlineData(Roles.GestorCae)]
    public void Los_roles_que_escriben_ven_Redactar_correo(string rol)
    {
        this.ConRolDeEscritura(rol);

        var (cut, _) = Renderizar(new Escenario());

        cut.FindAll(".bandeja-cabecera-pagina button").Select(b => b.TextContent.Trim()).Should().Contain("Redactar correo");
    }

    [Fact]
    public void El_Rol_Consulta_no_ve_banda_propia_en_la_Bandeja_ni_Redactar_correo_y_sigue_viendo_las_conversaciones()
    {
        this.ConRolDeEscritura(Roles.Consulta);
        var escenario = new Escenario();
        escenario.Conversaciones.Add(Conversacion("Documentación pendiente", ClienteRefrielectric));

        var (cut, _) = Renderizar(escenario);

        // El aviso del Rol Consulta lo pinta AvisoSoloConsulta del layout, no la página: aquí no puede haber una segunda banda.
        cut.FindAll(".bandeja-banda-consulta").Should().BeEmpty("la banda propia de la página se quitó: AvisoSoloConsulta del layout ya avisa");
        cut.Markup.Should().NotContain("Rol Consulta").And.NotContain("Ves todas las conversaciones");
        cut.FindAll(".bandeja-cabecera-pagina button").Should().BeEmpty("«Redactar correo» escribe: Consulta no lo ve");
        AsuntosVisibles(cut).Should().Equal("Documentación pendiente");
        cut.FindAll(".drawer-panel").Should().BeEmpty();
    }

    [Fact]
    public async Task El_cajon_de_Redactar_correo_lleva_los_rotulos_del_lienzo()
    {
        var escenario = new Escenario
        {
            Interceptar = peticion => peticion is ObtenerConexionesIntegracionQuery
                ? Task.FromResult<object?>(new List<ConexionIntegracionListaDto>
                {
                    new(Guid.NewGuid(), "cae@example.invalid", "CAE Norte", null, null, EstadoConexionIntegracion.Habilitada, DateTime.UtcNow, null, null),
                    new(Guid.NewGuid(), "obras@example.invalid", "Obras", null, null, EstadoConexionIntegracion.Habilitada, DateTime.UtcNow, null, null),
                })
                : null,
        };
        var (cut, _) = Renderizar(escenario);

        await BotonRedactarCorreo(cut).ClickAsync(new MouseEventArgs());

        var cajon = cut.Find(".drawer-panel");
        cajon.QuerySelector("h2")!.TextContent.Trim().Should().Be("Redactar correo");
        cajon.QuerySelector(".drawer-subtitulo")!.TextContent.Trim().Should().Be("Crea una conversación nueva");
        cajon.QuerySelectorAll("label").Select(l => l.TextContent.Trim()).Should().Equal("Desde", "Para", "Asunto", "Mensaje");
        cajon.QuerySelectorAll("select option").Select(o => o.TextContent.Trim())
            .Should().Contain(["CAE Norte · cae@example.invalid", "Obras · obras@example.invalid"], "cada buzón se rotula «nombre · dirección»");
        cajon.TextContent.Should().NotContain("Redactar mensaje").And.NotContain("Enviar desde").And.NotContain("separados por coma");
        cut.FindAll(".drawer-pie button").Select(b => b.TextContent.Trim()).Should().Equal("Cancelar", "Enviar");
    }
}
