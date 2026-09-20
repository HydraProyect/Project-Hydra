using Bunit;
using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Common;
using CaeManager.Application.Incidencias.Queries.ObtenerIncidencias;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Domain.Incidencias;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Incidencias.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Incidencias tiene que distinguir <b>«todavía no hay incidencias»</b> de
/// <b>«ninguna con estos filtros»</b>, y aquí la confusión cambia una
/// conclusión de seguridad: con «Sin resolver» puesto, leer que no hay ninguna
/// incidencia registrada hace creer que nunca ha pasado nada en ningún centro
/// — cuando el cero solo dice que no queda ninguna abierta.
/// </summary>
public class IncidenciasVacioPorFiltroTests : BunitContext
{
    /// <summary>La página importa ./js/atajos-lista.js; ese módulo queda fuera de lo que se observa aquí.</summary>
    public IncidenciasVacioPorFiltroTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private sealed class MediatorPorTipo : IMediator
    {
        public required IReadOnlyList<IncidenciaListaDto> Incidencias { get; init; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)(object)(request switch
            {
                ObtenerCentrosParaSelectorQuery => (object)Array.Empty<CentroSelectorDto>(),
                ObtenerTrabajadoresParaSelectorQuery => Array.Empty<TrabajadorSelectorDto>(),
                ObtenerIncidenciasQuery q => new ResultadoPaginado<IncidenciaListaDto>(
                    Incidencias, Incidencias.Count, q.Pagina, q.TamanoPagina),
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

    private sealed class UsuarioActualFalso : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<string?> ObtenerRolActualAsync() => Task.FromResult<string?>("Administrador");
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    /// <param name="estado">"SinResolver" o "Resuelta", el filtro que viaja por la URL.</param>
    private IRenderedComponent<Incidencias> Renderizar(string? estado = null, params IncidenciaListaDto[] incidencias)
    {
        Services.AddScoped<IMediator>(_ => new MediatorPorTipo { Incidencias = incidencias });
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped<ICurrentUserService, UsuarioActualFalso>();

        // El único filtro que viaja por la URL es el estado; la búsqueda vive
        // solo en el campo. Se acciona el de la URL a propósito: el buscador
        // rebota 300 ms y deja vivo un temporizador que se traga el clic
        // siguiente (medido en TiposDocumentoVacioPorFiltroTests).
        Services.GetRequiredService<NavigationManager>()
            .NavigateTo(estado is null ? "incidencias" : "incidencias?estado=" + Uri.EscapeDataString(estado));

        return Render<Incidencias>();
    }

    [Fact]
    public void Filtrando_por_sin_resolver_un_cero_no_se_lee_como_que_nunca_paso_nada()
    {
        var cut = Renderizar(estado: "SinResolver");

        cut.Markup.Should().Contain("Ninguna incidencia con estos filtros");
        cut.Markup.Should().Contain("Quitar los filtros");
        cut.Markup.Should().NotContain("Todavía no hay incidencias",
            "cero incidencias abiertas no es lo mismo que ninguna incidencia registrada jamás");
    }

    [Fact]
    public void Filtrando_por_resueltas_tambien_lo_distingue()
    {
        var cut = Renderizar(estado: "Resuelta");

        cut.Markup.Should().Contain("Ninguna incidencia con estos filtros");
        cut.Markup.Should().NotContain("Todavía no hay incidencias");
    }

    [Fact]
    public void Sin_filtros_y_sin_registros_sigue_invitando_a_registrar_la_primera()
    {
        var cut = Renderizar();

        cut.Markup.Should().Contain("Todavía no hay incidencias");
        cut.Markup.Should().Contain("Registra la primera incidencia operativa de un centro.");
        cut.Markup.Should().NotContain("Ninguna incidencia con estos filtros");
    }

    /// <summary>
    /// El filtrado es de servidor: la consulta devuelve el total YA filtrado.
    /// La barrera va delante — una aserción de ausencia sola sería verde vacío.
    /// </summary>
    [Fact]
    public void La_copia_no_afirma_cuantas_incidencias_hay_registradas()
    {
        var cut = Renderizar(estado: "SinResolver");

        cut.Markup.Should().Contain("Ninguna incidencia con estos filtros");
        cut.Markup.Should().NotContain("Hay incidencias registradas");
    }

    [Fact]
    public void Quitar_los_filtros_devuelve_la_lista_completa()
    {
        var cut = Renderizar(estado: "SinResolver");
        cut.Markup.Should().Contain("Ninguna incidencia con estos filtros", "es el punto de partida de este caso");

        cut.Find(".estado-vacio button").Click();

        cut.Markup.Should().NotContain("Ninguna incidencia con estos filtros");
        cut.Markup.Should().Contain("Todavía no hay incidencias");
    }

    [Fact]
    public void Con_resultados_no_se_pinta_ningun_estado_vacio()
    {
        var cut = Renderizar(estado: "SinResolver", incidencias: new IncidenciaListaDto(
            Guid.NewGuid(), Guid.NewGuid(), "Centro Zorrotzaurre", null, null,
            TipoIncidencia.Accidente, GravedadIncidencia.Leve, new DateOnly(2026, 9, 1), Resuelta: false));

        cut.Markup.Should().NotContain("Ninguna incidencia con estos filtros");
        cut.Markup.Should().NotContain("Todavía no hay incidencias");
        cut.Markup.Should().Contain("Centro Zorrotzaurre");
    }
}
