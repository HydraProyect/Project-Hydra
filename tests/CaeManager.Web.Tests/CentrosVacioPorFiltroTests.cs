using Bunit;
using CaeManager.Application.Centros.Commands.CrearCentro;
using CaeManager.Application.Centros.Queries.ObtenerCentros;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Common;
using CaeManager.Application.Visitas.Queries.ObtenerProximaVisitaPorCentro;
using CaeManager.Domain.Centros;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Centros.Pages;
using FluentAssertions;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Cierra por render el hueco declarado del trinquete para Centros. Dos
/// filtros: búsqueda y estado documental, los dos por URL.
///
/// <para>
/// Centros es además la pantalla sobre la que se descubrió, <b>por mutación</b>,
/// que el trinquete miraba solo el <c>.razor</c> cuando la propiedad vive en el
/// <c>.razor.cs</c> — y hubo que repetir la mutación dos veces para verlo. Que
/// un ratchet de texto pase no significa que la pantalla se pinte bien: eso es
/// lo que comprueba este fichero.
/// </para>
/// </summary>
public class CentrosVacioPorFiltroTests : BunitContext
{
    /// <summary>La página monta AtajosListaTeclado, que importa ./js/atajos-lista.js.</summary>
    public CentrosVacioPorFiltroTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private sealed class MediatorPorTipo : IMediator
    {
        public required IReadOnlyList<CentroListaDto> Centros { get; init; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)(object)(request switch
            {
                ObtenerClientesParaSelectorQuery => (object)Array.Empty<ClienteSelectorDto>(),
                ObtenerProximaVisitaPorCentroQuery => (IReadOnlyDictionary<Guid, IReadOnlyList<VisitaResumenDto>>)new Dictionary<Guid, IReadOnlyList<VisitaResumenDto>>(),
                ObtenerCentrosQuery q => new ResultadoPaginado<CentroListaDto>(
                    Centros, Centros.Count, q.Pagina, q.TamanoPagina),
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

    private static CentroListaDto Centro(string nombre) => new(
        Guid.NewGuid(), nombre, "C-001", Guid.NewGuid(), "Refrielectric S.A.",
        Guid.NewGuid(), "Montajes Ebro S.L.", EstadoCentro.Vigente,
        CumplimientoPorcentaje: 100, RecuentosCentroDto.Vacio);

    private IRenderedComponent<Centros> Renderizar(string? busqueda = null, string? estado = null,
        params CentroListaDto[] centros)
    {
        Services.AddScoped<IMediator>(_ => new MediatorPorTipo { Centros = centros });
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped<ICurrentUserService, UsuarioActualFalso>();
        Services.AddScoped<IValidator<CrearCentroCommand>>(_ => new InlineValidator<CrearCentroCommand>());

        var partes = new List<string>();
        if (!string.IsNullOrWhiteSpace(busqueda)) partes.Add("q=" + Uri.EscapeDataString(busqueda));
        if (!string.IsNullOrWhiteSpace(estado)) partes.Add("estado=" + Uri.EscapeDataString(estado));
        Services.GetRequiredService<NavigationManager>()
            .NavigateTo(partes.Count == 0 ? "centros" : "centros?" + string.Join('&', partes));

        return Render<Centros>();
    }

    [Fact]
    public void Sin_resultados_y_con_busqueda_no_invita_a_crear_el_primero()
    {
        var cut = Renderizar(busqueda: "Zorrotzaurre");

        cut.Markup.Should().Contain("Ningún centro con estos filtros");
        cut.Markup.Should().Contain("Quitar los filtros");
        cut.Markup.Should().NotContain("Aún no hay centros",
            "mandar a crear a quien acaba de buscar termina en un centro duplicado");
    }

    [Fact]
    public void Sin_resultados_y_con_filtro_documental_tambien_lo_distingue()
    {
        var cut = Renderizar(estado: nameof(EstadoCentro.Vencido));

        cut.Markup.Should().Contain("Ningún centro con estos filtros");
        cut.Markup.Should().NotContain("Aún no hay centros");
    }

    [Fact]
    public void Sin_filtros_y_sin_registros_sigue_invitando_a_crear_el_primero()
    {
        var cut = Renderizar();

        cut.Markup.Should().Contain("Aún no hay centros");
        cut.Markup.Should().Contain("Crea el primero para empezar a asignar trabajadores.");
        cut.Markup.Should().NotContain("Ningún centro con estos filtros");
    }

    /// <summary>
    /// El filtrado es de servidor: la consulta devuelve el total YA filtrado.
    /// La barrera va delante — una aserción de ausencia sola sería verde vacío.
    /// </summary>
    [Fact]
    public void La_copia_no_afirma_que_existan_centros_dados_de_alta()
    {
        var cut = Renderizar(busqueda: "Zorrotzaurre");

        cut.Markup.Should().Contain("Ningún centro con estos filtros");
        cut.Markup.Should().NotContain("Hay centros dados de alta");
    }

    /// <summary>
    /// «Quitar los filtros» limpia los dos y además los borra de la URL: sin
    /// eso, <c>OnParametersSet</c> los devolvería en la siguiente navegación
    /// dentro de la propia página.
    /// </summary>
    [Fact]
    public void Quitar_los_filtros_limpia_los_dos_y_tambien_la_url()
    {
        var cut = Renderizar(busqueda: "Zorrotzaurre", estado: nameof(EstadoCentro.Vencido));
        cut.Markup.Should().Contain("Ningún centro con estos filtros", "es el punto de partida de este caso");

        cut.Find(".estado-vacio button").Click();

        var uri = Services.GetRequiredService<NavigationManager>().Uri;
        uri.Should().NotContain("q=Zorrotzaurre").And.NotContain("estado=");
        cut.Markup.Should().Contain("Aún no hay centros");
        cut.Markup.Should().NotContain("Ningún centro con estos filtros");
    }

    [Fact]
    public void Con_resultados_no_se_pinta_ningun_estado_vacio()
    {
        var cut = Renderizar(busqueda: "Zorrotzaurre", centros: Centro("Centro Zorrotzaurre"));

        cut.Markup.Should().NotContain("Ningún centro con estos filtros");
        cut.Markup.Should().NotContain("Aún no hay centros");
        cut.Markup.Should().Contain("Centro Zorrotzaurre");
    }
}
