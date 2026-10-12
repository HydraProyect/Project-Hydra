using System.Text;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentoPorId;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Features.Documentos;
using CaeManager.Web.Services;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaeManager.Web.Tests;

/// <summary>
/// El visor de PDF (<c>VisorDocumento</c>, un <c>&lt;iframe src="/documentos/{id}/archivo"&gt;</c> en un
/// modal) y los enlaces «Ver PDF» necesitan que <c>GET /documentos/{id}/archivo</c> <b>se muestre</b> en el
/// navegador. Antes lo impedían dos cosas independientes: <c>Content-Disposition: attachment</c> (el
/// nombre pasado a <c>Results.File</c> como <c>fileDownloadName</c>) y las cabeceras anti-iframe que
/// <c>UseCabecerasSeguridad</c> pone en TODA respuesta (<c>X-Frame-Options: DENY</c>,
/// <c>frame-ancestors 'none'</c>). Hacía falta quitar las dos; quitar solo una dejaba el visor en blanco.
///
/// <para>
/// <b>Qué observa este test.</b> Ejecuta el <c>IResult</c> del manejador real contra un
/// <c>HttpContext</c> y mira las cabeceras que quedan <b>después</b> de que el ejecutor escriba las suyas.
/// Las comprobaciones de incrustación pasan por el middleware <c>UseCabecerasSeguridad</c> real: un test
/// que sembrara a mano el <c>DENY</c> pasaría aunque el middleware cambiara de sitio o de valor.
/// </para>
///
/// <para>
/// <b>Qué no observa.</b> Si un navegador real pinta el PDF: eso depende de su visor integrado, no del
/// servidor. Tampoco recorre el routing ni <c>Policies.SesionOExtension</c>, que este cambio no toca.
/// </para>
/// </summary>
public class DocumentosEndpointsVisorInlineTests
{
    private static readonly Guid DocumentoId = Guid.Parse("0199c0de-0000-7000-8000-000000000002");

    [Fact]
    public async Task El_PDF_se_sirve_inline_con_su_nombre_y_nunca_como_attachment()
    {
        var contexto = await ServirAsync(Documento("formacion-prl.pdf"));

        var disposicion = contexto.Response.Headers.ContentDisposition.ToString();
        disposicion.Should().StartWith("inline");
        disposicion.Should().Contain("formacion-prl.pdf", "Guardar como… y la pestaña del navegador usan este nombre");
        disposicion.Should().NotContainEquivalentOf("attachment", "attachment es lo que forzaba la descarga");
    }

    [Fact]
    public async Task Un_nombre_con_caracteres_no_ASCII_viaja_codificado_y_sigue_siendo_inline()
    {
        var contexto = await ServirAsync(Documento("Informe médico — Ñandú.pdf"));

        var disposicion = contexto.Response.Headers.ContentDisposition.ToString();
        disposicion.Should().StartWith("inline");
        disposicion.Should().Contain("filename*=UTF-8''", "RFC 5987: una cabecera HTTP no admite tildes sin codificar");
        disposicion.Should().NotContainEquivalentOf("attachment");
        disposicion.Should().NotContain("é", "ningún carácter no ASCII va crudo en la cabecera");
    }

    [Fact]
    public async Task Sin_nombre_de_descarga_sigue_siendo_inline()
    {
        var contexto = await ServirAsync(Documento(string.Empty));

        contexto.Response.Headers.ContentDisposition.ToString().Should().Be("inline");
    }

    /// <summary>Control positivo: lo que se midió es la respuesta que de verdad entrega el PDF.</summary>
    [Fact]
    public async Task Lo_medido_es_la_respuesta_que_entrega_el_PDF()
    {
        var contexto = await ServirAsync(Documento("a.pdf"));

        contexto.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        contexto.Response.ContentType.Should().Be("application/pdf");
        Encoding.UTF8.GetString(((MemoryStream)contexto.Response.Body).ToArray()).Should().Be(AlmacenConPdf.Contenido);
    }

    [Fact]
    public async Task El_PDF_solo_se_puede_incrustar_en_el_mismo_origen_a_traves_del_middleware_real()
    {
        var contexto = await ServirAsync(Documento("a.pdf"));

        contexto.Response.Headers["X-Frame-Options"].ToString().Should().Be("SAMEORIGIN");
        var politica = contexto.Response.Headers["Content-Security-Policy"].ToString();
        politica.Should().EndWith("; frame-ancestors 'self'", "'self' exacto y último: ni comodín, ni otros orígenes, ni esquemas añadidos");
        politica.Should().NotContain("frame-ancestors 'none'");
    }

    /// <summary>
    /// Un blob que falta en el almacén (Documento con adjunto registrado, fichero inexistente) responde 404
    /// sin relajar la incrustación y sin registrar un acceso que nunca entregó contenido (DEC-36).
    /// </summary>
    [Fact]
    public async Task Un_blob_ausente_responde_404_sin_relajar_la_incrustacion_ni_registrar_acceso()
    {
        var registro = new RegistroSinEfecto();
        var contexto = await EjecutarConMiddlewareAsync(async contexto =>
        {
            var resultado = await DocumentosEndpoints.ServirArchivoAsync(
                DocumentoId, contexto, new MediatorFalso(Documento("a.pdf")), new AlmacenSinFichero(),
                registro, NullLoggerFactory.Instance, CancellationToken.None);
            await resultado.ExecuteAsync(contexto);
        });

        contexto.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        contexto.Response.Headers["X-Frame-Options"].ToString().Should().Be("DENY");
        contexto.Response.Headers["Content-Security-Policy"].ToString().Should().Contain("frame-ancestors 'none'");
        contexto.Response.Headers.ContentDisposition.ToString().Should().BeEmpty();
        registro.Llamadas.Should().Be(0);
    }

    /// <summary>
    /// Control negativo: sin él, el test de arriba pasaría aunque se hubiera relajado el middleware para
    /// TODAS las rutas. Una ruta que no llama a <c>PermitirIncrustacionEnMismoOrigen</c> conserva el valor
    /// por defecto de la aplicación.
    /// </summary>
    [Fact]
    public async Task Cualquier_otra_ruta_sigue_sin_poder_incrustarse()
    {
        var contexto = await EjecutarConMiddlewareAsync(_ => Task.CompletedTask);

        contexto.Response.Headers["X-Frame-Options"].ToString().Should().Be("DENY");
        contexto.Response.Headers["Content-Security-Policy"].ToString().Should().Contain("frame-ancestors 'none'");
    }

    [Fact]
    public async Task Un_Documento_sin_adjunto_responde_404_sin_relajar_la_incrustacion()
    {
        var contexto = await ServirAsync(Documento("a.pdf") with { ArchivoUrl = null });

        contexto.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        contexto.Response.Headers["X-Frame-Options"].ToString().Should().Be("DENY");
        contexto.Response.Headers["Content-Security-Policy"].ToString().Should().Contain("frame-ancestors 'none'");
    }

    /// <summary>
    /// Solo cambia <c>frame-ancestors</c>: el resto de la política (script-src, object-src, default-src…)
    /// es exactamente la de toda la aplicación.
    /// </summary>
    [Fact]
    public async Task La_politica_del_PDF_solo_difiere_en_frame_ancestors()
    {
        var pdf = (await ServirAsync(Documento("a.pdf"))).Response.Headers["Content-Security-Policy"].ToString();
        var porDefecto = (await EjecutarConMiddlewareAsync(_ => Task.CompletedTask)).Response.Headers["Content-Security-Policy"].ToString();

        pdf.Replace("frame-ancestors 'self'", "frame-ancestors 'none'").Should().Be(porDefecto);
    }

    // ── Andamiaje ─────────────────────────────────────────────────────────

    private static Task<DefaultHttpContext> ServirAsync(DocumentoDetalleDto documento) =>
        EjecutarConMiddlewareAsync(async contexto =>
        {
            var resultado = await DocumentosEndpoints.ServirArchivoAsync(
                DocumentoId, contexto, new MediatorFalso(documento), new AlmacenConPdf(),
                new RegistroSinEfecto(), NullLoggerFactory.Instance, CancellationToken.None);
            await resultado.ExecuteAsync(contexto);
        });

    /// <summary>
    /// El pipeline real: <c>UseCabecerasSeguridad</c> y, como terminal, el manejador de la ruta. Es el
    /// mismo orden que en Program.cs (el middleware escribe antes y el endpoint se ejecuta después).
    /// </summary>
    private static async Task<DefaultHttpContext> EjecutarConMiddlewareAsync(Func<HttpContext, Task> terminal)
    {
        var servicios = new ServiceCollection().AddLogging().BuildServiceProvider();
        var app = new ApplicationBuilder(servicios);
        app.UseCabecerasSeguridad();
        app.Run(contexto => terminal(contexto));
        var pipeline = app.Build();

        var contexto = new DefaultHttpContext { RequestServices = servicios };
        contexto.Response.Body = new MemoryStream();
        await pipeline(contexto);
        return contexto;
    }

    private static DocumentoDetalleDto Documento(string nombreArchivoDescarga) => new(
        Id: DocumentoId,
        Ambito: AmbitoAplicacion.Trabajador,
        PropietarioNombre: "Ana Ruiz",
        TipoDocumentoNombre: "Formación PRL",
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
        NombreArchivoDescarga: nombreArchivoDescarga);

    private sealed class AlmacenConPdf : IFileStorageService
    {
        internal const string Contenido = "%PDF-de-prueba";

        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(Contenido)));

        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class AlmacenSinFichero : IFileStorageService
    {
        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) =>
            throw new FileNotFoundException("No encontramos el archivo solicitado.", identificador);

        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RegistroSinEfecto : IRegistroAccesoDocumentoSensibleService
    {
        public int Llamadas { get; private set; }

        public Task RegistrarSiSensibleAsync(Guid documentoId, TipoAccesoDocumentoSensible tipoAcceso, CancellationToken cancellationToken = default)
        {
            Llamadas++;
            return Task.CompletedTask;
        }

        public Task RegistrarSiSensibleAsync(
            Guid recursoId, Guid tipoDocumentoId, TipoAccesoDocumentoSensible tipoAcceso, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class MediatorFalso(object? respuesta) : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)respuesta!);

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
            where TRequest : IRequest => Task.CompletedTask;

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => Task.FromResult(respuesta);

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }
}
