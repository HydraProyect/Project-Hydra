using Bunit;
using CaeManager.Application.Plantillas.Queries.ObtenerDocumentosGenerados;
using CaeManager.Application.Plantillas.Queries.ObtenerPlantillasDocumento;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Domain.Plantillas;
using CaeManager.Web.Features.Plantillas.Components;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// «Documentos generados» es la última de las nueve del defecto sistémico, y la
/// única que no es una página: es el panel de la pestaña «Generados» de
/// Plantillas. Con una plantilla o un trabajador elegido y cero resultados
/// decía <i>«todavía no se ha generado ningún documento»</i>, que manda a
/// generar de nuevo algo que probablemente ya existe para otra plantilla u otro
/// trabajador.
///
/// <para>
/// El arnés es el mismo que <see cref="DocumentosGeneradosPanelAvisosTests"/>,
/// con un doble que filtra de verdad — uno que devolviera siempre lo mismo
/// dejaría sin observar el cambio de estado al filtrar.
/// </para>
/// </summary>
public class DocumentosGeneradosPanelVacioPorFiltroTests : BunitContext
{
    private static readonly Guid PlantillaId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid OtraPlantillaId = Guid.Parse("66666666-6666-6666-6666-666666666666");

    private sealed class MediatorQueFiltraDeVerdad(IReadOnlyList<DocumentoGeneradoListaDto> generados) : IMediator
    {
        public ObtenerDocumentosGeneradosQuery? UltimaConsulta { get; private set; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            switch (request)
            {
                case ObtenerPlantillasDocumentoQuery:
                    return Task.FromResult((TResponse)(object)(IReadOnlyList<PlantillaDocumentoListaDto>)[]);

                case ObtenerTrabajadoresParaSelectorQuery:
                    return Task.FromResult((TResponse)(object)(IReadOnlyList<TrabajadorSelectorDto>)[]);

                case ObtenerDocumentosGeneradosQuery q:
                    UltimaConsulta = q;
                    var visibles = generados
                        .Where(d => q.PlantillaDocumentoId is null || d.PlantillaDocumentoId == q.PlantillaDocumentoId)
                        .Where(d => q.TrabajadorId is null || d.TrabajadorId == q.TrabajadorId)
                        .ToList();
                    return Task.FromResult((TResponse)(object)(IReadOnlyList<DocumentoGeneradoListaDto>)visibles);

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

    /// <summary>
    /// Los argumentos van por nombre a propósito. Escribirlos por posición puso
    /// la plantilla en el hueco de <c>DocumentoId</c> y cuatro casos siguieron
    /// en verde por el motivo equivocado: comparaban contra una plantilla
    /// distinta y salían vacíos igual. Solo cayó el que exigía ver la fila.
    /// </summary>
    private static DocumentoGeneradoListaDto Generado(Guid plantillaId) => new(
        DocumentoGeneradoId: Guid.NewGuid(), DocumentoId: Guid.NewGuid(),
        PlantillaDocumentoId: plantillaId, PlantillaNombre: "Ficha de riesgos",
        TrabajadorId: Guid.NewGuid(), TrabajadorNombreCompleto: "Juan Pérez",
        EmpresaId: null, EmpresaRazonSocial: null, GeneradoEnUtc: DateTime.UtcNow,
        Estado: EstadoDocumentoGenerado.Generado);

    private MediatorQueFiltraDeVerdad _mediator = null!;

    private IRenderedComponent<DocumentosGeneradosPanel> Renderizar(params DocumentoGeneradoListaDto[] generados)
    {
        _mediator = new MediatorQueFiltraDeVerdad(generados);
        Services.AddScoped<IMediator>(_ => _mediator);
        return Render<DocumentosGeneradosPanel>();
    }

    /// <summary>
    /// El selector de plantilla es el primero de la barra y no rebota: su
    /// <c>onchange</c> recarga en el acto. El panel no tiene buscador de texto,
    /// así que aquí no aplica la trampa del rebote de <c>CampoTexto</c>.
    /// </summary>
    private static void FiltrarPorPlantilla(IRenderedComponent<DocumentosGeneradosPanel> cut, Guid plantillaId)
        => cut.FindAll(".barra-filtros select")[0].Change(plantillaId.ToString());

    [Fact]
    public void Filtrando_por_una_plantilla_sin_generados_no_se_dice_que_no_se_genero_nada()
    {
        var cut = Renderizar(Generado(OtraPlantillaId));

        FiltrarPorPlantilla(cut, PlantillaId);

        cut.Markup.Should().Contain("Ningún documento con estos filtros");
        cut.Markup.Should().Contain("Quitar los filtros");
        cut.Markup.Should().NotContain("Todavía no se ha generado ningún documento",
            "sí se generó uno, con otra plantilla: decir lo contrario manda a generarlo otra vez");
    }

    [Fact]
    public void Sin_filtros_y_sin_generados_sigue_diciendo_que_no_se_ha_generado_ninguno()
    {
        var cut = Renderizar();

        cut.Markup.Should().Contain("Todavía no se ha generado ningún documento");
        cut.Markup.Should().Contain("Genera uno individual o en lote desde una plantilla confirmada.");
        cut.Markup.Should().NotContain("Ningún documento con estos filtros");
    }

    [Fact]
    public void Quitar_los_filtros_los_limpia_en_la_consulta_y_devuelve_la_lista()
    {
        var cut = Renderizar(Generado(OtraPlantillaId));
        FiltrarPorPlantilla(cut, PlantillaId);
        cut.Markup.Should().Contain("Ningún documento con estos filtros", "es el punto de partida de este caso");

        cut.Find(".estado-vacio button").Click();

        _mediator.UltimaConsulta!.PlantillaDocumentoId.Should().BeNull(
            "«Quitar los filtros» tiene que limpiar el filtro de verdad, no solo repintar el estado");
        _mediator.UltimaConsulta!.TrabajadorId.Should().BeNull();
        cut.Find("table.tabla-datos").TextContent.Should().Contain("Ficha de riesgos");
        cut.Markup.Should().NotContain("Ningún documento con estos filtros");
    }

    /// <summary>
    /// El filtrado es de servidor: la consulta devuelve el total YA filtrado.
    /// La barrera va delante — una aserción de ausencia sola sería verde vacío.
    /// </summary>
    [Fact]
    public void La_copia_no_afirma_cuantos_documentos_hay_generados()
    {
        var cut = Renderizar(Generado(OtraPlantillaId));

        FiltrarPorPlantilla(cut, PlantillaId);

        cut.Markup.Should().Contain("Ningún documento con estos filtros");
        cut.Markup.Should().NotContain("Hay documentos generados");
    }

    [Fact]
    public void Con_resultados_no_se_pinta_ningun_estado_vacio()
    {
        var cut = Renderizar(Generado(PlantillaId));

        FiltrarPorPlantilla(cut, PlantillaId);

        cut.Markup.Should().NotContain("Ningún documento con estos filtros");
        cut.Markup.Should().NotContain("Todavía no se ha generado ningún documento");
        cut.Find("table.tabla-datos").TextContent.Should().Contain("Ficha de riesgos");
    }
}
