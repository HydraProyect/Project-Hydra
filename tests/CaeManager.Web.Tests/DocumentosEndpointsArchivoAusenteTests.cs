using System.Data.Common;
using System.Text;
using CaeManager.Application.Auditoria;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentoPorId;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Features.Documentos;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CaeManager.Web.Tests;

/// <summary>
/// Defecto B2 del piloto Outbound (2026-10-08). <c>GET /documentos/{id}/archivo</c> de un Documento
/// cuyo <c>ArchivoUrl</c> apunta a un fichero que ya no está en el almacén (siembra sin PDF, almacén
/// inconsistente) dejaba escapar el <see cref="FileNotFoundException"/> de
/// <c>IFileStorageService.AbrirAsync</c>: error 500, y en Development la página de excepción con la
/// clave del fichero. Ahora responde 404, como cuando el Documento no existe o no tiene adjunto.
///
/// <para>
/// <b>Qué observa y qué no.</b> Llama al manejador (<see cref="DocumentosEndpoints.ServirArchivoAsync"/>)
/// y ejecuta su <c>IResult</c> contra un <c>HttpContext</c>; no pasa por el routing ni por la
/// autorización (<c>Policies.SesionOExtension</c>), que este cambio no toca. El almacén es un doble que
/// lanza lo mismo que <c>DiskFileStorageService.AbrirAsync</c> cuando el fichero falta: que el almacén
/// real lo lance así no lo prueba este test, lo dice su código.
/// </para>
/// </summary>
public class DocumentosEndpointsArchivoAusenteTests
{
    private const string ClaveDelFichero = "3f3e0000000000000000000000000001/a1b2c3d4.pdf";
    private static readonly Guid DocumentoId = Guid.Parse("0199c0de-0000-7000-8000-000000000001");

    [Fact]
    public async Task Un_fichero_que_falta_en_el_almacen_responde_404_sin_excepcion()
    {
        var contexto = Contexto();
        var registro = new RegistroDeAccesosEspia();
        var avisos = new FabricaDeRegistroEspia();

        var resultado = await DocumentosEndpoints.ServirArchivoAsync(
            DocumentoId, contexto, new MediatorFalso(Documento()), new AlmacenSinElFichero(), registro, avisos, CancellationToken.None);
        await resultado.ExecuteAsync(contexto);

        contexto.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        LeerCuerpo(contexto).Should().NotContain(ClaveDelFichero, "la respuesta no revela dónde vive el fichero");
        registro.Accesos.Should().BeEmpty("DEC-36: no se registra un acceso que no entregó contenido");
        avisos.Entradas.Should().ContainSingle()
            .Which.Should().Match<(LogLevel Nivel, string Mensaje)>(e => e.Nivel == LogLevel.Warning && e.Mensaje.Contains(DocumentoId.ToString()));
        avisos.Entradas.Single().Mensaje.Should().NotContain(ClaveDelFichero);
    }

    /// <summary>Control positivo: con el fichero presente el mismo manejador lo sirve y registra el acceso.</summary>
    [Fact]
    public async Task Un_fichero_presente_se_sirve_y_registra_el_acceso()
    {
        var contexto = Contexto();
        var registro = new RegistroDeAccesosEspia();
        var avisos = new FabricaDeRegistroEspia();

        var resultado = await DocumentosEndpoints.ServirArchivoAsync(
            DocumentoId, contexto, new MediatorFalso(Documento()), new AlmacenConElFichero(), registro, avisos, CancellationToken.None);
        await resultado.ExecuteAsync(contexto);

        contexto.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        LeerCuerpo(contexto).Should().Be(AlmacenConElFichero.Contenido);
        registro.Accesos.Should().Equal((DocumentoId, TipoAccesoDocumentoSensible.Apertura));
        avisos.Entradas.Should().BeEmpty();
    }

    /// <summary>
    /// Falla en cerrado (DEC-36, REC-208; revisión extensa 2026-10-23, Z2-2): si el acceso no
    /// queda registrado, el manejador no devuelve el fichero y cierra el flujo que ya abrió.
    /// </summary>
    [Theory]
    [InlineData(0, StatusCodes.Status403Forbidden)]
    [InlineData(2, StatusCodes.Status403Forbidden)]
    [InlineData(3, StatusCodes.Status403Forbidden)]
    [InlineData(1, StatusCodes.Status503ServiceUnavailable)]
    [InlineData(4, StatusCodes.Status503ServiceUnavailable)]
    [InlineData(5, StatusCodes.Status503ServiceUnavailable)]
    [InlineData(6, StatusCodes.Status503ServiceUnavailable)]
    [InlineData(7, StatusCodes.Status503ServiceUnavailable)]
    [InlineData(8, StatusCodes.Status503ServiceUnavailable)]
    [InlineData(9, StatusCodes.Status503ServiceUnavailable)]
    public async Task Si_el_registro_falla_responde_sin_contenido_y_cierra_el_flujo(int fallo, int codigo)
    {
        var contexto = Contexto();
        var almacen = new AlmacenConElFichero();

        var resultado = await DocumentosEndpoints.ServirArchivoAsync(
            DocumentoId, contexto, new MediatorFalso(Documento()), almacen, new RegistroQueNoRegistra(fallo),
            new FabricaDeRegistroEspia(), CancellationToken.None);
        await resultado.ExecuteAsync(contexto);

        contexto.Response.StatusCode.Should().Be(codigo);
        almacen.UltimoFlujo.Should().NotBeNull("control positivo: el fichero llegó a abrirse");
        almacen.UltimoFlujo!.CanRead.Should().BeFalse("el flujo se cierra antes de responder");
        LeerCuerpo(contexto).Should().BeEmpty("no sale ni un byte del documento");
        LeerCuerpo(contexto).Should().NotContain(AlmacenConElFichero.Contenido);
        contexto.Response.Headers.ContentDisposition.Should().BeEmpty();
    }

    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    public async Task Un_error_no_transitorio_o_cancelacion_se_propaga_sin_servir_el_fichero(int fallo)
    {
        var contexto = Contexto();
        var almacen = new AlmacenConElFichero();
        var servir = () => DocumentosEndpoints.ServirArchivoAsync(
            DocumentoId, contexto, new MediatorFalso(Documento()), almacen, new RegistroQueNoRegistra(fallo),
            new FabricaDeRegistroEspia(), CancellationToken.None);

        if (fallo == 11)
            await servir.Should().ThrowAsync<OperationCanceledException>();
        else
            await servir.Should().ThrowAsync<InvalidOperationException>();
        almacen.UltimoFlujo.Should().NotBeNull();
        almacen.UltimoFlujo!.CanRead.Should().BeFalse();
        LeerCuerpo(contexto).Should().BeEmpty();
    }

    [Fact]
    public async Task Un_Documento_sin_adjunto_sigue_respondiendo_404_sin_tocar_el_almacen()
    {
        var contexto = Contexto();
        var avisos = new FabricaDeRegistroEspia();

        var resultado = await DocumentosEndpoints.ServirArchivoAsync(
            DocumentoId, contexto, new MediatorFalso(Documento() with { ArchivoUrl = null }), new AlmacenQueNoSeToca(),
            new RegistroDeAccesosEspia(), avisos, CancellationToken.None);
        await resultado.ExecuteAsync(contexto);

        contexto.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        avisos.Entradas.Should().BeEmpty("que un Documento no tenga adjunto no es una anomalía del almacén");
    }

    // ── Andamiaje ─────────────────────────────────────────────────────────

    private static DocumentoDetalleDto Documento() => new(
        Id: DocumentoId,
        Ambito: AmbitoAplicacion.Trabajador,
        PropietarioNombre: "Ana Ruiz",
        TipoDocumentoNombre: "Formación PRL",
        TipoDocumentoAplicaVencimientoAutomatico: true,
        FechaEmision: new DateOnly(2026, 1, 10),
        FechaVencimiento: new DateOnly(2027, 1, 10),
        EstadoVigencia: EstadoVigenciaDocumento.VenceEnFecha,
        ArchivoUrl: ClaveDelFichero,
        Comentarios: null,
        TipoDocumentoDescripcion: null,
        TipoDocumentoCriteriosValidacion: null,
        TipoDocumentoSeSolicitaA: null,
        TipoDocumentoObservaciones: null,
        Version: Guid.NewGuid(),
        TipoDocumentoPerfilDocumentoOficial: PerfilDocumentoOficial.Ninguno,
        EmpresaId: null,
        NombreArchivoDescarga: "formacion-prl.pdf");

    /// <summary><c>RequestServices</c> con registro y un <c>Body</c> escribible: lo que un <c>IResult</c> necesita para ejecutarse de verdad.</summary>
    private static DefaultHttpContext Contexto()
    {
        var servicios = new ServiceCollection();
        servicios.AddLogging();

        var contexto = new DefaultHttpContext { RequestServices = servicios.BuildServiceProvider() };
        contexto.Response.Body = new MemoryStream();
        return contexto;
    }

    private static string LeerCuerpo(HttpContext contexto) =>
        Encoding.UTF8.GetString(((MemoryStream)contexto.Response.Body).ToArray());

    /// <summary>Lo que hace <c>DiskFileStorageService.AbrirAsync</c> cuando el fichero no está: la excepción lleva la clave en <c>FileName</c>.</summary>
    private sealed class AlmacenSinElFichero : IFileStorageService
    {
        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) =>
            throw new FileNotFoundException("No encontramos el archivo solicitado.", identificador);

        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class AlmacenConElFichero : IFileStorageService
    {
        internal const string Contenido = "%PDF-de-prueba";

        public MemoryStream? UltimoFlujo { get; private set; }

        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default)
        {
            UltimoFlujo = new MemoryStream(Encoding.UTF8.GetBytes(Contenido));
            return Task.FromResult<Stream>(UltimoFlujo);
        }

        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class AlmacenQueNoSeToca : IFileStorageService
    {
        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Sin adjunto no hay nada que abrir.");

        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RegistroDeAccesosEspia : IRegistroAccesoDocumentoSensibleService
    {
        public List<(Guid DocumentoId, TipoAccesoDocumentoSensible Tipo)> Accesos { get; } = [];

        public Task RegistrarSiSensibleAsync(Guid documentoId, TipoAccesoDocumentoSensible tipoAcceso, CancellationToken cancellationToken = default)
        {
            Accesos.Add((documentoId, tipoAcceso));
            return Task.CompletedTask;
        }

        public Task RegistrarSiSensibleAsync(
            Guid recursoId, Guid tipoDocumentoId, TipoAccesoDocumentoSensible tipoAcceso, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    /// <summary>Lo que hace el servicio real cuando la base rechaza el registro.</summary>
    private sealed class RegistroQueNoRegistra(int fallo) : IRegistroAccesoDocumentoSensibleService
    {
        public Task RegistrarSiSensibleAsync(Guid documentoId, TipoAccesoDocumentoSensible tipoAcceso, CancellationToken cancellationToken = default) =>
            throw fallo switch
            {
                0 => new AccesoDocumentoSensibleNoRegistradoException(documentoId),
                1 => new TimeoutException(),
                2 => new FalloBaseDatos("42501", false),
                3 => new DbUpdateException("rechazo", new FalloBaseDatos("42501", false)),
                4 => new FalloBaseDatos("08006", true),
                5 => new DbUpdateException("transitorio", new FalloBaseDatos("08006", true)),
                6 => new InvalidOperationException("transitorio", new FalloBaseDatos("08006", true)),
                7 => new InvalidOperationException("transitorio", new DbUpdateException("escritura", new FalloBaseDatos("08006", true))),
                8 => new InvalidOperationException("transitorio", new TimeoutException()),
                9 => new RetryLimitExceededException("reintentos agotados", new DbUpdateException("escritura", new FalloBaseDatos("08006", true))),
                11 => new OperationCanceledException(),
                _ => new InvalidOperationException()
            };

        public Task RegistrarSiSensibleAsync(
            Guid recursoId, Guid tipoDocumentoId, TipoAccesoDocumentoSensible tipoAcceso, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FalloBaseDatos(string sqlState, bool transitorio) : DbException
    {
        public override string SqlState => sqlState;
        public override bool IsTransient => transitorio;
    }

    private sealed class FabricaDeRegistroEspia : ILoggerFactory, ILogger
    {
        public List<(LogLevel Nivel, string Mensaje)> Entradas { get; } = [];

        public ILogger CreateLogger(string categoryName) => this;

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entradas.Add((logLevel, formatter(state, exception) + (exception is null ? string.Empty : " " + exception)));
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
