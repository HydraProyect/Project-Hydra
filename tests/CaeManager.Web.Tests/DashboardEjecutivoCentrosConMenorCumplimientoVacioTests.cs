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
        var texto = RenderizarTextoVacio();

        texto.Should().Be("Todavía no hay documentación de trabajador que evaluar: ningún centro con trabajadores asignados la pide.");
        texto.Should().NotContainEquivalentOf("obligatori", "es configuración (ResolucionTipoDocumentoCentro.Aplica), no una obligación legal");
    }

    /// <summary>
    /// La lista llega vacía también cuando un centro SÍ pide documentación de
    /// trabajador pero no tiene a nadie asignado (CalculoEstadoCentroService
    /// cuenta cero requeridos sin asignaciones activas — premisa probada contra
    /// Postgres en CalculoEstadoCentroServiceTests). El DTO no distingue ese
    /// caso del de un centro que no pide nada, así que el texto tiene que ser
    /// cierto en los dos: vacío ⇔ ningún centro CON trabajadores asignados la
    /// pide. "Ningún centro pide…" mandaba a revisar la configuración de un
    /// centro que ya la tiene bien.
    /// </summary>
    [Fact]
    public void El_vacio_del_KPI_no_atribuye_la_ausencia_a_que_ningun_centro_pida_documentacion()
    {
        var texto = RenderizarTextoVacio();

        texto.Should().NotStartWith("Ningún centro pide",
            "un centro que pide documentación de trabajador pero sin trabajadores asignados también deja la lista vacía");
        texto.Should().Contain("con trabajadores asignados");
    }

    private string RenderizarTextoVacio()
    {
        Services.AddScoped<IMediator>(_ => new MediatorFalso());
        Services.AddScoped<ToastService>();
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        var cut = Render<DashboardEjecutivo>();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("texto-vacio-seccion"));
        return cut.Find("p.texto-vacio-seccion").TextContent;
    }
}
