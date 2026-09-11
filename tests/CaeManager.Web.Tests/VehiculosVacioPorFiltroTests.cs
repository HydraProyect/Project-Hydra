using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Application.Subcontratas.Queries.ObtenerSubcontratasParaSelector;
using CaeManager.Application.Tenants.Queries.ObtenerPerfilVocabularioActual;
using CaeManager.Application.Vehiculos.Commands.CrearVehiculo;
using CaeManager.Application.Vehiculos.Queries.ObtenerVehiculos;
using CaeManager.Domain.Tenants;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Vehiculos.Pages;
using FluentAssertions;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// El mismo defecto que <see cref="TrabajadoresVacioPorFiltroTests"/> cierra
/// para Trabajadores, y con la misma construcción: rejilla, cuatro filtros
/// —búsqueda, documentación, empresa y subcontrata— y filtrado de servidor.
/// Ofrecer «crea el primero» a quien acaba de filtrar lo manda a dar de alta un
/// vehículo que probablemente ya existe, con la matrícula duplicada que eso
/// arrastra.
/// </summary>
public class VehiculosVacioPorFiltroTests : BunitContext
{
    /// <summary>La página importa ./js/atajos-lista.js; ese módulo queda fuera de lo que se observa aquí.</summary>
    public VehiculosVacioPorFiltroTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private static readonly Guid EmpresaId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid SubcontrataId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private sealed class MediatorPorTipo : IMediator
    {
        public required IReadOnlyList<VehiculoListaDto> Vehiculos { get; init; }

        public List<object> Enviadas { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);
            return Task.FromResult((TResponse)(object)(request switch
            {
                ObtenerPerfilVocabularioActualQuery => PerfilVocabularioTenant.Consultora,
                ObtenerEmpresasParaSelectorQuery => (object)new[] { new EmpresaSelectorDto(EmpresaId, "Montajes Ebro S.L.") },
                ObtenerSubcontratasParaSelectorQuery => new[] { new SubcontrataSelectorDto(SubcontrataId, "Aislamientos Nervión S.L.") },
                ObtenerVehiculosQuery q => new ResultadoPaginado<VehiculoListaDto>(
                    Vehiculos, Vehiculos.Count, q.Pagina, q.TamanoPagina),
                _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
            }));
        }

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

    private IRenderedComponent<Vehiculos> Renderizar(string? busqueda = null, string? estado = null,
        params VehiculoListaDto[] vehiculos) =>
        RenderizarConMediador(busqueda, estado, vehiculos).Cut;

    private (IRenderedComponent<Vehiculos> Cut, MediatorPorTipo Mediador) RenderizarConMediador(
        string? busqueda = null, string? estado = null, params VehiculoListaDto[] vehiculos)
    {
        var mediador = new MediatorPorTipo { Vehiculos = vehiculos };
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped<ICurrentUserService, UsuarioActualFalso>();
        Services.AddScoped<IValidator<CrearVehiculoCommand>>(_ => new InlineValidator<CrearVehiculoCommand>());

        // Los filtros de URL son [SupplyParameterFromQuery]: se llega a ellos
        // navegando, no pasándolos como parámetros de componente.
        var partes = new List<string>();
        if (!string.IsNullOrWhiteSpace(busqueda)) partes.Add("q=" + Uri.EscapeDataString(busqueda));
        if (!string.IsNullOrWhiteSpace(estado)) partes.Add("estado=" + Uri.EscapeDataString(estado));
        Services.GetRequiredService<NavigationManager>()
            .NavigateTo(partes.Count == 0 ? "vehiculos" : "vehiculos?" + string.Join('&', partes));

        return (Render<Vehiculos>(), mediador);
    }

    [Fact]
    public void Sin_resultados_y_con_busqueda_no_invita_a_crear_el_primero()
    {
        var cut = Renderizar(busqueda: "1234-ABC");

        cut.Markup.Should().Contain("Ningún vehículo con estos filtros");
        cut.Markup.Should().Contain("Quitar los filtros");
        cut.Markup.Should().NotContain("Aún no hay vehículos",
            "mandar a crear a quien acaba de buscar una matrícula termina en un vehículo duplicado");
    }

    [Fact]
    public void Sin_resultados_y_con_filtro_documental_tambien_lo_distingue()
    {
        var cut = Renderizar(estado: nameof(CaeManager.Domain.Documentos.EstadoDocumento.Vencido));

        cut.Markup.Should().Contain("Ningún vehículo con estos filtros");
        cut.Markup.Should().NotContain("Aún no hay vehículos");
    }

    [Fact]
    public void Sin_filtros_y_sin_registros_sigue_invitando_a_crear_el_primero()
    {
        var cut = Renderizar();

        cut.Markup.Should().Contain("Aún no hay vehículos");
        cut.Markup.Should().Contain("Crea el primero para empezar a gestionar su documentación.");
        cut.Markup.Should().NotContain("Ningún vehículo con estos filtros");
    }

    /// <summary>
    /// El filtrado es de servidor: la consulta devuelve el total YA filtrado,
    /// así que la pantalla no sabe cuántos vehículos hay sin filtro. La barrera
    /// va delante — una aserción de ausencia sola sería verde vacío.
    /// </summary>
    [Fact]
    public void La_copia_no_afirma_que_existan_vehiculos_dados_de_alta()
    {
        var cut = Renderizar(busqueda: "1234-ABC");

        cut.Markup.Should().Contain("Ningún vehículo con estos filtros");
        cut.Markup.Should().NotContain("Hay vehículos dados de alta");
    }

    [Fact]
    public void Quitar_los_filtros_devuelve_la_lista_completa()
    {
        var cut = Renderizar(busqueda: "1234-ABC");
        cut.Markup.Should().Contain("Ningún vehículo con estos filtros", "es el punto de partida de este caso");

        cut.Find(".estado-vacio button").Click();

        cut.Markup.Should().NotContain("Ningún vehículo con estos filtros");
        cut.Markup.Should().Contain("Aún no hay vehículos",
            "sin filtros y sin registros, el estado correcto vuelve a ser el de la lista vacía de verdad");
    }

    [Fact]
    public void Con_resultados_no_se_pinta_ningun_estado_vacio()
    {
        var cut = Renderizar(busqueda: "Furgoneta", vehiculos: new VehiculoListaDto(
            Guid.NewGuid(), "Furgoneta de obra", "Transit", "1234-ABC", "Montajes Ebro S.L."));

        cut.Markup.Should().NotContain("Ningún vehículo con estos filtros");
        cut.Markup.Should().NotContain("Aún no hay vehículos");
        cut.Markup.Should().Contain("1234-ABC");
    }

    // --- Recuento de consultas ----------------------------------------------------------------

    private static int ConsultasDeLista(MediatorPorTipo mediador) =>
        mediador.Enviadas.OfType<ObtenerVehiculosQuery>().Count();

    private static IRenderedComponent<CampoTexto> CajaDeBusqueda(IRenderedComponent<Vehiculos> cut) =>
        cut.FindComponents<CampoTexto>().First(c => c.Instance.Placeholder?.StartsWith("Buscar por nombre") == true);

    /// <summary>
    /// Cambiar el tamaño de página pide la página 1 del tamaño nuevo UNA vez.
    /// <c>SetCurrentPageIndexAsync</c> ya avisa a QuickGrid aunque la página no
    /// cambie, así que refrescar además la rejilla pedía lo mismo dos veces
    /// (ver <c>RecargarAsync</c> en <c>Vehiculos.razor.cs</c>). Los mismos dos
    /// vehículos antes y después mantienen el total quieto, así que lo que se
    /// cuenta es lo que pide la página y no una repetición de QuickGrid.
    /// </summary>
    [Fact]
    public void Cambiar_el_tamano_de_pagina_hace_una_sola_consulta()
    {
        var (cut, mediador) = RenderizarConMediador(vehiculos:
        [
            new VehiculoListaDto(Guid.NewGuid(), "Furgoneta de obra", "Transit", "1234-ABC", "Montajes Ebro S.L."),
            new VehiculoListaDto(Guid.NewGuid(), "Camión grúa", "Actros", "5678-DEF", "Montajes Ebro S.L.")
        ]);
        var consultasAntes = ConsultasDeLista(mediador);

        cut.Find(".paginador-tamano-select").Change("50");

        mediador.Enviadas.OfType<ObtenerVehiculosQuery>().Last().TamanoPagina.Should().Be(50);
        (ConsultasDeLista(mediador) - consultasAntes).Should().Be(1,
            "avisar a la paginación y refrescar la rejilla son dos formas de pedir lo mismo");
    }

    /// <summary>
    /// Buscar recarga la lista UNA vez, por el mismo motivo. El doble del
    /// mediador no filtra de verdad, así que el total se queda quieto y no
    /// puede colarse una repetición de QuickGrid en el recuento.
    /// </summary>
    [Fact]
    public async Task Buscar_sin_cambiar_el_total_hace_una_sola_consulta()
    {
        var (cut, mediador) = RenderizarConMediador(vehiculos:
        [
            new VehiculoListaDto(Guid.NewGuid(), "Furgoneta de obra", "Transit", "1234-ABC", "Montajes Ebro S.L."),
            new VehiculoListaDto(Guid.NewGuid(), "Camión grúa", "Actros", "5678-DEF", "Montajes Ebro S.L.")
        ]);
        var consultasAntes = ConsultasDeLista(mediador);

        await cut.InvokeAsync(() => CajaDeBusqueda(cut).Instance.ValorChanged.InvokeAsync("1234-ABC"));

        mediador.Enviadas.OfType<ObtenerVehiculosQuery>().Last().Busqueda.Should().Be("1234-ABC");
        (ConsultasDeLista(mediador) - consultasAntes).Should().Be(1,
            "avisar a la paginación y refrescar la rejilla son dos formas de pedir lo mismo");
    }
}
