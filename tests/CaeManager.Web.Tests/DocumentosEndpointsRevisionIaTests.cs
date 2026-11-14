using System.Text;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentoPorId;
using CaeManager.Application.Documentos.Queries.ObtenerRevisionesIaPendientes;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Features.Documentos;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaeManager.Web.Tests;

/// <summary>
/// <c>GET /documentos/revision-ia/{revisionId}/archivo</c> (D-49, opción C): el visor de la pantalla Revisión IA se carga
/// solo y su acceso queda como «Revisión IA», como mucho una fila por persona y Documento desde que se creó la revisión
/// pendiente, y cada acceso cuenta para la señal de extracción masiva. Aquí se fija el manejador: sirve el
/// Documento de la revisión —no uno que elija el cliente—, lo registra por la vía deduplicada y nunca como Apertura, y una
/// revisión que la cola no alcanza da 404 sin abrir ni registrar nada. <c>/documentos/{id}/archivo</c> sigue registrando
/// una Apertura en cada GET.
///
/// <para>
/// Qué no observa: la deduplicación misma (la prueba el servicio en Application y, contra la base, la integración), ni el
/// routing y la autorización por rol de la ruta (<c>EndpointsQueSirvenBinariosEstanCatalogadosTests</c> fija el mapeo).
/// </para>
/// </summary>
public class DocumentosEndpointsRevisionIaTests
{
    private static readonly Guid RevisionId = Guid.Parse("0199c0de-0000-7000-8000-0000000000a1");
    private static readonly Guid DocumentoDeLaRevision = Guid.Parse("0199c0de-0000-7000-8000-0000000000d1");
    private static readonly DateTime CreadaEnUtc = new(2026, 11, 12, 9, 30, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Sirve_en_linea_el_documento_de_la_revision_y_lo_registra_como_Revision_IA_desde_su_creacion()
    {
        var mediador = new MediadorFalso(new RevisionIaParaVisorDto(DocumentoDeLaRevision, CreadaEnUtc), Documento(DocumentoDeLaRevision));
        var registro = new RegistroEspia();

        var contexto = await ServirAsync(mediador, registro);

        contexto.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        contexto.Response.ContentType.Should().Be("application/pdf");
        contexto.Response.Headers.ContentDisposition.ToString().Should().StartWith("inline");
        Encoding.UTF8.GetString(((MemoryStream)contexto.Response.Body).ToArray()).Should().Be(AlmacenConPdf.Contenido);
        mediador.Enviadas.OfType<ObtenerRevisionIaParaVisorQuery>().Should().ContainSingle().Which.RevisionId.Should().Be(RevisionId);
        mediador.Enviadas.OfType<ObtenerDocumentoPorIdQuery>().Should().ContainSingle()
            .Which.Id.Should().Be(DocumentoDeLaRevision, "se sirve el Documento de la revisión, que el cliente no elige");
        registro.RevisionIa.Should().Equal([(DocumentoDeLaRevision, CreadaEnUtc)]);
        registro.Otros.Should().BeEmpty("el visor de la revisión nunca deja una Apertura");
    }

    [Fact]
    public async Task Una_revision_que_la_cola_no_alcanza_da_404_sin_abrir_el_documento_ni_registrar()
    {
        var mediador = new MediadorFalso(revision: null, Documento(DocumentoDeLaRevision));
        var registro = new RegistroEspia();

        var contexto = await ServirAsync(mediador, registro);

        contexto.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        ((MemoryStream)contexto.Response.Body).Length.Should().Be(0);
        contexto.Response.Headers.ContentDisposition.ToString().Should().BeEmpty();
        mediador.Enviadas.OfType<ObtenerDocumentoPorIdQuery>().Should().BeEmpty("sin revisión al alcance no se lee ningún Documento");
        registro.RevisionIa.Should().BeEmpty();
        registro.Otros.Should().BeEmpty();
        DebeSerIncrustableSinPaginaDeEstado(contexto);
    }

    [Fact]
    public async Task Si_el_Documento_de_la_revision_no_es_legible_da_404_sin_registrar()
    {
        var mediador = new MediadorFalso(new RevisionIaParaVisorDto(DocumentoDeLaRevision, CreadaEnUtc), documento: null);
        var registro = new RegistroEspia();

        var contexto = await ServirAsync(mediador, registro);

        contexto.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        registro.RevisionIa.Should().BeEmpty();
        registro.Otros.Should().BeEmpty();
        DebeSerIncrustableSinPaginaDeEstado(contexto);
    }

    [Fact]
    public async Task Si_el_registro_falla_responde_403_sin_entregar_ningun_byte()
    {
        var mediador = new MediadorFalso(new RevisionIaParaVisorDto(DocumentoDeLaRevision, CreadaEnUtc), Documento(DocumentoDeLaRevision));
        var registro = new RegistroEspia { Falla = true };

        var contexto = await ServirAsync(mediador, registro);

        contexto.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden, "falla en cerrado, como /documentos/{id}/archivo");
        ((MemoryStream)contexto.Response.Body).Length.Should().Be(0);
        contexto.Response.Headers.ContentDisposition.ToString().Should().BeEmpty();
        DebeSerIncrustableSinPaginaDeEstado(contexto);
    }

    /// <summary>
    /// Control: la ruta de siempre no cambia; su 404 no se vuelve incrustable ni apaga la página de estado (solo el visor
    /// de Revisión IA lo necesita, porque se carga solo en un iframe).
    /// </summary>
    [Fact]
    public async Task El_404_del_archivo_por_Id_de_Documento_no_se_vuelve_incrustable()
    {
        var contexto = NuevoContexto();
        var resultado = await DocumentosEndpoints.ServirArchivoAsync(
            DocumentoDeLaRevision, contexto, new MediadorFalso(null, documento: null), new AlmacenConPdf(),
            new RegistroEspia(), NullLoggerFactory.Instance, CancellationToken.None);
        await resultado.ExecuteAsync(contexto);

        contexto.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        contexto.Response.Headers["X-Frame-Options"].ToString().Should().BeEmpty();
        contexto.Features.Get<IStatusCodePagesFeature>()!.Enabled.Should().BeTrue();
    }

    /// <summary>Control: la ruta de siempre sigue registrando una Apertura en cada GET, sin deduplicar.</summary>
    [Fact]
    public async Task El_archivo_por_Id_de_Documento_sigue_registrando_una_Apertura_en_cada_peticion()
    {
        var registro = new RegistroEspia();
        for (var i = 0; i < 2; i++)
        {
            var contexto = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
            contexto.Response.Body = new MemoryStream();
            var resultado = await DocumentosEndpoints.ServirArchivoAsync(
                DocumentoDeLaRevision, contexto, new MediadorFalso(null, Documento(DocumentoDeLaRevision)), new AlmacenConPdf(),
                registro, NullLoggerFactory.Instance, CancellationToken.None);
            await resultado.ExecuteAsync(contexto);
            contexto.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        }

        registro.Otros.Should().Equal([
            (DocumentoDeLaRevision, TipoAccesoDocumentoSensible.Apertura),
            (DocumentoDeLaRevision, TipoAccesoDocumentoSensible.Apertura)]);
        registro.RevisionIa.Should().BeEmpty();
    }

    // ── Andamiaje ─────────────────────────────────────────────────────────

    /// <summary>
    /// Un rechazo del visor (revisión resuelta en otra pestaña, fuera del alcance, registro rechazado) lleva las mismas
    /// cabeceras de incrustación en el mismo origen que la respuesta buena, sin cuerpo, y apaga la página de estado, que
    /// metería en el iframe el HTML de /not-found: el visor queda en blanco en vez de enseñar el error del navegador.
    /// </summary>
    private static void DebeSerIncrustableSinPaginaDeEstado(DefaultHttpContext contexto)
    {
        contexto.Response.Headers["X-Frame-Options"].ToString().Should().Be("SAMEORIGIN");
        contexto.Response.Headers.ContentSecurityPolicy.ToString().Should().Contain("frame-ancestors 'self'");
        ((MemoryStream)contexto.Response.Body).Length.Should().Be(0, "sin cuerpo: ningún dato del Documento ni de la revisión");
        contexto.Features.Get<IStatusCodePagesFeature>()!.Enabled.Should().BeFalse();
    }

    private static DefaultHttpContext NuevoContexto()
    {
        var contexto = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        contexto.Response.Body = new MemoryStream();
        contexto.Features.Set<IStatusCodePagesFeature>(new StatusCodePagesFeature());
        return contexto;
    }

    private static async Task<DefaultHttpContext> ServirAsync(MediadorFalso mediador, RegistroEspia registro)
    {
        var contexto = NuevoContexto();
        var resultado = await DocumentosEndpoints.ServirArchivoDeRevisionIaAsync(
            RevisionId, contexto, mediador, new AlmacenConPdf(), registro, NullLoggerFactory.Instance, CancellationToken.None);
        await resultado.ExecuteAsync(contexto);
        return contexto;
    }

    private static DocumentoDetalleDto Documento(Guid id) => new(
        Id: id,
        Ambito: AmbitoAplicacion.Trabajador,
        PropietarioNombre: "Ana Ruiz",
        TipoDocumentoNombre: "Reconocimiento médico",
        TipoDocumentoAplicaVencimientoAutomatico: true,
        FechaEmision: new DateOnly(2026, 1, 10),
        FechaVencimiento: new DateOnly(2027, 1, 10),
        EstadoVigencia: EstadoVigenciaDocumento.VenceEnFecha,
        ArchivoUrl: "3f3e0000000000000000000000000001/a1b2c3d4.pdf",
        Comentarios: null,
        TipoDocumentoDescripcion: null,
        TipoDocumentoCriteriosValidacion: null,
        TipoDocumentoSeSolicitaA: null,
        TipoDocumentoObservaciones: null,
        Version: Guid.NewGuid(),
        TipoDocumentoPerfilDocumentoOficial: PerfilDocumentoOficial.Ninguno,
        EmpresaId: null,
        NombreArchivoDescarga: "reconocimiento.pdf");

    private sealed class AlmacenConPdf : IFileStorageService
    {
        internal const string Contenido = "%PDF-revision-ia";

        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(Contenido)));

        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RegistroEspia : IRegistroAccesoDocumentoSensibleService
    {
        public List<(Guid DocumentoId, DateTime Desde)> RevisionIa { get; } = [];
        public List<(Guid DocumentoId, TipoAccesoDocumentoSensible Tipo)> Otros { get; } = [];
        public bool Falla { get; init; }

        public Task RegistrarSiSensibleAsync(Guid documentoId, TipoAccesoDocumentoSensible tipoAcceso, CancellationToken cancellationToken = default)
        {
            Otros.Add((documentoId, tipoAcceso));
            return Task.CompletedTask;
        }

        public Task RegistrarSiSensibleAsync(
            Guid recursoId, Guid tipoDocumentoId, TipoAccesoDocumentoSensible tipoAcceso, CancellationToken cancellationToken = default)
        {
            Otros.Add((recursoId, tipoAcceso));
            return Task.CompletedTask;
        }

        public Task RegistrarRevisionIaSiSensibleAsync(Guid documentoId, DateTime revisionCreadaEnUtc, CancellationToken cancellationToken = default)
        {
            if (Falla)
                throw new CaeManager.Application.Auditoria.AccesoDocumentoSensibleNoRegistradoException(documentoId);
            RevisionIa.Add((documentoId, revisionCreadaEnUtc));
            return Task.CompletedTask;
        }
    }

    /// <summary>Contesta la consulta de la revisión y la del Documento; anota todo lo que se le pide.</summary>
    private sealed class MediadorFalso(RevisionIaParaVisorDto? revision, DocumentoDetalleDto? documento) : IMediator
    {
        public List<object> Enviadas { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);
            object? respuesta = request switch
            {
                ObtenerRevisionIaParaVisorQuery => revision,
                ObtenerDocumentoPorIdQuery => documento,
                _ => throw new NotSupportedException(request.GetType().Name),
            };
            return Task.FromResult((TResponse)respuesta!);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
            where TRequest : IRequest => throw new NotSupportedException();

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }
}
