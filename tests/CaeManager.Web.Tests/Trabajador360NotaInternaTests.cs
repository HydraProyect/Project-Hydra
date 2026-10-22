using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Trabajadores.Commands.GuardarNotaInternaTrabajador;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Features.Trabajadores.Pages;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// «Nota interna» de Trabajador 360: la misma tarjeta y el mismo editor que Empresa 360 (NotaInternaEditable), al final
/// del lateral. Quién la ve lo decide el servidor (NotaInternaVisible); quién ve «Editar →», el rol con escritura.
/// </summary>
public partial class Trabajador360Gen2Tests
{
    private const string NotaDelTrabajador = "Prefiere que le llamen por la tarde.";

    private static IElement? TarjetaNotaInterna(IRenderedComponent<TrabajadorDetalle> cut) =>
        cut.FindAll("[data-pieza=nota-interna]").SingleOrDefault();

    private static bool EditorNotaInternaAbierto(IRenderedComponent<TrabajadorDetalle> cut) =>
        cut.FindAll("[role=dialog]").Any(d => d.QuerySelector("h2")?.TextContent.Trim() == "Nota interna");

    private static IElement EditorNotaInterna(IRenderedComponent<TrabajadorDetalle> cut) =>
        cut.FindAll("[role=dialog]").Single(d => d.QuerySelector("h2")?.TextContent.Trim() == "Nota interna");

    private (Guid Id, MediatorFalso Mediador) ConNotaInterna(string? notas, bool visible = true)
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno") with { Notas = notas, NotaInternaVisible = visible };
        return (id, mediador);
    }

    [Fact]
    public void El_equipo_ve_la_nota_interna_al_final_del_lateral_con_su_pie_y_Editar()
    {
        var (id, _) = ConNotaInterna(NotaDelTrabajador);

        var cut = Renderizar(id);

        var tarjeta = TarjetaNotaInterna(cut);
        tarjeta.Should().NotBeNull();
        tarjeta!.TextContent.Should().Contain(NotaDelTrabajador).And.Contain("No la ve el Cliente.");
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
    public void Al_usuario_de_Cliente_la_pagina_no_le_pinta_la_tarjeta_ni_vacia()
    {
        this.ConRolDeEscritura(Roles.Cliente);
        var (id, _) = ConNotaInterna(null, visible: false);

        var cut = Renderizar(id);

        TarjetaNotaInterna(cut).Should().BeNull();
        cut.Markup.Should().NotContain("Nota interna").And.NotContain("No la ve el Cliente.");
    }

    [Fact]
    public void Consulta_lee_la_nota_pero_no_se_le_ofrece_editarla()
    {
        this.ConRolDeEscritura(Roles.Consulta);
        var (id, _) = ConNotaInterna(NotaDelTrabajador);

        var cut = Renderizar(id);

        TarjetaNotaInterna(cut)!.TextContent.Should().Contain(NotaDelTrabajador);
        TarjetaNotaInterna(cut)!.QuerySelectorAll("button").Should().BeEmpty();
    }

    [Fact]
    public void Editar_y_guardar_manda_la_nota_con_la_version_leida_y_la_siguiente_usa_la_version_releida()
    {
        var (id, mediador) = ConNotaInterna(NotaDelTrabajador);
        var versionLeida = mediador.Detalles[id].Version;
        var cut = Renderizar(id);

        TarjetaNotaInterna(cut)!.QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Editar →").Click();
        cut.WaitForAssertion(() => EditorNotaInternaAbierto(cut).Should().BeTrue());
        var campo = EditorNotaInterna(cut).QuerySelector("textarea")!;
        campo.TextContent.Should().Be(NotaDelTrabajador);
        campo.Input("Llamar antes de las 10.");
        EditorNotaInterna(cut).QuerySelector(".drawer-pie .boton-primario")!.Click();

        cut.WaitForAssertion(() => EditorNotaInternaAbierto(cut).Should().BeFalse());
        mediador.Enviadas.OfType<GuardarNotaInternaTrabajadorCommand>().Should().ContainSingle()
            .Which.Should().Be(new GuardarNotaInternaTrabajadorCommand(id, "Llamar antes de las 10.", versionLeida));
        TarjetaNotaInterna(cut)!.TextContent.Should().Contain("Llamar antes de las 10.").And.NotContain(NotaDelTrabajador);

        // La relectura trajo la Version nueva: el segundo guardado no da conflicto contra la suya propia.
        var versionReleida = mediador.Detalles[id].Version;
        TarjetaNotaInterna(cut)!.QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Editar →").Click();
        cut.WaitForAssertion(() => EditorNotaInternaAbierto(cut).Should().BeTrue());
        EditorNotaInterna(cut).QuerySelector("textarea")!.Input("Otra.");
        EditorNotaInterna(cut).QuerySelector(".drawer-pie .boton-primario")!.Click();

        cut.WaitForAssertion(() => mediador.Enviadas.OfType<GuardarNotaInternaTrabajadorCommand>().Should().HaveCount(2));
        mediador.Enviadas.OfType<GuardarNotaInternaTrabajadorCommand>().Last().Version.Should().Be(versionReleida);
    }

    [Fact]
    public void Un_conflicto_cierra_el_editor_relee_la_nota_ajena_y_al_reabrir_recupera_el_borrador()
    {
        var (id, mediador) = ConNotaInterna(NotaDelTrabajador);
        mediador.ResultadoNota = Result.Fallo(Error.Crear(ConcurrenciaOptimista.CodigoConflicto, "Otra persona guardó antes."));
        var cut = Renderizar(id);
        mediador.Detalles[id] = mediador.Detalles[id] with { Notas = "Lo de la otra persona." };

        TarjetaNotaInterna(cut)!.QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Editar →").Click();
        cut.WaitForAssertion(() => EditorNotaInternaAbierto(cut).Should().BeTrue());
        EditorNotaInterna(cut).QuerySelector("textarea")!.Input("Lo mío.");
        EditorNotaInterna(cut).QuerySelector(".drawer-pie .boton-primario")!.Click();

        cut.WaitForAssertion(() => EditorNotaInternaAbierto(cut).Should().BeFalse());
        TarjetaNotaInterna(cut)!.TextContent.Should().Contain("Lo de la otra persona.");

        TarjetaNotaInterna(cut)!.QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Editar →").Click();
        cut.WaitForAssertion(() => EditorNotaInternaAbierto(cut).Should().BeTrue());
        EditorNotaInterna(cut).QuerySelector("textarea")!.TextContent.Should().Be("Lo mío.");
    }

    [Fact]
    public void Un_rechazo_del_servidor_deja_el_editor_abierto_con_su_motivo()
    {
        var (id, mediador) = ConNotaInterna(NotaDelTrabajador);
        mediador.ResultadoNota = Result.Fallo(Error.Crear("Trabajador.NoEncontrado", "No encontramos este trabajador."));
        var cut = Renderizar(id);

        TarjetaNotaInterna(cut)!.QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Editar →").Click();
        cut.WaitForAssertion(() => EditorNotaInternaAbierto(cut).Should().BeTrue());
        EditorNotaInterna(cut).QuerySelector("textarea")!.Input("Lo mío.");
        EditorNotaInterna(cut).QuerySelector(".drawer-pie .boton-primario")!.Click();

        cut.WaitForAssertion(() => EditorNotaInterna(cut).TextContent.Should().Contain("No encontramos este trabajador."));
        TarjetaNotaInterna(cut)!.TextContent.Should().Contain(NotaDelTrabajador);
    }
}
