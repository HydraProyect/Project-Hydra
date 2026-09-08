using Bunit;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Application.Subcontratas;
using CaeManager.Application.Subcontratas.Commands.CrearSubcontrata;
using CaeManager.Application.Subcontratas.Queries.ObtenerSubcontratas;
using CaeManager.Application.Tenants.Queries.ObtenerPerfilVocabularioActual;
using CaeManager.Domain.Subcontratas;
using CaeManager.Domain.Tenants;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Subcontratas.Pages;
using FluentAssertions;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Cierra el <b>hueco declarado</b> que dejó el trinquete
/// <c>ListasDistinguenVacioPorFiltroTests</c>: Clientes, Centros, Subcontratas
/// y Documentos se corrigieron a la vez que Empresas y Trabajadores, pero solo
/// tenían verificación <b>estructural</b> — que el fuente declarara
/// <c>HayFiltrosActivos</c> y lo usara en una guarda. Eso no comprueba que el
/// estado se pinte, ni que su texto sea cierto, ni que el botón funcione.
/// Un trinquete de texto da la alarma, no la garantía.
///
/// <para>
/// Subcontratas es la más simple de las cuatro: un único filtro, el buscador.
/// Por eso su copia dice «con esta búsqueda» y no «con estos filtros», y el
/// botón «Quitar la búsqueda» y no «Quitar los filtros» — comprobarlo importa,
/// porque el patrón se copió de una pantalla con cuatro.
/// </para>
/// </summary>
public class SubcontratasVacioPorFiltroTests : BunitContext
{
    /// <summary>La página monta AtajosListaTeclado, que importa ./js/atajos-lista.js.</summary>
    public SubcontratasVacioPorFiltroTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private sealed class MediatorPorTipo : IMediator
    {
        public required IReadOnlyList<SubcontrataListaDto> Subcontratas { get; init; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)(object)(request switch
            {
                ObtenerPerfilVocabularioActualQuery => PerfilVocabularioTenant.Consultora,
                ObtenerClientesParaSelectorQuery => (object)Array.Empty<ClienteSelectorDto>(),
                ObtenerEmpresasParaSelectorQuery => Array.Empty<EmpresaSelectorDto>(),
                ObtenerSubcontratasQuery q => new ResultadoPaginado<SubcontrataListaDto>(
                    Subcontratas, Subcontratas.Count, q.Pagina, q.TamanoPagina),
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

    private static SubcontrataListaDto Subcontrata(string razonSocial) => new(
        Guid.NewGuid(), razonSocial, "B-48.220.917", DateTime.UtcNow,
        NivelServicioSubcontrata.Gestionada, CumplimientoPorcentaje: 100, RecuentosSubcontrataDto.Vacio);

    /// <param name="busqueda">Valor del filtro de texto que llega por la URL (?q=).</param>
    private IRenderedComponent<Subcontratas> Renderizar(string? busqueda = null,
        params SubcontrataListaDto[] subcontratas)
    {
        Services.AddScoped<IMediator>(_ => new MediatorPorTipo { Subcontratas = subcontratas });
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped<ICurrentUserService, UsuarioActualFalso>();
        Services.AddScoped<IValidator<CrearSubcontrataCommand>>(_ => new InlineValidator<CrearSubcontrataCommand>());

        // El filtro es [SupplyParameterFromQuery]: se llega a él navegando, no
        // pasándolo como parámetro de componente.
        Services.GetRequiredService<NavigationManager>()
            .NavigateTo(busqueda is null ? "subcontratas" : "subcontratas?q=" + Uri.EscapeDataString(busqueda));

        return Render<Subcontratas>();
    }

    [Fact]
    public void Sin_resultados_y_con_busqueda_no_invita_a_crear_la_primera()
    {
        var cut = Renderizar(busqueda: "Aislamientos Nervión");

        cut.Markup.Should().Contain("Ninguna subcontrata con esta búsqueda");
        cut.Markup.Should().Contain("Quitar la búsqueda");
        cut.Markup.Should().NotContain("Aún no hay subcontratas",
            "mandar a crear a quien acaba de buscar termina en una subcontrata duplicada");
    }

    /// <summary>
    /// Un único filtro, así que la copia habla en singular. El patrón vino de
    /// pantallas con cuatro, y decir «filtros» donde solo hay un buscador manda
    /// a buscar filtros que no existen.
    /// </summary>
    [Fact]
    public void La_copia_habla_de_la_busqueda_porque_es_el_unico_filtro_que_hay()
    {
        var cut = Renderizar(busqueda: "Aislamientos Nervión");

        cut.Markup.Should().Contain("Ninguna subcontrata con esta búsqueda", "es la barrera de este caso");
        cut.Markup.Should().NotContain("con estos filtros");
        cut.Markup.Should().NotContain("Quitar los filtros");
    }

    [Fact]
    public void Sin_filtros_y_sin_registros_sigue_invitando_a_crear_la_primera()
    {
        var cut = Renderizar();

        cut.Markup.Should().Contain("Aún no hay subcontratas");
        cut.Markup.Should().Contain("Crea la primera para poder dar de alta a sus trabajadores.");
        cut.Markup.Should().NotContain("Ninguna subcontrata con esta búsqueda");
    }

    /// <summary>
    /// El filtrado es de servidor: la consulta devuelve el total YA filtrado,
    /// así que la pantalla no sabe cuántas hay sin filtro. La barrera va
    /// delante — una aserción de ausencia sola sería verde vacío.
    /// </summary>
    [Fact]
    public void La_copia_no_afirma_que_existan_subcontratas_dadas_de_alta()
    {
        var cut = Renderizar(busqueda: "Aislamientos Nervión");

        cut.Markup.Should().Contain("Ninguna subcontrata con esta búsqueda");
        cut.Markup.Should().NotContain("Hay subcontratas dadas de alta");
    }

    [Fact]
    public void Quitar_la_busqueda_devuelve_al_estado_sin_filtrar()
    {
        var cut = Renderizar(busqueda: "Aislamientos Nervión");
        cut.Markup.Should().Contain("Ninguna subcontrata con esta búsqueda", "es el punto de partida de este caso");

        cut.Find(".estado-vacio button").Click();

        cut.Markup.Should().NotContain("Ninguna subcontrata con esta búsqueda");
        cut.Markup.Should().Contain("Aún no hay subcontratas");
    }

    [Fact]
    public void Con_resultados_no_se_pinta_ningun_estado_vacio()
    {
        var cut = Renderizar(busqueda: "Nervión", Subcontrata("Aislamientos Nervión S.L."));

        cut.Markup.Should().NotContain("Ninguna subcontrata con esta búsqueda");
        cut.Markup.Should().NotContain("Aún no hay subcontratas");
        cut.Markup.Should().Contain("Aislamientos Nervión S.L.");
    }
}
