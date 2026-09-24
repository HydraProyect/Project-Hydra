using Bunit;
using CaeManager.Application.Centros.Commands.CrearCentro;
using CaeManager.Application.Centros.Commands.EliminarCentros;
using CaeManager.Application.Clientes.Commands.EliminarClientes;
using CaeManager.Application.Centros.Queries.ObtenerCentros;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Common;
using CaeManager.Application.Visitas.Queries.ObtenerProximaVisitaPorCentro;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
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
        this.ConRolDeEscritura();
        ComponentFactories.AddStub<AcordeonAsignacionesCentro>();
    }

    private sealed class MediatorPorTipo : IMediator
    {
        public required IReadOnlyList<CentroListaDto> Centros { get; init; }
        public List<object> Enviadas { get; } = [];
        public int? EliminadosDelLote { get; set; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)(object)(Registrar(request) switch
            {
                ObtenerClientesParaSelectorQuery => (object)Array.Empty<ClienteSelectorDto>(),
                EliminarCentrosCommand lote => Result.Exito(new ResultadoEliminacionLoteDto(EliminadosDelLote ?? lote.Ids.Count, [])),
                ObtenerProximaVisitaPorCentroQuery => (IReadOnlyDictionary<Guid, IReadOnlyList<VisitaResumenDto>>)new Dictionary<Guid, IReadOnlyList<VisitaResumenDto>>(),
                ObtenerCentrosQuery q => new ResultadoPaginado<CentroListaDto>(
                    Centros, Centros.Count, q.Pagina, q.TamanoPagina),
                _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
            }));

        private object Registrar(object peticion)
        {
            Enviadas.Add(peticion);
            return peticion;
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
        public Task<string?> ObtenerRolOrigenAsync() => ObtenerRolEfectivoAsync();
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>("Administrador");
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    private static CentroListaDto Centro(string nombre, EstadoCentro estado = EstadoCentro.Vigente) => new(
        Guid.NewGuid(), nombre, "C-001", Guid.NewGuid(), "Refrielectric S.A.",
        Guid.NewGuid(), "Montajes Ebro S.L.", estado,
        CumplimientoPorcentaje: 100, RecuentosCentroDto.Vacio);

    private MediatorPorTipo _mediador = null!;

    private IRenderedComponent<Centros> Renderizar(params CentroListaDto[] centros)
    {
        _mediador = new MediatorPorTipo { Centros = centros };
        Services.AddScoped<IMediator>(_ => _mediador);
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

    /// <summary>
    /// El Workspace no es modal: con la ficha del centro abierta, la baja en lote
    /// se confirma desde la lista que queda detrás. La lista retira la ficha si su
    /// centro iba en el lote y cayó alguno; no la toca si era de otro centro ni si
    /// el lote no eliminó nada (la guarda «Eliminados > 0»).
    /// </summary>
    [Theory]
    [InlineData(true, null, false)]  // iba en el lote, cayó: se retira
    [InlineData(false, null, true)]  // no iba en el lote: se queda
    [InlineData(true, 0, true)]      // iba en el lote pero no cayó nada: se queda
    public async Task Eliminar_en_lote_retira_la_ficha_abierta_solo_si_su_centro_iba_y_cayo_alguno(
        bool ibaEnElLote, int? eliminados, bool seQuedaAbierta)
    {
        var elegido = Centro("Centro Logístico Norte");
        var otro = Centro("Centro Logístico Sur");
        var cut = Renderizar(elegido, otro);
        _mediador.EliminadosDelLote = eliminados;
        var workspace = Services.GetRequiredService<ContextWorkspaceService>();
        var abierto = ibaEnElLote ? elegido : otro;
        await cut.InvokeAsync(() => workspace.AbrirAsync(EntidadWorkspace.Centro, abierto.Id, abierto.Nombre, "informacion"));
        workspace.EstaAbierto.Should().BeTrue("control positivo: la ficha estaba abierta");

        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Selección múltiple").ClickAsync(new MouseEventArgs());
        await cut.Find("input[aria-label='Seleccionar el centro Centro Logístico Norte']").ChangeAsync(new ChangeEventArgs { Value = true });
        await cut.FindAll(".barra-acciones-lote button").Single(b => b.TextContent.Trim() == "Eliminar seleccionados")
            .ClickAsync(new MouseEventArgs());
        await cut.FindAll("[role=dialog] button").Single(b => b.TextContent.Trim() == "Eliminar").ClickAsync(new MouseEventArgs());

        _mediador.Enviadas.OfType<EliminarCentrosCommand>().Single().Ids.Should().Equal([elegido.Id],
            "el caso solo vale si el lote pidió ese centro y ninguno más");
        workspace.EstaAbierto.Should().Be(seQuedaAbierta);
    }
}
