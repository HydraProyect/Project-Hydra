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
/// «Redactar correo» de la cabecera, rótulos de su cajón y banda del Rol Consulta (lienzo «Pulida: bandeja de la cartera», L-2, L-4 y
/// L-48; líneas 535, 548-550 y 986-994 del tablero). La banda solo informa: lo que Consulta no puede hacer lo quitan SoloConEscritura
/// y el behavior de autorización de escritura, no ella.
/// </summary>
public partial class ComunicacionesGen2Tests
{
    private const string TextoBandaConsulta =
        "Ves todas las conversaciones, notas y fichas, pero no puedes responder, cambiar el estado ni aprobar acciones.";

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
    public void Los_roles_que_escriben_no_ven_la_banda_del_Rol_Consulta(string rol)
    {
        this.ConRolDeEscritura(rol);

        var (cut, _) = Renderizar(new Escenario());

        cut.FindAll(".bandeja-banda-consulta").Should().BeEmpty();
        cut.FindAll(".bandeja-cabecera-pagina button").Select(b => b.TextContent.Trim()).Should().Contain("Redactar correo");
    }

    [Fact]
    public void El_Rol_Consulta_ve_la_banda_con_el_rol_en_negrita_y_el_texto_del_lienzo_y_no_ve_Redactar_correo()
    {
        this.ConRolDeEscritura(Roles.Consulta);
        var escenario = new Escenario();
        escenario.Conversaciones.Add(Conversacion("Documentación pendiente", ClienteRefrielectric));

        var (cut, _) = Renderizar(escenario);

        var banda = cut.Find(".bandeja-banda-consulta");
        banda.GetAttribute("role").Should().Be("status");
        banda.Children.Select(c => (c.TagName, c.TextContent)).Should().Equal(("B", "Rol Consulta"), ("SPAN", TextoBandaConsulta));
        cut.FindAll(".bandeja-cabecera-pagina button").Should().BeEmpty("la banda informa, y «Redactar correo» escribe: Consulta no lo ve");
        AsuntosVisibles(cut).Should().Equal("Documentación pendiente");
        cut.FindAll(".drawer-panel").Should().BeEmpty();
    }

    [Fact]
    public void La_banda_va_entre_la_cabecera_de_la_pagina_y_el_layout_de_la_bandeja()
    {
        this.ConRolDeEscritura(Roles.Consulta);

        var (cut, _) = Renderizar(new Escenario());

        var banda = cut.Find(".bandeja-banda-consulta");
        banda.PreviousElementSibling!.ClassList.Should().Contain("bandeja-cabecera-pagina");
        banda.NextElementSibling!.ClassList.Should().Contain("bandeja-layout");
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
