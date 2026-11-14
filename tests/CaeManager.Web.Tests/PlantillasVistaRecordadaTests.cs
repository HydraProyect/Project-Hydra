using System.Text.Json;
using System.Web;
using Bunit;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Configuracion.Queries;
using CaeManager.Application.Plantillas.Queries.ObtenerDocumentosGenerados;
using CaeManager.Application.Plantillas.Queries.ObtenerPlantillasDocumento;
using CaeManager.Application.Plantillas.Queries.ObtenerTotalDocumentosGeneradosConAvisos;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Plantillas;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using PaginaPlantillas = CaeManager.Web.Features.Plantillas.Pages.Plantillas;

namespace CaeManager.Web.Tests;

/// <summary>
/// La vista recordada de /plantillas recuerda también cuál de las dos vistas (Catálogo o Generados) estaba abierta, con sus
/// filtros, y la restaura al volver: sin filtros en la URL, y también con <c>?Pestana=generados</c> a secas (elegir la vista no
/// es filtrar). Sin vista recordada, /plantillas abre Catálogo (DDL-080). Con filtros en la URL manda la URL.
/// </summary>
public class PlantillasVistaRecordadaTests : BunitContext
{
    private sealed class MediatorFalso(string? vistaRecordada) : IMediator
    {
        public List<object> Enviadas { get; } = [];

        public List<PlantillaDocumentoListaDto> Plantillas { get; } = [];
        public List<DocumentoGeneradoListaDto> Generados { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);
            return Task.FromResult((TResponse)Responder(request)!);
        }

        private object? Responder(object request) => request switch
        {
            ObtenerVistaRecordadaQuery => vistaRecordada,
            ObtenerPlantillasDocumentoQuery => (IReadOnlyList<PlantillaDocumentoListaDto>)Plantillas.ToList(),
            ObtenerTrabajadoresParaSelectorQuery => (IReadOnlyList<TrabajadorSelectorDto>)[],
            ObtenerTotalDocumentosGeneradosConAvisosQuery => Generados.Count(g => g.Estado == EstadoDocumentoGenerado.GeneradoConAvisos),
            ObtenerDocumentosGeneradosQuery => (IReadOnlyList<DocumentoGeneradoListaDto>)Generados.ToList(),
            _ when request.GetType().Name is "GuardarVistaRecordadaCommand" or "OlvidarVistaRecordadaCommand" => Result.Exito(),
            _ => throw new NotSupportedException($"Petición no prevista en este test: {request.GetType().Name}.")
        };

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

    public PlantillasVistaRecordadaTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.ConRolDeEscritura();
    }

    private static string Vista(params (string Parametro, string Valor)[] valores) =>
        JsonSerializer.Serialize(valores.ToDictionary(v => v.Parametro, v => v.Valor));

    private static MediatorFalso ConDocumentos(string? vistaRecordada)
    {
        var plantilla = new PlantillaDocumentoListaDto(Guid.NewGuid(), "Entrega de EPI", null, AmbitoAplicacion.Trabajador,
            FormatoOrigenPlantilla.PdfConCampos, EstadoPlantillaDocumento.Activa, Guid.NewGuid(), Guid.NewGuid(),
            EstadoConfiguracionPlantilla.Confirmada, 1, null, null);
        var mediador = new MediatorFalso(vistaRecordada);
        mediador.Plantillas.Add(plantilla);
        mediador.Generados.Add(new DocumentoGeneradoListaDto(Guid.NewGuid(), Guid.NewGuid(), plantilla.Id, plantilla.Nombre,
            Guid.NewGuid(), "Ana Pérez", null, "Montajes Roca", new DateTime(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc),
            EstadoDocumentoGenerado.Generado));
        return mediador;
    }

    private IRenderedComponent<PaginaPlantillas> Renderizar(MediatorFalso mediador, string url)
    {
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddLocalization();
        Services.AddScoped<ToastService>();
        Services.GetRequiredService<NavigationManager>().NavigateTo(url);

        var cut = Render<PaginaPlantillas>();
        cut.WaitForAssertion(() => cut.Markup.Should().NotContain("aria-busy=\"true\""));
        return cut;
    }

    private Dictionary<string, string> ParametrosDeLaUrl()
    {
        var consulta = HttpUtility.ParseQueryString(new Uri(Services.GetRequiredService<NavigationManager>().Uri).Query);
        return consulta.AllKeys.ToDictionary(k => k!, k => consulta[k]!);
    }

    private static string VistaAbierta(IRenderedComponent<PaginaPlantillas> cut) =>
        cut.Find(".segmentado-opcion[aria-pressed='true']").TextContent;

    [Fact]
    public void Sin_nada_en_la_url_restaura_la_vista_Generados_recordada_con_sus_filtros()
    {
        var mediador = ConDocumentos(Vista(("Pestana", "generados"), ("estado", "GeneradoConAvisos")));

        var cut = Renderizar(mediador, "plantillas");

        cut.WaitForAssertion(() => VistaAbierta(cut).Should().StartWith("Generados"));
        cut.WaitForAssertion(() => ParametrosDeLaUrl().Should().BeEquivalentTo(
            new Dictionary<string, string> { ["Pestana"] = "generados", ["estado"] = "GeneradoConAvisos" }));
        mediador.Enviadas.OfType<ObtenerVistaRecordadaQuery>().Should().Equal([new ObtenerVistaRecordadaQuery(PantallasConVistaRecordada.Plantillas)]);
    }

    [Fact]
    public void Pestana_generados_a_secas_restaura_los_filtros_recordados_de_Generados()
    {
        var mediador = ConDocumentos(Vista(("Pestana", "generados"), ("estado", "GeneradoConAvisos")));

        var cut = Renderizar(mediador, "plantillas?Pestana=generados");

        cut.WaitForAssertion(() => ParametrosDeLaUrl().Should().BeEquivalentTo(
            new Dictionary<string, string> { ["Pestana"] = "generados", ["estado"] = "GeneradoConAvisos" }));
        VistaAbierta(cut).Should().StartWith("Generados");
        mediador.Enviadas.OfType<ObtenerVistaRecordadaQuery>().Should().ContainSingle("elegir la vista no es filtrar: se lee lo recordado");
    }

    [Fact]
    public void Pestana_generados_a_secas_no_se_pisa_con_los_filtros_recordados_del_Catalogo()
    {
        var mediador = ConDocumentos(Vista(("ambito", "Vehiculo")));

        var cut = Renderizar(mediador, "plantillas?Pestana=generados");

        VistaAbierta(cut).Should().StartWith("Generados", "el enlace pide Generados y eso manda");
        ParametrosDeLaUrl().Should().BeEquivalentTo(new Dictionary<string, string> { ["Pestana"] = "generados" },
            "los filtros recordados son del Catálogo: no se aplican a Generados");
    }

    [Fact]
    public void Con_filtros_en_la_url_manda_la_url_y_no_se_lee_lo_recordado()
    {
        var mediador = ConDocumentos(Vista(("Pestana", "generados"), ("estado", "GeneradoConAvisos")));

        var cut = Renderizar(mediador, "plantillas?Pestana=generados&estado=Generado");

        ParametrosDeLaUrl().Should().BeEquivalentTo(
            new Dictionary<string, string> { ["Pestana"] = "generados", ["estado"] = "Generado" });
        VistaAbierta(cut).Should().StartWith("Generados");
        mediador.Enviadas.OfType<ObtenerVistaRecordadaQuery>().Should().BeEmpty();
    }

    [Fact]
    public void Sin_vista_recordada_abre_Catalogo_sin_tocar_la_url()
    {
        var mediador = ConDocumentos(null);

        var cut = Renderizar(mediador, "plantillas");

        VistaAbierta(cut).Should().StartWith("Catálogo");
        ParametrosDeLaUrl().Should().BeEmpty();
        mediador.Enviadas.OfType<ObtenerVistaRecordadaQuery>().Should().ContainSingle();
    }

    [Fact]
    public void La_vista_recordada_del_Catalogo_se_restaura_en_Catalogo()
    {
        var mediador = ConDocumentos(Vista(("ambito", "Trabajador")));

        var cut = Renderizar(mediador, "plantillas");

        cut.WaitForAssertion(() => ParametrosDeLaUrl().Should().BeEquivalentTo(
            new Dictionary<string, string> { ["ambito"] = "Trabajador" }));
        VistaAbierta(cut).Should().StartWith("Catálogo");
    }
}
