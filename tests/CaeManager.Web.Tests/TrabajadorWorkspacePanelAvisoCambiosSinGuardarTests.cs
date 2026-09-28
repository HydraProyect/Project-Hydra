using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadorPorId;
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
/// P1-E2b (lote 1): la edición en línea de Información del panel de Trabajador pinta su
/// propio aviso de cambios sin guardar. Vive en el panel, no en la pestaña: cambiar de
/// pestaña no la pierde, así que el aviso va fuera de <c>Pestanas</c> y no pregunta.
/// </summary>
public class TrabajadorWorkspacePanelAvisoCambiosSinGuardarTests : BunitContext
{
    private readonly List<string> _pestanasPedidas = [];

    public TrabajadorWorkspacePanelAvisoCambiosSinGuardarTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        this.ConRolDeEscritura();
    }

    private sealed class MediatorFalso(TrabajadorDetalleDto detalle) : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            request is ObtenerTrabajadorPorIdQuery
                ? Task.FromResult((TResponse)(object)detalle)
                : throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.");

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => Task.CompletedTask;
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => Task.FromResult<object?>(null);
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
    }

    private NavigationManager Navegacion => Services.GetRequiredService<NavigationManager>();

    private async Task<IRenderedComponent<TrabajadorWorkspacePanel>> EditarInformacionAsync()
    {
        var id = Guid.NewGuid();
        var detalle = new TrabajadorDetalleDto(
            id, Guid.NewGuid(), null, "Refrielectric S.A.", "Marco", "Vila", "12884021K",
            new DateOnly(1990, 1, 1), null, null, null, null, null, Guid.NewGuid());
        Services.AddScoped<IMediator>(_ => new MediatorFalso(detalle));
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();

        var cut = Render<TrabajadorWorkspacePanel>(p => p
            .Add(x => x.EntidadId, id)
            .Add(x => x.PestanaActiva, "informacion")
            .Add(x => x.PestanaActivaChanged, (string v) => _pestanasPedidas.Add(v)));
        await cut.FindAll("button").Single(b => b.GetAttribute("aria-label") == "Editar información del trabajador")
            .ClickAsync(new MouseEventArgs());
        return cut;
    }

    private static IElement Control(IRenderedComponent<TrabajadorWorkspacePanel> cut, string etiqueta) =>
        cut.Find("#" + cut.FindAll("label").Single(l => l.TextContent.Trim() == etiqueta).GetAttribute("for"));

    [Fact]
    public async Task La_informacion_editada_pregunta_al_salir()
    {
        var cut = await EditarInformacionAsync();
        await Control(cut, "Apellidos").InputAsync(new ChangeEventArgs { Value = "Vila Soto" });

        await cut.SalirYComprobarQuePreguntaAsync(Navegacion);
    }

    [Fact]
    public async Task Abrir_la_edicion_sin_tocar_nada_no_pregunta()
    {
        var cut = await EditarInformacionAsync();

        await cut.SalirYComprobarQueNoPreguntaAsync(Navegacion, "los valores de partida no son un cambio de quien edita");
    }

    [Fact]
    public async Task Cancelar_tras_editar_no_pregunta()
    {
        var cut = await EditarInformacionAsync();
        await Control(cut, "Apellidos").InputAsync(new ChangeEventArgs { Value = "Vila Soto" });
        await cut.FindAll("button").First(b => b.TextContent.Trim() == "Cancelar").ClickAsync(new MouseEventArgs());

        await cut.SalirYComprobarQueNoPreguntaAsync(Navegacion, "«Cancelar» es una decisión explícita");
    }

    [Fact]
    public async Task Cambiar_de_pestana_con_la_edicion_a_medias_no_pregunta()
    {
        var cut = await EditarInformacionAsync();
        await Control(cut, "Apellidos").InputAsync(new ChangeEventArgs { Value = "Vila Soto" });

        await cut.FindAll("[role=tab]").Single(t => t.TextContent.Trim() == "Historial").ClickAsync(new MouseEventArgs());

        _pestanasPedidas.Should().Equal(["historial"], "la edición vive en el panel y sobrevive al cambio de pestaña");
        cut.FindAll(".modal-contenido").Should().BeEmpty();
    }
}
