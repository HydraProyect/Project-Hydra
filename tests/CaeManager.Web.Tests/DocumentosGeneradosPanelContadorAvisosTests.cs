using Bunit;
using CaeManager.Application.Plantillas.Queries.ObtenerDocumentosGenerados;
using CaeManager.Application.Plantillas.Queries.ObtenerPlantillasDocumento;
using CaeManager.Application.Plantillas.Queries.ObtenerTotalDocumentosGeneradosConAvisos;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Domain.Plantillas;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Plantillas.Components;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Defecto encontrado el 2026-09-08 al cerrar el vacío-por-filtro (PR #507):
/// <c>DocumentosGeneradosPanel</c> notificaba a su padre
/// <c>_documentosGenerados.Count</c> —la lista YA filtrada de la tabla—
/// como si fuera el número de la pestaña "Generados" de Plantillas, así que
/// aplicar un filtro hacía bajar ese número. Plantillas TALVEG.dc.html
/// resuelve la pregunta de producto que dejó abierta la nota: ese badge no
/// es un total de la pestaña — es "N documentos generados con avisos,
/// pendientes de revisar" (tono aviso), ajeno al filtro de la tabla. Este
/// arnés comprueba las dos mitades del contrato: qué número se notifica, y
/// que filtrar la tabla no lo vuelve a tocar.
/// </summary>
public class DocumentosGeneradosPanelContadorAvisosTests : BunitContext
{
    /// <summary>Con filas, el panel monta AtajosListaTeclado y TextoFechaCopiable: importan módulos JS y avisan por toast.</summary>
    public DocumentosGeneradosPanelContadorAvisosTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddScoped<ToastService>();
    }

    private static readonly Guid PlantillaId = Guid.Parse("77777777-7777-7777-7777-777777777777");

    /// <summary>El panel lanza cuatro consultas distintas por el mismo IMediator — responde por tipo, no una única respuesta para todas.</summary>
    private sealed class MediatorFalso(int avisosPendientes, IReadOnlyList<DocumentoGeneradoListaDto> filaDeTabla) : IMediator
    {
        public int LlamadasATotalAvisos { get; private set; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            switch (request)
            {
                case ObtenerPlantillasDocumentoQuery:
                    return Task.FromResult((TResponse)(object)(IReadOnlyList<PlantillaDocumentoListaDto>)[]);

                case ObtenerTrabajadoresParaSelectorQuery:
                    return Task.FromResult((TResponse)(object)(IReadOnlyList<TrabajadorSelectorDto>)[]);

                case ObtenerTotalDocumentosGeneradosConAvisosQuery:
                    LlamadasATotalAvisos++;
                    return Task.FromResult((TResponse)(object)avisosPendientes);

                case ObtenerDocumentosGeneradosQuery:
                    return Task.FromResult((TResponse)(object)filaDeTabla);

                default:
                    throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.");
            }
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

    private static DocumentoGeneradoListaDto Generado(EstadoDocumentoGenerado estado) => new(
        DocumentoGeneradoId: Guid.NewGuid(), DocumentoId: Guid.NewGuid(),
        PlantillaDocumentoId: PlantillaId, PlantillaNombre: "Ficha de riesgos",
        TrabajadorId: null, TrabajadorNombreCompleto: null,
        EmpresaId: null, EmpresaRazonSocial: null, GeneradoEnUtc: DateTime.UtcNow,
        Estado: estado);

    [Fact]
    public void Notifica_el_total_de_avisos_pendientes_al_cargar()
    {
        var mediator = new MediatorFalso(avisosPendientes: 3, filaDeTabla: [Generado(EstadoDocumentoGenerado.Generado)]);
        Services.AddScoped<IMediator>(_ => mediator);
        var notificado = -1;

        Render<DocumentosGeneradosPanel>(parametros => parametros
            .Add(p => p.AvisosPendientesCambiado, v => notificado = v));

        notificado.Should().Be(3);
    }

    [Fact]
    public void Filtrar_la_tabla_no_cambia_el_total_de_avisos_pendientes_notificado()
    {
        // La tabla, filtrada, muestra un documento SIN avisos con esta plantilla;
        // el total de avisos pendientes (3) es del ámbito completo del panel, no
        // de lo que queda visible en la tabla tras filtrar — si el defecto
        // reapareciera, este valor cambiaría (o se notificaría una segunda vez).
        var mediator = new MediatorFalso(avisosPendientes: 3, filaDeTabla: [Generado(EstadoDocumentoGenerado.Generado)]);
        Services.AddScoped<IMediator>(_ => mediator);
        var valoresNotificados = new List<int>();

        var cut = Render<DocumentosGeneradosPanel>(parametros => parametros
            .Add(p => p.AvisosPendientesCambiado, v => valoresNotificados.Add(v)));

        cut.FindAll(".barra-filtros select")[0].Change(PlantillaId.ToString());

        valoresNotificados.Should().ContainSingle().Which.Should().Be(3,
            "AvisosPendientesCambiado se pide una sola vez al iniciar — filtrar la tabla no debe volver a dispararlo ni cambiar el valor");
        mediator.LlamadasATotalAvisos.Should().Be(1);
    }
}
