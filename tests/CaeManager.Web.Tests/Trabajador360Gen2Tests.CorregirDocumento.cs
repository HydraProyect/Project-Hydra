using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Trabajadores.Commands.CorregirDocumentoIdentidadTrabajador;
using CaeManager.Domain.Common;
using CaeManager.Web.Features.Trabajadores.Pages;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace CaeManager.Web.Tests;

/// <summary>
/// Acción «Corregir documento de identidad» de la ficha 360 (decisión de Chris, 2026-10-20): drawer del kit
/// (DrawerFormulario), aviso de Pasaporte antes de guardar, marcadores rechazados sin llegar al servidor y la orden con la
/// versión cargada.
/// </summary>
public partial class Trabajador360Gen2Tests
{
    private static async Task<IRenderedComponent<TrabajadorDetalle>> AbrirCorregirDocumentoAsync(IRenderedComponent<TrabajadorDetalle> cut)
    {
        await cut.Find(".menu-acciones-disparador").ClickAsync(new MouseEventArgs());
        await cut.FindAll("[role=menuitem]").Single(i => i.TextContent.Trim() == "Corregir documento de identidad")
            .ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.Find("[data-campo='documento-actual']"));
        return cut;
    }

    private static async Task EscribirDocumentoAsync(IRenderedComponent<TrabajadorDetalle> cut, string valor)
    {
        var campo = cut.FindAll("[role=dialog] input")
            .Single(i => i.GetAttribute("type") is null or "text");
        await campo.InputAsync(new ChangeEventArgs { Value = valor });
        await campo.ChangeAsync(new ChangeEventArgs { Value = valor });
    }

    private static Task GuardarDocumentoAsync(IRenderedComponent<TrabajadorDetalle> cut) =>
        cut.FindAll("[role=dialog] button").Single(b => b.TextContent.Trim() == "Guardar documento").ClickAsync(new MouseEventArgs());

    [Fact]
    public async Task Un_valor_que_no_es_DNI_ni_NIE_avisa_de_que_se_guardara_como_Pasaporte_y_se_envia()
    {
        var id = Guid.NewGuid(); var m = ConTrabajador(id);
        m.Detalles[id] = m.Detalles[id] with { Dni = "12345678Z" };
        var cut = await AbrirCorregirDocumentoAsync(Renderizar(id));

        cut.Find("[data-campo='documento-actual']").TextContent.Should().Contain("12345678Z");
        cut.FindAll("[data-aviso='pasaporte']").Should().BeEmpty("sin cambios no hay aviso");

        await EscribirDocumentoAsync(cut, "12345678");
        cut.Find("[data-aviso='pasaporte']").TextContent.Should()
            .Contain("No coincide con un DNI ni un NIE: se guardará como Pasaporte");

        await GuardarDocumentoAsync(cut);

        m.Enviadas.OfType<CorregirDocumentoIdentidadTrabajadorCommand>().Should().ContainSingle()
            .Which.Should().Be(new CorregirDocumentoIdentidadTrabajadorCommand(id, m.Detalles[id].Version, "12345678"));
    }

    [Fact]
    public async Task Un_NIE_no_avisa()
    {
        var id = Guid.NewGuid(); var m = ConTrabajador(id);
        m.Detalles[id] = m.Detalles[id] with { Dni = "AB1234567" };
        var cut = await AbrirCorregirDocumentoAsync(Renderizar(id));

        await EscribirDocumentoAsync(cut, "X1234567L");

        cut.FindAll("[data-aviso='pasaporte']").Should().BeEmpty();
    }

    [Fact]
    public async Task Un_marcador_se_rechaza_en_el_formulario_sin_enviar_nada()
    {
        var id = Guid.NewGuid(); var m = ConTrabajador(id);
        var cut = await AbrirCorregirDocumentoAsync(Renderizar(id));

        await EscribirDocumentoAsync(cut, "SIN DNI");
        await GuardarDocumentoAsync(cut);

        m.Enviadas.OfType<CorregirDocumentoIdentidadTrabajadorCommand>().Should().BeEmpty();
        cut.Find("[role=dialog]").TextContent.Should().Contain("no puede llevar espacios");
    }

    [Fact]
    public async Task Si_el_servidor_lo_rechaza_el_drawer_sigue_abierto_con_el_motivo()
    {
        var id = Guid.NewGuid(); var m = ConTrabajador(id);
        m.ResultadoCorregirDocumento = Result.Fallo(Error.Crear("Trabajador.DocumentoSinGestion",
            "Solo quien gestiona la empresa o la subcontrata de este trabajador puede corregir su documento de identidad."));
        var cut = await AbrirCorregirDocumentoAsync(Renderizar(id));

        await EscribirDocumentoAsync(cut, "X1234567L");
        await GuardarDocumentoAsync(cut);

        cut.Find("[role=dialog]").TextContent.Should().Contain("Solo quien gestiona la empresa");
    }
}