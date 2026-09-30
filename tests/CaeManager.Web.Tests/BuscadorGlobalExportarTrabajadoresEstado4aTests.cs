using Bunit;
using CaeManager.Application.BusquedaGlobal.Queries.BuscarGlobal;
using CaeManager.Application.BusquedaGlobal.Queries.ObtenerRecientes;
using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Web.Features.BusquedaGlobal;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>ITenantActual sin Tenant, para los tests de la paleta que no lo consultan.</summary>
internal sealed class TenantActualDePaletaFalso : ITenantActual
{
    public Guid? TenantId => null;
}

/// <summary>
/// La paleta no ofrece «Exportar a Excel» en /trabajadores mientras la pantalla esté en el
/// estado 4a: aplica la misma condición que la página (vía <c>ContextoEmpresaActiva</c>) y que el endpoint
/// (<see cref="ClientesAutorizados.PideElegirEmpresa"/>).
/// </summary>
public class BuscadorGlobalExportarTrabajadoresEstado4aTests : BunitContext
{
    private static readonly Guid Origen = Guid.NewGuid();
    private static readonly Guid Externo = Guid.NewGuid();

    private sealed class Tenant(Guid? id) : ITenantActual
    {
        public Guid? TenantId { get; } = id;
    }

    private sealed class MediatorFalso(IReadOnlyList<ClienteAutorizadoDto> autorizados) : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)(request switch
            {
                ObtenerRecientesQuery => (object)(IReadOnlyList<ItemBusquedaDto>)[],
                ObtenerClientesAutorizadosQuery => autorizados,
                _ => throw new NotSupportedException(request.GetType().Name),
            }));

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            Task.CompletedTask;
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => Task.FromResult<object?>(null);
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    private async Task<IRenderedComponent<BuscadorGlobal>> AbrirEnTrabajadoresAsync(
        IReadOnlyList<ClienteAutorizadoDto> autorizados, Guid tenantActual)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddScoped<IMediator>(_ => new MediatorFalso(autorizados));
        Services.AddScoped<BusquedaGlobalService>();
        Services.AddScoped<ITenantActual>(_ => new Tenant(tenantActual));
        Services.GetRequiredService<NavigationManager>().NavigateTo("/trabajadores");

        var cut = Render<BuscadorGlobal>();
        await cut.InvokeAsync(() => cut.Instance.AbrirDesdeJs());
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Nuevo trabajador"));
        return cut;
    }

    private static ClienteAutorizadoDto Propio(bool gestionado = false) =>
        new(Origen, "Operador CAE", EsOrigen: true, EsGestionadoPorOperacion: gestionado);

    private static ClienteAutorizadoDto Cartera() =>
        new(Externo, "Tenant beneficiario", EsOrigen: false, EsGestionadoPorOperacion: true, EsCarteraGestorCae: true);

    [Fact]
    public async Task En_el_estado_4a_la_paleta_no_ofrece_exportar_trabajadores()
    {
        var cut = await AbrirEnTrabajadoresAsync([Propio(), Cartera()], Origen);

        cut.Markup.Should().NotContain("/trabajadores/exportar.xlsx");
    }

    [Fact]
    public async Task Con_una_empresa_de_la_cartera_elegida_la_paleta_ofrece_exportar_trabajadores()
    {
        var cut = await AbrirEnTrabajadoresAsync([Propio(), Cartera()], Externo);

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("/trabajadores/exportar.xlsx"));
    }
}
