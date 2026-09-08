using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Gestiones.Queries.ObtenerGestiones;
using CaeManager.Domain.Gestiones;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Gestiones.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Gestiones tiene que distinguir <b>«sin gestiones»</b> de <b>«ninguna con
/// estos filtros»</b>. Aquí el texto sin filtrar es además una explicación de
/// cómo se generan —«se generan desde la Bandeja cuando se detecta la
/// actualización de un documento…»—, así que leído con «Completadas» puesto
/// hace creer que el mecanismo no funciona, cuando el cero solo dice que
/// ninguna se ha completado todavía.
/// </summary>
public class GestionesVacioPorFiltroTests : BunitContext
{
    /// <summary>
    /// La rejilla importa <c>QuickGrid.razor.js</c> al montarse. Esta página no
    /// tiene <c>AtajosListaTeclado</c>, así que sin esto el módulo de QuickGrid
    /// es el que tumba los cinco casos — no un fallo del estado vacío.
    /// </summary>
    public GestionesVacioPorFiltroTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private sealed class MediatorPorTipo : IMediator
    {
        public required IReadOnlyList<GestionListaDto> Gestiones { get; init; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)(object)(request switch
            {
                ObtenerGestionesQuery q => new ResultadoPaginado<GestionListaDto>(
                    Gestiones, Gestiones.Count, q.Pagina, q.TamanoPagina),
                _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
            }));

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            Task.CompletedTask;

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            Task.FromResult<object?>(null);

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    /// <param name="estado">"Pendiente" o "Completada", el filtro que viaja por la URL.</param>
    private IRenderedComponent<Gestiones> Renderizar(string? estado = null, params GestionListaDto[] gestiones)
    {
        Services.AddScoped<IMediator>(_ => new MediatorPorTipo { Gestiones = gestiones });
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();

        // Se acciona el filtro de la URL y no el buscador: este rebota 300 ms
        // y deja vivo un temporizador que se traga el clic siguiente (medido en
        // TiposDocumentoVacioPorFiltroTests).
        Services.GetRequiredService<NavigationManager>()
            .NavigateTo(estado is null ? "gestiones" : "gestiones?estado=" + Uri.EscapeDataString(estado));

        return Render<Gestiones>();
    }

    [Fact]
    public void Filtrando_por_completadas_un_cero_no_se_lee_como_que_el_mecanismo_falla()
    {
        var cut = Renderizar(estado: nameof(EstadoGestion.Completada));

        cut.Markup.Should().Contain("Ninguna gestión con estos filtros");
        cut.Markup.Should().Contain("Quitar los filtros");
        cut.Markup.Should().NotContain("Las gestiones se generan desde la Bandeja",
            "explicar cómo se generan a quien acaba de filtrar por «Completadas» sugiere que no se generan");
    }

    [Fact]
    public void Filtrando_por_pendientes_tambien_lo_distingue()
    {
        var cut = Renderizar(estado: nameof(EstadoGestion.Pendiente));

        cut.Markup.Should().Contain("Ninguna gestión con estos filtros");
        cut.Markup.Should().NotContain("Sin gestiones");
    }

    [Fact]
    public void Sin_filtros_y_sin_registros_sigue_explicando_de_donde_salen()
    {
        var cut = Renderizar();

        cut.Markup.Should().Contain("Sin gestiones");
        cut.Markup.Should().Contain("Las gestiones se generan desde la Bandeja");
        cut.Markup.Should().NotContain("Ninguna gestión con estos filtros");
    }

    [Fact]
    public void Quitar_los_filtros_devuelve_la_lista_completa()
    {
        var cut = Renderizar(estado: nameof(EstadoGestion.Completada));
        cut.Markup.Should().Contain("Ninguna gestión con estos filtros", "es el punto de partida de este caso");

        cut.Find(".estado-vacio button").Click();

        cut.Markup.Should().NotContain("Ninguna gestión con estos filtros");
        cut.Markup.Should().Contain("Sin gestiones");
    }

    [Fact]
    public void Con_resultados_no_se_pinta_ningun_estado_vacio()
    {
        var cut = Renderizar(estado: nameof(EstadoGestion.Pendiente), gestiones: new GestionListaDto(
            Guid.NewGuid(), Guid.NewGuid(), "Juan Pérez", Guid.NewGuid(), "Centro Zorrotzaurre",
            Guid.NewGuid(), "Reconocimiento médico", EstadoGestion.Pendiente, DateTime.UtcNow));

        cut.Markup.Should().NotContain("Ninguna gestión con estos filtros");
        cut.Markup.Should().NotContain("Sin gestiones");
        cut.Markup.Should().Contain("Reconocimiento médico");
    }
}
