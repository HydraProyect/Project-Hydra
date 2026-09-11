using Bunit;
using CaeManager.Application.Dashboard.Catalogo;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.DashboardEjecutivo.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaeManager.Web.Tests;

/// <summary>
/// El KPI "Centros con menor cumplimiento" agrega CalculoEstadoCentroService
/// (mismo criterio de ResolucionTipoDocumentoCentro.Aplica que Alertas.razor):
/// un centro cuenta como que "pide" documentación de trabajador tanto si tiene
/// fila propia como si sigue el valor general del tipo. "Obligatoria" sugería
/// una norma legal cuando es configuración — TALVEG orienta, no impone.
/// </summary>
public class DashboardEjecutivoCentrosConMenorCumplimientoVacioTests : BunitContext
{
    public DashboardEjecutivoCentrosConMenorCumplimientoVacioTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private sealed class MediatorFalso : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)(object)(request switch
            {
                ObtenerPreferenciaDashboardQuery => new[] { CatalogoKpis.CentrosConMenorCumplimiento },
                ObtenerDashboardEjecutivoQuery => new DashboardEjecutivoDto(
                    TotalTenants: 1, TrabajadoresActivos: 0, Centros: 0, VisitasProgramadas: 0, VisitasUrgentes: 0,
                    DocumentosVigentes: 0, DocumentosProximos: 0, DocumentosUrgentes: 0, DocumentosVencidos: 0,
                    TasaCumplimiento: 100, PorcentajeCumplimientoDocumental: null,
                    CentrosConMenorCumplimiento: [], IncidenciasAbiertas: 0, IncidenciasPorGravedad: [],
                    TiempoMedioResolucionIncidenciasDias: null, ConfianzaMediaIa: null, CosteIaMesActual: 0m,
                    TiempoMedioProcesamientoIaMs: null, FacturacionEstimadaMesActual: 0m, Bpo: KpisBpoDto.Vacio,
                    TenantsConPresupuestoIaExcedido: []),
                ObtenerEstadisticasAprobacionDocumentoQuery => new EstadisticasAprobacionDocumentoDto(0, 0),
                ObtenerDesgloseDashboardQuery => new DesgloseDashboardDto([], [], []),
                _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
            })!);

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

    [Fact]
    public void El_vacio_del_KPI_no_llama_obligatoria_a_la_documentacion_de_trabajador()
    {
        Services.AddScoped<IMediator>(_ => new MediatorFalso());
        Services.AddScoped<ToastService>();
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        var cut = Render<DashboardEjecutivo>();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("texto-vacio-seccion"));
        var texto = cut.Find("p.texto-vacio-seccion").TextContent;

        texto.Should().Be("Ningún centro pide todavía documentación de trabajador que evaluar.");
        texto.Should().NotContainEquivalentOf("obligatori", "es configuración (ResolucionTipoDocumentoCentro.Aplica), no una obligación legal");
    }
}
