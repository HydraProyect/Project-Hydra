using Bunit;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Application.Centros.Commands.CrearCentro;
using CaeManager.Application.Centros.Queries.ObtenerCentros;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Common;
using CaeManager.Application.Visitas.Queries.ObtenerProximaVisitaPorCentro;
using CaeManager.Domain.Centros;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Centros.Components;
using CaeManager.Web.Features.Centros.Pages;
using FluentAssertions;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Lista de Centros: la baja («Eliminar centro» del menú de fila y «Eliminar seleccionados» del lote) y la
/// asignación masiva son escrituras, y un rol de solo lectura (Consulta) no debe verlas. Cada caso lleva su
/// control positivo con un rol de escritura: sin él, la ausencia podría venir de un menú que no abrió.
/// </summary>
public class CentrosListaSoloLecturaTests : BunitContext
{
    public CentrosListaSoloLecturaTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        ComponentFactories.AddStub<AcordeonAsignacionesCentro>();
        Services.AddLocalization();
    }

    private sealed class MediatorPorTipo(IReadOnlyList<CentroListaDto> centros) : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)(object)(request switch
            {
                ObtenerClientesParaSelectorQuery => (object)Array.Empty<ClienteSelectorDto>(),
                ObtenerProximaVisitaPorCentroQuery => (IReadOnlyDictionary<Guid, IReadOnlyList<VisitaResumenDto>>)new Dictionary<Guid, IReadOnlyList<VisitaResumenDto>>(),
                ObtenerClientesAutorizadosQuery => (IReadOnlyList<ClienteAutorizadoDto>)[new ClienteAutorizadoDto(Guid.NewGuid(), "Propia", EsOrigen: true)],
                ObtenerCentrosQuery q => new ResultadoPaginado<CentroListaDto>(centros, centros.Count, q.Pagina, q.TamanoPagina),
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
        public Task<string?> ObtenerRolOrigenAsync() => ObtenerRolEfectivoAsync();
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>("Administrador");
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    private IRenderedComponent<Centros> Renderizar(string rol)
    {
        this.ConRolDeEscritura(rol);
        var centro = new CentroListaDto(
            Guid.NewGuid(), "Centro Logístico Norte", "C-001", Guid.NewGuid(), "Refrielectric S.A.",
            Guid.NewGuid(), "Montajes Ebro S.L.", EstadoCentro.Vigente,
            CumplimientoPorcentaje: 100, RecuentosCentroDto.Vacio);
        Services.AddScoped<IMediator>(_ => new MediatorPorTipo([centro]));
        Services.AddScoped<ITenantActual>(_ => new SeleccionEmpresaGestionadaDePrueba());
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped<ICurrentUserService, UsuarioActualFalso>();
        Services.AddScoped<IValidator<CrearCentroCommand>>(_ => new InlineValidator<CrearCentroCommand>());
        Services.GetRequiredService<NavigationManager>().NavigateTo("centros");
        return Render<Centros>();
    }

    [Theory]
    [InlineData(Roles.GestorCae, true)]
    [InlineData(Roles.Consulta, false)]
    public async Task Eliminar_centro_del_menu_de_fila_solo_lo_ve_un_rol_con_escritura(string rol, bool debeVerse)
    {
        var cut = Renderizar(rol);
        await cut.Find(".tarjeta-fila-acordeon-acciones .menu-acciones-disparador").ClickAsync(new MouseEventArgs());

        var items = cut.FindAll("[role=menu] button, [role=menu] [role=menuitem]").Select(i => i.TextContent.Trim()).ToList();
        items.Should().Contain("Ver ficha 360", "barrera: el menú se abrió y pinta sus acciones de lectura");
        items.Contains("Eliminar centro").Should().Be(debeVerse);
    }

    [Theory]
    [InlineData(Roles.GestorCae, true)]
    [InlineData(Roles.Consulta, false)]
    public async Task La_barra_de_lote_con_baja_y_asignacion_masiva_solo_la_ve_un_rol_con_escritura(string rol, bool debeVerse)
    {
        var cut = Renderizar(rol);
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Selección múltiple").ClickAsync(new MouseEventArgs());
        await cut.Find("input[aria-label='Seleccionar el centro Centro Logístico Norte']").ChangeAsync(new ChangeEventArgs { Value = true });

        var textos = cut.FindAll("button").Select(b => b.TextContent.Trim()).ToList();
        // Barrera: la selección se hizo (Find de la casilla no lanza), y la lista sigue pintada.
        textos.Should().Contain("Selección múltiple");
        textos.Contains("Eliminar seleccionados").Should().Be(debeVerse);
        textos.Any(t => t.StartsWith("Asignar a centros seleccionados", StringComparison.Ordinal)).Should().Be(debeVerse);
    }
}
