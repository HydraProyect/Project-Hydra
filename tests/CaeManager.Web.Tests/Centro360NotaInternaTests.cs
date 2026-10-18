using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Centros.Commands.GuardarNotaInternaCentro;
using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Features.Centros.Pages;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// «Nota interna» de Centro 360: la misma tarjeta y el mismo editor que Empresa 360 (NotaInternaEditable), al final del
/// lateral. Quién la ve lo decide el servidor (NotaInternaVisible); quién ve «Editar →», el rol con escritura.
/// </summary>
public partial class Centro360Gen2Tests
{
    private const string NotaDelCentro = "El vigilante cierra la barrera a las 14:00.";

    private static IElement? TarjetaNotaInterna(IRenderedComponent<CentroDetalle> cut) =>
        cut.FindAll("[data-pieza=nota-interna]").SingleOrDefault();

    private static bool EditorNotaInternaAbierto(IRenderedComponent<CentroDetalle> cut) =>
        cut.FindAll("[role=dialog]").Any(d => d.QuerySelector("h2")?.TextContent.Trim() == "Nota interna");

    private static IElement EditorNotaInterna(IRenderedComponent<CentroDetalle> cut) =>
        cut.FindAll("[role=dialog]").Single(d => d.QuerySelector("h2")?.TextContent.Trim() == "Nota interna");

    private (Guid Id, MediatorFalso Mediador) ConNotaInterna(string? notas, bool visible = true)
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Centro Norte") with { Notas = notas, NotaInternaVisible = visible };
        return (id, mediador);
    }

    [Fact]
    public void El_equipo_ve_la_nota_interna_al_final_del_lateral_con_su_pie_y_Editar()
    {
        var (id, _) = ConNotaInterna(NotaDelCentro);

        var cut = Renderizar(id);

        var tarjeta = TarjetaNotaInterna(cut);
        tarjeta.Should().NotBeNull();
        tarjeta!.TextContent.Should().Contain(NotaDelCentro).And.Contain("No la ve el cliente.");
        tarjeta.QuerySelectorAll("button").Should().ContainSingle(b => b.TextContent.Trim() == "Editar →");
        cut.Find(".cuerpo-con-lateral-lateral").LastElementChild!.GetAttribute("data-pieza").Should().Be("nota-interna");
    }

    [Fact]
    public void Sin_nota_el_equipo_ve_la_tarjeta_con_su_vacio()
    {
        var (id, _) = ConNotaInterna(null);

        var cut = Renderizar(id);

        TarjetaNotaInterna(cut)!.TextContent.Should().Contain("Sin nota interna.");
    }

    [Fact]
    public void Sin_NotaInternaVisible_la_pagina_no_pinta_la_tarjeta_ni_vacia()
    {
        var (id, _) = ConNotaInterna(null, visible: false);

        var cut = Renderizar(id);

        TarjetaNotaInterna(cut).Should().BeNull();
        cut.Markup.Should().NotContain("Nota interna").And.NotContain("No la ve el cliente.");
    }

    [Fact]
    public void Consulta_lee_la_nota_pero_no_se_le_ofrece_editarla()
    {
        this.ConRolDeEscritura(Roles.Consulta);
        var (id, _) = ConNotaInterna(NotaDelCentro);

        var cut = Renderizar(id);

        TarjetaNotaInterna(cut)!.TextContent.Should().Contain(NotaDelCentro);
        TarjetaNotaInterna(cut)!.QuerySelectorAll("button").Should().BeEmpty();
    }

    [Fact]
    public void Editar_y_guardar_manda_la_nota_con_la_version_leida_y_relee_la_ficha()
    {
        var (id, mediador) = ConNotaInterna(NotaDelCentro);
        var versionLeida = mediador.Detalles[id].Version;
        var cut = Renderizar(id);

        TarjetaNotaInterna(cut)!.QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Editar →").Click();
        cut.WaitForAssertion(() => EditorNotaInternaAbierto(cut).Should().BeTrue());
        var campo = EditorNotaInterna(cut).QuerySelector("textarea")!;
        campo.TextContent.Should().Be(NotaDelCentro);
        campo.Input("Pedir acceso por la puerta 3.");
        EditorNotaInterna(cut).QuerySelector(".drawer-pie .boton-primario")!.Click();

        cut.WaitForAssertion(() => EditorNotaInternaAbierto(cut).Should().BeFalse());
        mediador.Enviadas.OfType<GuardarNotaInternaCentroCommand>().Should().ContainSingle()
            .Which.Should().Be(new GuardarNotaInternaCentroCommand(id, "Pedir acceso por la puerta 3.", versionLeida));
        TarjetaNotaInterna(cut)!.TextContent.Should().Contain("Pedir acceso por la puerta 3.").And.NotContain(NotaDelCentro);
        mediador.Enviadas.OfType<CaeManager.Application.Centros.Queries.ObtenerCentroPorId.ObtenerCentroPorIdQuery>()
            .Should().HaveCount(2, "tras guardar se relee la cabecera para tener la Version nueva");
    }

    [Fact]
    public void Un_conflicto_cierra_el_editor_y_relee_la_nota_de_la_otra_persona()
    {
        var (id, mediador) = ConNotaInterna(NotaDelCentro);
        mediador.ResultadoNota = Result.Fallo(Error.Crear(ConcurrenciaOptimista.CodigoConflicto, "Otra persona guardó antes."));
        var cut = Renderizar(id);
        mediador.Detalles[id] = mediador.Detalles[id] with { Notas = "Lo de la otra persona." };

        TarjetaNotaInterna(cut)!.QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Editar →").Click();
        cut.WaitForAssertion(() => EditorNotaInternaAbierto(cut).Should().BeTrue());
        EditorNotaInterna(cut).QuerySelector("textarea")!.Input("Lo mío.");
        EditorNotaInterna(cut).QuerySelector(".drawer-pie .boton-primario")!.Click();

        cut.WaitForAssertion(() => EditorNotaInternaAbierto(cut).Should().BeFalse());
        TarjetaNotaInterna(cut)!.TextContent.Should().Contain("Lo de la otra persona.");
    }
}
