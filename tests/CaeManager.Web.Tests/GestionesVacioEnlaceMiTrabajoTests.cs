using System.Security.Claims;
using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Gestiones.Queries.ObtenerGestiones;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Layout;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Gestiones.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Defecto C3 del piloto Outbound (2026-10-08). El estado vacío de Gestiones explica que las gestiones
/// se generan desde Mi trabajo y ofrece ir allí; enlazaba siempre a <c>/bandeja</c> (la cola de un solo
/// Tenant) aunque el usuario tuviera varios Tenants autorizados, cuando el menú ya lleva a
/// <c>/mi-trabajo</c> en ese caso. El destino es el de la entrada «Mi trabajo» del menú, leído del
/// catálogo y no repetido aquí; y a quien el menú no se la ofrece (rol Consulta, defecto C1) tampoco
/// se le ofrece el botón.
/// </summary>
public class GestionesVacioEnlaceMiTrabajoTests : BunitContext
{
    public GestionesVacioEnlaceMiTrabajoTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
    }

    private IRenderedComponent<Gestiones> Renderizar(string rol, int tenantsAutorizados)
    {
        this.ConRolDeEscritura(rol);
        Services.AddScoped<IMediator>(_ => new MediatorFalso(tenantsAutorizados));
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.GetRequiredService<NavigationManager>().NavigateTo("gestiones");

        var cut = Render<Gestiones>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Sin gestiones"));
        return cut;
    }

    private static string RutaDelMenu(bool variosTenants) =>
        CatalogoMenuLateral.Enlaces.Single(e => e.Id == "mi-trabajo").RutaPara(
            new ContextoMenuLateral(new ClaimsPrincipal(), null, false, false, default, VariosTenants: variosTenants));

    [Theory]
    [InlineData(1, "bandeja")]
    [InlineData(2, "mi-trabajo")]
    public void El_boton_del_estado_vacio_lleva_adonde_lleva_Mi_trabajo_en_el_menu(int tenantsAutorizados, string esperada)
    {
        RutaDelMenu(tenantsAutorizados > 1).Should().Be(esperada, "control: es lo que decide el catálogo del menú");

        var cut = Renderizar(Roles.GestorCae, tenantsAutorizados);

        cut.Find(".estado-vacio [href]").GetAttribute("href").Should().Be(RutaDelMenu(tenantsAutorizados > 1));
    }

    [Fact]
    public void A_quien_el_menu_no_ofrece_Mi_trabajo_el_estado_vacio_tampoco()
    {
        var cut = Renderizar(Roles.Consulta, tenantsAutorizados: 2);

        cut.FindAll(".estado-vacio [href]").Should().BeEmpty("al rol Consulta sus páginas de Mi trabajo le deniegan el acceso");
    }

    private sealed class MediatorFalso(int tenantsAutorizados) : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)(object)(request switch
            {
                ObtenerGestionesQuery q => new ResultadoPaginado<GestionListaDto>([], 0, q.Pagina, q.TamanoPagina),
                ObtenerClientesAutorizadosQuery => (IReadOnlyList<ClienteAutorizadoDto>)
                    [.. Enumerable.Range(0, tenantsAutorizados).Select(i => new ClienteAutorizadoDto(Guid.NewGuid(), $"Organización {i}", EsOrigen: i == 0))],
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
}
