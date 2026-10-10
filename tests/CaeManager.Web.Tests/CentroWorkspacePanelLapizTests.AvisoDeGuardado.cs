using Bunit;
using CaeManager.Application.Centros.Commands.EditarCentro;
using CaeManager.Domain.Common;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Centros.Components;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;

namespace CaeManager.Web.Tests;

/// <summary>
/// El panel de Centro del Context Workspace avisa del guardado: sin el aviso, la fila de /centros
/// seguiría enseñando el dato anterior, porque el panel vive en MainLayout y guarda sin pasar por
/// la página. Un guardado rechazado no avisa: no hay nada nuevo que leer.
/// </summary>
public partial class CentroWorkspacePanelLapizTests
{
    /// <summary>Lo que responde <see cref="EditarCentroCommand"/>; sin fijarlo, el mediador no lo conoce.</summary>
    private Result? _edicion;

    private static async Task GuardarDesdeElLapizAsync(IRenderedComponent<CentroWorkspacePanel> cut)
    {
        await cut.Find(Lapiz).ClickAsync(new MouseEventArgs());
        await cut.FindAll(".workspace-acciones-edicion button").Single(b => b.TextContent.Trim() == "Guardar").ClickAsync(new MouseEventArgs());
    }

    [Fact]
    public async Task Guardar_desde_el_lapiz_avisa_del_guardado_con_el_tipo_y_el_id_del_Centro()
    {
        _edicion = Result.Exito();
        var cut = Renderizar("informacion");
        var avisos = this.EscucharAvisosDeGuardado();

        await GuardarDesdeElLapizAsync(cut);

        _mediador.Enviadas.OfType<EditarCentroCommand>().Should().ContainSingle("control positivo: el guardado se envió")
            .Which.Id.Should().Be(Id);
        avisos.Should().Equal([(EntidadWorkspace.Centro, Id)]);
    }

    [Fact]
    public async Task Un_guardado_rechazado_no_avisa_del_guardado()
    {
        _edicion = Result.Fallo(Error.Crear("Centro.Concurrencia", "Otra persona ha modificado este centro."));
        var cut = Renderizar("informacion");
        var avisos = this.EscucharAvisosDeGuardado();

        await GuardarDesdeElLapizAsync(cut);

        _mediador.Enviadas.OfType<EditarCentroCommand>().Should().ContainSingle("control positivo: el guardado se envió");
        cut.Markup.Should().Contain("Otra persona ha modificado este centro.", "control positivo: el rechazo llegó al panel");
        avisos.Should().BeEmpty("sin guardado firme no hay fila que volver a pedir");
    }
}
