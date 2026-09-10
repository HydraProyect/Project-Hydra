using Bunit;
using CaeManager.Application.Centros.Commands.CrearCentro;
using CaeManager.Application.Centros.Queries.ObtenerCentros;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Common;
using CaeManager.Application.Visitas.Queries.ObtenerProximaVisitaPorCentro;
using CaeManager.Domain.Centros;
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
/// Lista de Centros contra su mockup Gen 2 (Lista Centros TALVEG.dc.html).
///
/// <para>
/// Lo que se comprueba aquí es lo que la PÁGINA pinta. El acordeón de
/// asignaciones se sustituye por un stub: sus enlaces condicionales a Centro
/// 360 son precisamente el defecto que el mockup señala, y el enlace nuevo no
/// puede depender de ellos. Si el test viera el acordeón real, un enlace suyo
/// podría dar verde por el camino equivocado. Que esos enlaces del acordeón no
/// se sumen al de la página —un solo camino por fila— lo mide, con el acordeón
/// real, <see cref="CentrosEnlaceUnicoCentro360Tests"/>.
/// </para>
/// </summary>
public class CentrosListaGen2Tests : BunitContext
{
    /// <summary>La página monta AtajosListaTeclado, que importa ./js/atajos-lista.js.</summary>
    public CentrosListaGen2Tests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        ComponentFactories.AddStub<AcordeonAsignacionesCentro>();
    }

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

    private static CentroListaDto Centro(string nombre, EstadoCentro estado = EstadoCentro.Vigente) => new(
        Guid.NewGuid(), nombre, "C-001", Guid.NewGuid(), "Refrielectric S.A.",
        Guid.NewGuid(), "Montajes Ebro S.L.", estado,
        CumplimientoPorcentaje: 100, RecuentosCentroDto.Vacio);

    private IRenderedComponent<Centros> Renderizar(params CentroListaDto[] centros)
    {
        Services.AddScoped<IMediator>(_ => new MediatorPorTipo { Centros = centros });
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped<ICurrentUserService, UsuarioActualFalso>();
        Services.AddScoped<IValidator<CrearCentroCommand>>(_ => new InlineValidator<CrearCentroCommand>());

        Services.GetRequiredService<NavigationManager>().NavigateTo("centros");

        return Render<Centros>();
    }

    [Fact]
    public void El_titulo_es_la_cabecera_de_pagina_Gen_2()
    {
        var cut = Renderizar(Centro("Centro Logístico Norte"));

        cut.Find("header.cabecera-pagina h1.titulo-pagina").TextContent.Trim().Should().Be("Centros");
    }

    [Fact]
    public async Task Al_expandir_un_centro_aparece_el_camino_a_Centro_360()
    {
        var centro = Centro("Centro Logístico Norte");
        var cut = Renderizar(centro);

        // Barrera: la fila está pintada y colapsada. Sin ella, la ausencia
        // del enlace sería un verde vacío.
        cut.Markup.Should().Contain("Centro Logístico Norte");
        cut.FindAll("a.acordeon-centro-enlace-360").Should().BeEmpty(
            "el contenido expandido solo se monta al expandir la fila");

        await cut.Find("button.boton-expandir-fila").ClickAsync(new MouseEventArgs());

        var enlace = cut.Find("a.acordeon-centro-enlace-360");
        enlace.GetAttribute("href").Should().Be($"/centros/{centro.Id}");
        enlace.TextContent.Should().Contain("Ver el centro completo");
        cut.Find(".acordeon-centro-titulo").TextContent.Should().Be("Asignaciones con incidencias");
    }

    /// <summary>
    /// El enlace no depende de lo que haya dentro del acordeón ni del estado
    /// del centro: un centro bloqueado y uno vigente lo llevan igual, y cada
    /// uno apunta a SU ficha.
    /// </summary>
    [Fact]
    public async Task Cada_centro_expandido_enlaza_a_su_propio_Centro_360_sea_cual_sea_su_estado()
    {
        var bloqueado = Centro("Obra Valdés — Fase 2", EstadoCentro.Bloqueado);
        var vigente = Centro("Almacén Sur", EstadoCentro.Vigente);
        var cut = Renderizar(bloqueado, vigente);

        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Expandir todos").ClickAsync(new MouseEventArgs());

        cut.FindAll("a.acordeon-centro-enlace-360")
            .Select(a => a.GetAttribute("href"))
            .Should().Equal($"/centros/{bloqueado.Id}", $"/centros/{vigente.Id}");
    }
}
