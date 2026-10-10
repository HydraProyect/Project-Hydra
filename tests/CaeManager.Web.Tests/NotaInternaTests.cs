using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.TiposDocumento.Queries.ObtenerEstadoTipoDocumento;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Documentos.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaeManager.Web.Tests;

/// <summary>
/// La pieza <see cref="NotaInterna"/> (tarjeta «Nota interna» del lateral de las fichas 360) y su uso en Tipo de
/// documento 360. El de Subcontrata 360, que además edita, está en <see cref="Subcontrata360PaginaTests"/>.
/// bUnit no evalúa CSS: que el texto conserve los saltos de línea (<c>white-space: pre-line</c>) no se afirma aquí.
/// </summary>
public class NotaInternaTests : BunitContext
{
    public NotaInternaTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
    }

    // ── La pieza ──────────────────────────────────────────────────────────

    [Fact]
    public void Con_texto_pinta_la_tarjeta_compacta_con_su_titulo_la_nota_y_el_pie()
    {
        var cut = Render<NotaInterna>(p => p.Add(x => x.Texto, "Llamar antes de las 10.\nPreguntar por Leire."));

        var pieza = cut.Find("[data-pieza=nota-interna]");
        pieza.QuerySelector(".tarjeta.tarjeta-compacta").Should().NotBeNull();
        pieza.QuerySelector(".tarjeta-titulo")!.TextContent.Trim().Should().Be("Nota interna");
        pieza.QuerySelector(".nota-interna-texto")!.TextContent.Should().Be("Llamar antes de las 10.\nPreguntar por Leire.");
        pieza.QuerySelector(".nota-interna-pie")!.TextContent.Trim().Should().Be("Solo visible para tu equipo.");
        pieza.QuerySelectorAll("[data-vacia]").Should().BeEmpty();
        pieza.QuerySelectorAll(".tarjeta-acciones").Should().BeEmpty("sin Acciones la tarjeta es de solo lectura");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n ")]
    public void Sin_texto_pinta_el_vacio_y_conserva_el_pie(string? texto)
    {
        var cut = Render<NotaInterna>(p => p.Add(x => x.Texto, texto));

        cut.Find("[data-vacia=true]").TextContent.Trim().Should().Be("Sin nota interna.");
        cut.Find(".nota-interna-pie").TextContent.Trim().Should().Be("Solo visible para tu equipo.");
    }

    [Fact]
    public void La_accion_de_la_cabecera_es_la_que_le_pasa_quien_la_usa()
    {
        var cut = Render<NotaInterna>(p => p
            .Add(x => x.Texto, "Nota.")
            .Add(x => x.Acciones, "<button type=\"button\" id=\"accion-de-prueba\">Editar →</button>"));

        cut.Find("[data-pieza=nota-interna] .tarjeta-acciones #accion-de-prueba").TextContent.Should().Be("Editar →");
    }

    // ── Tipo de documento 360 ─────────────────────────────────────────────

    private sealed class MediatorFalso(EstadoTipoDocumentoDto estado) : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            request is ObtenerEstadoTipoDocumentoQuery
                ? Task.FromResult((TResponse)(object)estado)
                : throw new NotSupportedException($"Petición no prevista en este test: {request.GetType().Name}.");

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

    /// <summary>La página monta DrawerGestionDocumento, que los inyecta; cerrado, nadie los toca.</summary>
    private sealed class AlmacenArchivosQueNadieDebeTocar : IFileStorageService
    {
        private static Exception NoDeberia() => new NotSupportedException("Con el drawer cerrado no se abre ningún archivo.");

        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) => throw NoDeberia();
        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) => throw NoDeberia();
        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) => throw NoDeberia();
    }

    private sealed class ConversorQueNadieDebeTocar : IConversorWordPdfService
    {
        public Task<byte[]> ConvertirAPdfAsync(byte[] contenidoDocx, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Con el drawer cerrado no se convierte nada.");
    }

    private static readonly Guid TipoId = Guid.NewGuid();

    private static EstadoTipoDocumentoDto Estado(string? notas) => new(
        TipoId, "Formación Art. 19", VigenciaMeses: 12, AplicaVencimientoAutomatico: true, RequisitoDocumental.Si,
        NaturalezaJuridica.RequisitoCliente, SeSolicitaA: null, Aliases: [],
        Cumplimiento: new FraccionCumplimiento(0, 0), Centros: 0, Trabajadores: 0, Recuentos: [], Filas: [],
        TotalFiltradas: 0, Pagina: 1, TamanoPagina: ObtenerEstadoTipoDocumentoQuery.TamanoPaginaPorDefecto, Notas: notas);

    private IRenderedComponent<TipoDocumentoDetalle> RenderizarTipo(string? notas, string rol)
    {
        this.ConRolDeEscritura(rol);
        Services.AddScoped<IMediator>(_ => new MediatorFalso(Estado(notas)));
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped<IFileStorageService, AlmacenArchivosQueNadieDebeTocar>();
        Services.AddScoped<IConversorWordPdfService, ConversorQueNadieDebeTocar>();
        Services.AddScoped<ITenantActual>(_ => new SeleccionEmpresaGestionadaDePrueba());
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        SetRendererInfo(new RendererInfo("Server", isInteractive: true));

        Services.GetRequiredService<NavigationManager>().NavigateTo($"documentos/tipos/{TipoId}");
        var cut = Render<TipoDocumentoDetalle>(p => p.Add(x => x.TipoDocumentoId, TipoId));
        cut.WaitForAssertion(() => cut.FindAll("[data-pieza=lateral]").Should().ContainSingle());
        return cut;
    }

    [Fact]
    public void Tipo_de_documento_360_pinta_la_nota_del_tipo_al_final_del_lateral()
    {
        var cut = RenderizarTipo("Pedir siempre el certificado con las horas.", Roles.GestorCae);

        var lateral = cut.Find("[data-pieza=lateral]");
        lateral.LastElementChild!.GetAttribute("data-pieza").Should().Be("nota-interna", "la nota cierra el lateral, tras «Información»");
        var nota = lateral.QuerySelector("[data-pieza=nota-interna]")!;
        nota.QuerySelector(".tarjeta-titulo")!.TextContent.Trim().Should().Be("Nota interna");
        nota.TextContent.Should().Contain("Pedir siempre el certificado con las horas.").And.Contain("Solo visible para tu equipo.");
    }

    [Fact]
    public void Tipo_de_documento_360_sin_nota_pinta_el_vacio()
    {
        var cut = RenderizarTipo(notas: null, Roles.GestorCae);

        cut.Find("[data-pieza=nota-interna] [data-vacia=true]").TextContent.Trim().Should().Be("Sin nota interna.");
    }

    /// <summary>La nota se edita donde el resto del tipo (Configuración), y allí solo entra el Administrador.</summary>
    [Fact]
    public void En_Tipo_de_documento_360_solo_el_Administrador_ve_Editar_en_la_nota_y_lleva_a_la_configuracion_del_tipo()
    {
        var cut = RenderizarTipo("Nota.", Roles.Administrador);

        cut.Find("[data-pieza=nota-interna] .tarjeta-acciones button").Click();

        Services.GetRequiredService<NavigationManager>().Uri.Should().EndWith("/tipos-documento");
    }

    [Fact]
    public void En_Tipo_de_documento_360_un_Gestor_CAE_lee_la_nota_sin_Editar()
    {
        var cut = RenderizarTipo("Nota.", Roles.GestorCae);

        cut.FindAll("[data-pieza=nota-interna] button").Should().BeEmpty();
    }
}
