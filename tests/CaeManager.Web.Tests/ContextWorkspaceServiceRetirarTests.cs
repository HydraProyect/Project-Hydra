using CaeManager.Web.Components.Workspace;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// P41b: la baja de la entidad solo vive en las listas y el Workspace no es
/// modal, así que la lista tiene que retirar la ficha de lo que acaba de dar de
/// baja (hallazgo de Codex sobre la PR). Solo actúa sobre el frame ACTUAL.
/// </summary>
public class ContextWorkspaceServiceRetirarTests
{
    private static readonly Guid Ebro = Guid.NewGuid();
    private static readonly Guid Otra = Guid.NewGuid();

    [Fact]
    public async Task Cierra_la_ficha_abierta_de_la_entidad_dada_de_baja()
    {
        var servicio = new ContextWorkspaceService();
        await servicio.AbrirAsync(EntidadWorkspace.Empresa, Ebro, "Montajes Ebro S.L.", "informacion");
        servicio.EstaAbierto.Should().BeTrue("control positivo: el frame estaba abierto");

        await servicio.RetirarSiEstaAbiertoAsync(EntidadWorkspace.Empresa, [Ebro]);

        servicio.EstaAbierto.Should().BeFalse();
    }

    [Fact]
    public async Task Con_historial_vuelve_al_nivel_anterior_en_vez_de_cerrar()
    {
        var servicio = new ContextWorkspaceService();
        await servicio.AbrirAsync(EntidadWorkspace.Cliente, Otra, "Refrielectric S.A.", "informacion");
        await servicio.NavegarAAsync(EntidadWorkspace.Empresa, Ebro, "Montajes Ebro S.L.", "informacion");

        await servicio.RetirarSiEstaAbiertoAsync(EntidadWorkspace.Empresa, [Ebro]);

        servicio.FrameActual.Should().NotBeNull().And.Match<WorkspaceFrame>(f => f.EntidadId == Otra);
    }

    [Theory]
    [InlineData(false)] // mismo tipo, otro id
    [InlineData(true)]  // mismo id, otro tipo
    public async Task No_toca_una_ficha_que_no_es_la_dada_de_baja(bool otroTipo)
    {
        var servicio = new ContextWorkspaceService();
        await servicio.AbrirAsync(EntidadWorkspace.Empresa, Ebro, "Montajes Ebro S.L.", "informacion");

        if (otroTipo)
            await servicio.RetirarSiEstaAbiertoAsync(EntidadWorkspace.Cliente, [Ebro]);
        else
            await servicio.RetirarSiEstaAbiertoAsync(EntidadWorkspace.Empresa, [Otra]);

        servicio.FrameActual.Should().NotBeNull().And.Match<WorkspaceFrame>(f => f.EntidadId == Ebro);
    }
}
