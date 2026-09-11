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
    /// <summary>Con filas, el panel monta AtajosListaTeclado y TextoFechaCopiable: importan módulos JS y avisan por toast.</summary>
    public DocumentosGeneradosPanelVacioPorFiltroTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddScoped<ToastService>();
    }

    private static readonly Guid PlantillaId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid OtraPlantillaId = Guid.Parse("66666666-6666-6666-6666-666666666666");

    private sealed class MediatorQueFiltraDeVerdad(
        IReadOnlyList<DocumentoGeneradoListaDto> generados,
        IReadOnlyList<TrabajadorSelectorDto>? trabajadores = null) : IMediator
    {
        public ObtenerDocumentosGeneradosQuery? UltimaConsulta { get; private set; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            switch (request)
            {
                case ObtenerPlantillasDocumentoQuery:
                    return Task.FromResult((TResponse)(object)(IReadOnlyList<PlantillaDocumentoListaDto>)[]);

                case ObtenerTrabajadoresParaSelectorQuery:
                    return Task.FromResult((TResponse)(object)(trabajadores ?? []));

                case ObtenerDocumentosGeneradosQuery q:
                    UltimaConsulta = q;
                    var visibles = generados
                        .Where(d => q.PlantillaDocumentoId is null || d.PlantillaDocumentoId == q.PlantillaDocumentoId)
                        .Where(d => q.TrabajadorId is null || d.TrabajadorId == q.TrabajadorId)
                        .ToList();
                    return Task.FromResult((TResponse)(object)(IReadOnlyList<DocumentoGeneradoListaDto>)visibles);

                case ObtenerTotalDocumentosGeneradosConAvisosQuery:
                    // A propósito ignora cualquier filtro: es justo lo que este arnés existe para comprobar.
                    return Task.FromResult((TResponse)(object)generados.Count(d => d.Estado == EstadoDocumentoGenerado.GeneradoConAvisos));

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
    private static DocumentoGeneradoListaDto Generado(Guid plantillaId, Guid? trabajadorId = null) => new(
        DocumentoGeneradoId: Guid.NewGuid(), DocumentoId: Guid.NewGuid(),
        PlantillaDocumentoId: plantillaId, PlantillaNombre: "Ficha de riesgos",
        TrabajadorId: trabajadorId ?? Guid.NewGuid(), TrabajadorNombreCompleto: "Juan Pérez",
        EmpresaId: null, EmpresaRazonSocial: null, GeneradoEnUtc: DateTime.UtcNow,
        Estado: EstadoDocumentoGenerado.Generado);

    private MediatorQueFiltraDeVerdad _mediator = null!;

    private IRenderedComponent<DocumentosGeneradosPanel> Renderizar(params DocumentoGeneradoListaDto[] generados) =>
        RenderizarCon(null, generados);

    /// <summary>Con trabajadores el segundo selector tiene opciones: sin ellas no se puede filtrar por trabajador desde la interfaz.</summary>
    private IRenderedComponent<DocumentosGeneradosPanel> RenderizarCon(
        IReadOnlyList<TrabajadorSelectorDto>? trabajadores, params DocumentoGeneradoListaDto[] generados)
    {
        _mediator = new MediatorQueFiltraDeVerdad(generados, trabajadores);
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

    private static void FiltrarPorTrabajador(IRenderedComponent<DocumentosGeneradosPanel> cut, Guid trabajadorId)
        => cut.FindAll(".barra-filtros select")[1].Change(trabajadorId.ToString());

    private static readonly Guid TrabajadorId = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly Guid OtroTrabajadorId = Guid.Parse("88888888-8888-8888-8888-888888888888");

    private static readonly IReadOnlyList<TrabajadorSelectorDto> Trabajadores =
    [
        new(TrabajadorId, "Marta Ruiz", "12345678Z", null),
        new(OtroTrabajadorId, "Juan Pérez", "87654321X", null)
    ];

    private static string DescripcionDelVacio(IRenderedComponent<DocumentosGeneradosPanel> cut)
        => cut.Find(".estado-vacio").TextContent;

    [Fact]
    public void Filtrando_por_una_plantilla_sin_generados_no_se_dice_que_no_se_genero_nada()
    {
        var cut = Renderizar(Generado(OtraPlantillaId));

        FiltrarPorPlantilla(cut, PlantillaId);

        cut.Markup.Should().Contain("Ningún documento generado con este filtro");
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
        cut.Markup.Should().NotContain("Ningún documento generado con este filtro");
    }

    [Fact]
    public void Quitar_los_filtros_los_limpia_en_la_consulta_y_devuelve_la_lista()
    {
        var cut = Renderizar(Generado(OtraPlantillaId));
        FiltrarPorPlantilla(cut, PlantillaId);
        cut.Markup.Should().Contain("Ningún documento generado con este filtro", "es el punto de partida de este caso");

        cut.Find(".estado-vacio button").Click();

        _mediator.UltimaConsulta!.PlantillaDocumentoId.Should().BeNull(
            "«Quitar los filtros» tiene que limpiar el filtro de verdad, no solo repintar el estado");
        _mediator.UltimaConsulta!.TrabajadorId.Should().BeNull();
        cut.Find("table.tabla-datos").TextContent.Should().Contain("Ficha de riesgos");
        cut.Markup.Should().NotContain("Ningún documento generado con este filtro");
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

        cut.Markup.Should().Contain("Ningún documento generado con este filtro");
        cut.Markup.Should().NotContain("Hay documentos generados");
    }

    [Fact]
    public void Con_resultados_no_se_pinta_ningun_estado_vacio()
    {
        var cut = Renderizar(Generado(PlantillaId));

        FiltrarPorPlantilla(cut, PlantillaId);

        cut.Markup.Should().NotContain("Ningún documento generado con este filtro");
        cut.Markup.Should().NotContain("Todavía no se ha generado ningún documento");
        cut.Find("table.tabla-datos").TextContent.Should().Contain("Ficha de riesgos");
    }

    /// <summary>
    /// Revisión de Codex: la descripción daba por elegidos los dos filtros
    /// («ni al trabajador seleccionado») aunque solo hubiera uno puesto.
    /// </summary>
    [Fact]
    public void Con_solo_la_plantilla_filtrada_el_vacio_no_nombra_un_trabajador_que_nadie_eligio()
    {
        var cut = RenderizarCon(Trabajadores, Generado(OtraPlantillaId, OtroTrabajadorId));

        FiltrarPorPlantilla(cut, PlantillaId);

        DescripcionDelVacio(cut).Should().Contain("Ninguno corresponde a la plantilla seleccionada.")
            .And.NotContain("trabajador", "no hay ningún trabajador elegido que pueda dejar fuera a nadie");
    }

    [Fact]
    public void Con_solo_el_trabajador_filtrado_el_vacio_no_nombra_una_plantilla_que_nadie_eligio()
    {
        var cut = RenderizarCon(Trabajadores, Generado(PlantillaId, OtroTrabajadorId));

        FiltrarPorTrabajador(cut, TrabajadorId);

        DescripcionDelVacio(cut).Should().Contain("Ninguno corresponde al trabajador seleccionado.")
            .And.NotContain("plantilla seleccionada");
    }

    [Fact]
    public void Con_los_dos_filtros_puestos_el_vacio_nombra_los_dos()
    {
        var cut = RenderizarCon(Trabajadores, Generado(OtraPlantillaId, OtroTrabajadorId));

        FiltrarPorPlantilla(cut, PlantillaId);
        FiltrarPorTrabajador(cut, TrabajadorId);

        DescripcionDelVacio(cut).Should().Contain("Ninguno corresponde a la plantilla ni al trabajador seleccionados.");
    }

    /// <summary>
    /// Revisión de Codex: la columna de acciones no tenía encabezado, así que
    /// quien recorre la tabla con lector de pantalla no sabía de qué columna
    /// venían «Ver PDF» y «Gestionar». A la vista sigue sin rótulo.
    /// </summary>
    [Fact]
    public void La_columna_de_acciones_se_rotula_para_los_lectores_de_pantalla()
    {
        var cut = Renderizar(Generado(PlantillaId));

        var encabezados = cut.FindAll("table.tabla-datos thead th");
        encabezados.Should().HaveCount(6);
        encabezados[5].TextContent.Trim().Should().Be("Acciones");
        encabezados[5].QuerySelector("span")!.ClassName.Should().Be("encabezado-acciones-generados",
            "el rótulo se oculta a la vista con la clase del propio panel, no con una de otro componente");
    }
}
