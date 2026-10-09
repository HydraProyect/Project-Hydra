using Bunit;
using CaeManager.Application.Trabajadores.Commands.EditarTrabajador;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadorPorId;
using CaeManager.Domain.Common;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Trabajadores.Components;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Escucha de <see cref="ContextWorkspaceService.OnEntidadGuardada"/> para los tests de panel:
/// lo que el panel del Context Workspace emite al guardar es lo que el listado atiende para
/// sustituir su fila en sitio (ver los ficheros <c>*.FilaSeRefresca.cs</c>).
/// </summary>
internal static class EscuchaDeAvisosDeGuardado
{
    /// <summary>Se suscribe al servicio del contenedor del test y devuelve la lista donde irán cayendo los avisos.</summary>
    public static List<(EntidadWorkspace Tipo, Guid Id)> EscucharAvisosDeGuardado(this BunitContext contexto)
    {
        var avisos = new List<(EntidadWorkspace Tipo, Guid Id)>();
        contexto.Services.GetRequiredService<ContextWorkspaceService>().OnEntidadGuardada += (tipo, id) => avisos.Add((tipo, id));
        return avisos;
    }
}

/// <summary>
/// El panel de Trabajador del Context Workspace avisa del guardado: sin el aviso, la fila de
/// /trabajadores seguiría enseñando el dato anterior, porque el panel vive en MainLayout y
/// guarda sin pasar por la página. Un guardado rechazado no avisa: no hay nada nuevo que leer.
/// </summary>
public class TrabajadorWorkspacePanelAvisoDeGuardadoTests : BunitContext
{
    private const string Lapiz = "button[aria-label='Editar información del trabajador']";
    private static readonly Guid Id = Guid.NewGuid();

    private MediadorPorFuncion _mediador = null!;

    private IRenderedComponent<TrabajadorWorkspacePanel> Renderizar(Result edicion)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.ConRolDeEscritura();
        Services.AddLocalization();
        _mediador = new MediadorPorFuncion(p => p switch
        {
            ObtenerTrabajadorPorIdQuery => new TrabajadorDetalleDto(Id, Guid.NewGuid(), null, "Refrielectric S.A.", "Marco", "Vila",
                "12884021K", new DateOnly(1990, 1, 1), null, null, null, null, null, Guid.NewGuid()),
            EditarTrabajadorCommand => edicion,
            // La documentación base y las pestañas piden lo suyo: aquí no se mide, y cada una
            // pinta su propio error.
            _ => throw new NotSupportedException($"Consulta no prevista en este test: {p.GetType().Name}.")
        });
        Services.AddScoped<IMediator>(_ => _mediador);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();

        return Render<TrabajadorWorkspacePanel>(p => p
            .Add(x => x.EntidadId, Id)
            .Add(x => x.PestanaActiva, "informacion")
            .Add(x => x.PestanaActivaChanged, EventCallback.Factory.Create<string>(this, _ => { })));
    }

    private static async Task GuardarDesdeElLapizAsync(IRenderedComponent<TrabajadorWorkspacePanel> cut)
    {
        await cut.Find(Lapiz).ClickAsync(new MouseEventArgs());
        await cut.FindAll(".workspace-acciones-edicion button").Single(b => b.TextContent.Trim() == "Guardar").ClickAsync(new MouseEventArgs());
    }

    [Fact]
    public async Task Guardar_desde_el_lapiz_avisa_del_guardado_con_el_tipo_y_el_id_del_Trabajador()
    {
        var cut = Renderizar(Result.Exito());
        var avisos = this.EscucharAvisosDeGuardado();

        await GuardarDesdeElLapizAsync(cut);

        _mediador.Enviadas.OfType<EditarTrabajadorCommand>().Should().ContainSingle("control positivo: el guardado se envió")
            .Which.Id.Should().Be(Id);
        avisos.Should().Equal([(EntidadWorkspace.Trabajador, Id)]);
    }

    [Fact]
    public async Task Un_guardado_rechazado_no_avisa_del_guardado()
    {
        var cut = Renderizar(Result.Fallo(Error.Crear("Trabajador.Concurrencia", "Otra persona ha modificado este trabajador.")));
        var avisos = this.EscucharAvisosDeGuardado();

        await GuardarDesdeElLapizAsync(cut);

        _mediador.Enviadas.OfType<EditarTrabajadorCommand>().Should().ContainSingle("control positivo: el guardado se envió");
        cut.Markup.Should().Contain("Otra persona ha modificado este trabajador.", "control positivo: el rechazo llegó al panel");
        avisos.Should().BeEmpty("sin guardado firme no hay fila que volver a pedir");
    }
}
