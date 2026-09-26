using Bunit;
using CaeManager.Application.Alertas.Queries.ObtenerSugerenciasPreventivas;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Application.Documentos.Queries.ObtenerFirmaGuardadaUsuario;
using CaeManager.Application.Documentos.Queries.ObtenerFirmasEnCampoDocumento;
using CaeManager.Application.Documentos.Queries.ObtenerSelloEmpresa;
using CaeManager.Infrastructure.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentoPorId;
using CaeManager.Application.Documentos.Queries.ObtenerValidacionOficialDocumento;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Documentos.Components;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// REC-097 (DEC-21): la pestaña "Versiones" se retiró del panel de Documento
/// porque anunciaba un versionado de archivos que no existe — mismo criterio
/// que #411 con "2fa" en el hub de Configuración (ver
/// <see cref="ConfiguracionTests"/> para el mismo patrón de test). Sin este
/// test, reintroducir la entrada no lo detectaría nadie hasta que un Gestor
/// CAE clicara la pestaña.
/// </summary>
public class DocumentoWorkspacePanelTests : BunitContext
{
    public DocumentoWorkspacePanelTests()
    {
        // Firma en campo y Modal importan módulos JS; queda fuera de lo que se observa aquí.
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.ConRolDeEscritura();
        Services.AddLocalization();
    }

    /// <summary>El panel lanza dos consultas distintas por el mismo IMediator — responde por tipo.</summary>
    private sealed class MediatorDocumento : IMediator
    {
        public required DocumentoDetalleDto Detalle { get; init; }
        public bool SinFirmas { get; init; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)(request switch
            {
                ObtenerDocumentoPorIdQuery => (object?)Detalle,
                ObtenerValidacionOficialDocumentoQuery => null,
                ObtenerFirmasEnCampoDocumentoQuery => SinFirmas ? (IReadOnlyList<FirmaEnCampoDocumentoDto>)[] : (IReadOnlyList<FirmaEnCampoDocumentoDto>)
                    [new FirmaEnCampoDocumentoDto(Guid.NewGuid(), "Lucía Prieto", "GestorCae", DateTime.UtcNow, null, "hash")],
                ObtenerFirmaGuardadaUsuarioQuery => null,
                ObtenerSelloEmpresaQuery => null,
                ObtenerSugerenciasPreventivasQuery => (IReadOnlyList<SugerenciaPreventivaDto>)[],
                ObtenerDocumentosQuery consulta => new ResultadoPaginado<DocumentoListaDto>(
                    [new DocumentoListaDto(Detalle.Id, Detalle.Ambito, Detalle.PropietarioNombre, Detalle.TipoDocumentoNombre,
                        Detalle.FechaEmision, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(5), EstadoDocumento.Urgente, null, [])],
                    1, consulta.Pagina, consulta.TamanoPagina),
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

    private static DocumentoDetalleDto Detalle() => new(
        Id: Guid.NewGuid(),
        Ambito: AmbitoAplicacion.Empresa,
        PropietarioNombre: "Refrielectric S.A.",
        TipoDocumentoNombre: "Certificado TGSS",
        TipoDocumentoAplicaVencimientoAutomatico: false,
        FechaEmision: new DateOnly(2026, 1, 1),
        FechaVencimiento: null,
        EstadoVigencia: EstadoVigenciaDocumento.SinConfirmar,
        ArchivoUrl: null,
        Comentarios: null,
        TipoDocumentoDescripcion: null,
        TipoDocumentoCriteriosValidacion: null,
        TipoDocumentoSeSolicitaA: null,
        TipoDocumentoObservaciones: null,
        Version: Guid.NewGuid(),
        TipoDocumentoPerfilDocumentoOficial: PerfilDocumentoOficial.Ninguno,
        EmpresaId: Guid.NewGuid());

    private IRenderedComponent<DocumentoWorkspacePanel> Renderizar(DocumentoDetalleDto detalle, string pestanaActiva)
    {
        Services.AddScoped<ToastService>();
        Services.AddScoped<IMediator>(_ => new MediatorDocumento { Detalle = detalle });
        return Render<DocumentoWorkspacePanel>(parametros => parametros
            .Add(p => p.EntidadId, detalle.Id)
            .Add(p => p.PestanaActiva, pestanaActiva));
    }

    [Fact]
    public void El_conjunto_de_pestanas_no_ofrece_Versiones()
    {
        var cut = Renderizar(Detalle(), "informacion");

        var pestanas = cut.FindAll("[role=tab]").Select(boton => boton.TextContent.Trim()).ToList();

        pestanas.Should().Contain("Validación").And.Contain("Historial");
        pestanas.Should().NotContain("Versiones",
            "REC-097/DEC-21 la retiró: anunciar un versionado que no existe es peor que no tenerlo");
    }

    [Fact]
    public void Un_deep_link_guardado_a_versiones_cae_a_Informacion_sin_dejar_el_panel_vacio()
    {
        var detalle = Detalle();
        var cut = Renderizar(detalle, "versiones");

        cut.Find("[role=tab][aria-selected='true']").TextContent.Trim().Should().Be("Información");
        cut.Find(".pestanas-panel").TextContent.Should().Contain(detalle.PropietarioNombre,
            "un ctx=…:versiones guardado antes de REC-097 no puede dejar el panel sin contenido");
    }

    // ------------------------------------------------------------------ solo escritura
    // Renovar, firmar, gestionar desde Sugerencias y «Actualizar documento» acaban en un
    // ICommand que AutorizacionEscrituraBehavior deniega a Consulta: no se le ofrecen. El
    // caso con escritura de cada Theory es el control de que el disparador llegó a pintarse.

    [Theory]
    [InlineData(Roles.GestorCae, true)]
    [InlineData(Roles.Consulta, false)]
    public void Renovar_solo_se_ofrece_a_los_roles_con_escritura(string rol, bool seOfrece)
    {
        this.ConRolDeEscritura(rol);
        var detalle = Detalle();
        var cut = Renderizar(detalle, "informacion");

        cut.Find(".pestanas-panel").TextContent.Should().Contain(detalle.PropietarioNombre, "la información se sigue viendo");
        cut.FindAll("button").Any(b => b.TextContent.Trim() == "Renovar").Should().Be(seOfrece);
        cut.FindAll(".acciones-documento-360").Should().HaveCount(seOfrece ? 1 : 0, "sin Renovar no queda el contenedor vacío");
    }

    [Theory]
    [InlineData(Roles.GestorCae, true)]
    [InlineData(Roles.Consulta, false)]
    public void Firmar_en_campo_solo_se_ofrece_a_los_roles_con_escritura(string rol, bool seOfrece)
    {
        this.ConRolDeEscritura(rol);
        var detalle = Detalle() with { ArchivoUrl = "documentos/firmable.pdf" };
        Services.AddScoped<ToastService>();
        Services.AddScoped<IMediator>(_ => new MediatorDocumento { Detalle = detalle });

        var cut = Render<FirmaEnCampoTab>(p => p.Add(t => t.EntidadId, detalle.Id));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Firmas ya realizadas", "las firmas hechas se siguen viendo"));
        cut.Markup.Should().Contain("Lucía Prieto");
        cut.FindAll("button").Any(b => b.TextContent.Trim() == "Firmar").Should().Be(seOfrece);
        cut.FindAll("canvas").Should().HaveCount(seOfrece ? 1 : 0, "sin firmar no hay lienzo en el que dibujar");
    }

    [Theory]
    [InlineData(Roles.Consulta, true)]
    [InlineData(Roles.GestorCae, false)]
    public void Sin_firmas_Consulta_ve_que_no_hay_en_vez_de_una_pestana_en_blanco(string rol, bool veEstadoVacio)
    {
        this.ConRolDeEscritura(rol);
        var detalle = Detalle() with { ArchivoUrl = "documentos/firmable.pdf" };
        Services.AddScoped<ToastService>();
        Services.AddScoped<IMediator>(_ => new MediatorDocumento { Detalle = detalle, SinFirmas = true });

        var cut = Render<FirmaEnCampoTab>(p => p.Add(t => t.EntidadId, detalle.Id));

        // Barrera: el render cargado pinta la firma nueva (con escritura) o el estado vacío (Consulta).
        cut.WaitForAssertion(() => (cut.Markup.Contains("Nueva firma") || cut.Markup.Contains("Sin firmas")).Should().BeTrue());
        cut.Markup.Contains("Sin firmas").Should().Be(veEstadoVacio);
        cut.Markup.Should().NotContain("Firmas ya realizadas");
    }

    [Theory]
    [InlineData(Roles.GestorCae, true)]
    [InlineData(Roles.Consulta, false)]
    public void Gestionar_desde_Sugerencias_solo_se_ofrece_a_los_roles_con_escritura(string rol, bool seOfrece)
    {
        this.ConRolDeEscritura(rol);
        var detalle = Detalle();
        Services.AddScoped<ToastService>();
        Services.AddScoped<IMediator>(_ => new MediatorDocumento { Detalle = detalle });
        Services.AddScoped<IFileStorageService, AlmacenArchivosQueNadieDebeTocar>();
        Services.AddScoped<IConversorWordPdfService, ConversorQueNadieDebeTocar>();
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        var cut = Render<SugerenciasPreventivasTab>();

        cut.WaitForAssertion(() => cut.Find(".fila-documento-requerido").TextContent.Should().Contain(detalle.PropietarioNombre,
            "la ventana de vencimiento se sigue viendo"));
        cut.FindAll("button").Any(b => b.TextContent.Trim() == "Gestionar").Should().Be(seOfrece);
    }

    [Theory]
    [InlineData(Roles.GestorCae, true)]
    [InlineData(Roles.Consulta, false)]
    public void Actualizar_documento_desde_el_visor_solo_se_ofrece_a_los_roles_con_escritura(string rol, bool seOfrece)
    {
        this.ConRolDeEscritura(rol);

        var cut = Render<VisorDocumento>(p => p
            .Add(v => v.Visible, true)
            .Add(v => v.DocumentoId, Guid.NewGuid()));

        cut.Markup.Should().Contain("Abrir en pestaña nueva", "ver el archivo sigue siendo de lectura");
        cut.FindAll("button").Any(b => b.TextContent.Trim() == "Actualizar documento").Should().Be(seOfrece);
    }

    /// <summary>Sugerencias monta DrawerGestionDocumento, que los inyecta; estos tests no lo abren.</summary>
    private sealed class AlmacenArchivosQueNadieDebeTocar : IFileStorageService
    {
        private static Exception NoDeberia() => new NotSupportedException("Este test no abre archivos.");

        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) => throw NoDeberia();
        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) => throw NoDeberia();
        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) => throw NoDeberia();
    }

    private sealed class ConversorQueNadieDebeTocar : IConversorWordPdfService
    {
        public Task<byte[]> ConvertirAPdfAsync(byte[] contenidoDocx, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Este test no convierte nada.");
    }
}
