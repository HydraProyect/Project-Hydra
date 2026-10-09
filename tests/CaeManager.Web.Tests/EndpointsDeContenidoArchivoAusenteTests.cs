using System.Reflection;
using System.Text;
using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Queries.ObtenerAdjuntoParaDescarga;
using CaeManager.Application.Documentos.Queries.ObtenerFirmaGuardadaUsuario;
using CaeManager.Application.Documentos.Queries.ObtenerSelloEmpresa;
using CaeManager.Application.Subcontratas.Queries.ObtenerEvidenciaVerificacionParaDescarga;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Features.Centros;
using CaeManager.Web.Features.Comunicaciones;
using CaeManager.Web.Features.Documentos;
using CaeManager.Web.Features.Subcontratas;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CaeManager.Web.Tests;

/// <summary>
/// Resto del defecto B2 del piloto Outbound (lote 2). <c>DocumentosEndpoints</c> dejó de responder
/// 500 cuando el fichero de un Documento no está en el almacén
/// (<see cref="DocumentosEndpointsArchivoAusenteTests"/>); los otros cinco puntos que sirven bytes de
/// <c>IFileStorageService.AbrirAsync</c> seguían dejando escapar el <see cref="FileNotFoundException"/>:
/// la plantilla de un requisito documental de Centro, el adjunto de un mensaje, la firma guardada, el
/// sello de una Empresa y la evidencia de una verificación externa. Ahora responden 404 y dejan un
/// aviso sin la clave del fichero.
///
/// <para>
/// <b>Qué observa y qué no.</b> Llama a cada manejador y ejecuta su <c>IResult</c> contra un
/// <c>HttpContext</c>; no pasa por el routing ni por la política de autorización del endpoint, que
/// este cambio no toca. El almacén es un doble que lanza lo mismo que
/// <c>DiskFileStorageService.AbrirAsync</c> cuando el fichero falta: que el almacén real lo lance así
/// no lo prueba este test, lo dice su código. Lo que cada consulta decide sobre el alcance tampoco se
/// prueba aquí (el mediador es un doble); sí que el manejador sigue respondiendo 404 sin abrir nada
/// cuando la consulta no devuelve el recurso, y, en el único que resuelve el alcance él mismo (la
/// plantilla del requisito), que un Centro fuera de alcance sigue sin abrir el almacén.
/// </para>
/// </summary>
public class EndpointsDeContenidoArchivoAusenteTests
{
    private const string ClaveDelFichero = "3f3e0000000000000000000000000001/a1b2c3d4.bin";
    private static readonly Guid Id = Guid.Parse("0199c0de-0000-7000-8000-000000000002");
    private static readonly Guid CentroId = Guid.Parse("0199c0de-0000-7000-8000-0000000000c1");
    private static readonly Guid TipoDocumentoId = Guid.Parse("0199c0de-0000-7000-8000-0000000000d1");

    // ── Plantilla de un requisito documental de Centro ────────────────────

    [Fact]
    public async Task Plantilla_de_requisito_cuyo_fichero_falta_responde_404_y_avisa_sin_la_clave()
    {
        var (contexto, avisos) = (Contexto(), new FabricaDeRegistroEspia());

        var resultado = await RequisitosDocumentalesEndpoints.ServirPlantillaAsync(
            Id, new RepositorioConUnaFila(Plantilla(ClaveDelFichero)), Alcance(centrosVisibles: null),
            new AlmacenSinElFichero(), avisos, CancellationToken.None);
        await resultado.ExecuteAsync(contexto);

        AfirmarAusente(contexto, avisos, idEnElAviso: Id);
    }

    [Fact]
    public async Task Plantilla_de_requisito_presente_se_sirve()
    {
        var (contexto, avisos) = (Contexto(), new FabricaDeRegistroEspia());

        var resultado = await RequisitosDocumentalesEndpoints.ServirPlantillaAsync(
            Id, new RepositorioConUnaFila(Plantilla(ClaveDelFichero)), Alcance(centrosVisibles: [CentroId]),
            new AlmacenConElFichero(), avisos, CancellationToken.None);
        await resultado.ExecuteAsync(contexto);

        AfirmarServido(contexto, avisos);
    }

    [Fact]
    public async Task Requisito_sin_plantilla_sigue_respondiendo_404_sin_tocar_el_almacen()
    {
        var (contexto, avisos) = (Contexto(), new FabricaDeRegistroEspia());

        var resultado = await RequisitosDocumentalesEndpoints.ServirPlantillaAsync(
            Id, new RepositorioConUnaFila(Plantilla(archivoUrl: null)), Alcance(centrosVisibles: null),
            new AlmacenQueNoSeToca(), avisos, CancellationToken.None);
        await resultado.ExecuteAsync(contexto);

        AfirmarNoEncontradoSinAviso(contexto, avisos);
    }

    /// <summary>La comprobación de alcance no se ha movido: sigue antes de abrir el almacén.</summary>
    [Fact]
    public async Task Plantilla_de_un_Centro_fuera_de_alcance_sigue_respondiendo_404_sin_tocar_el_almacen()
    {
        var (contexto, avisos) = (Contexto(), new FabricaDeRegistroEspia());

        var resultado = await RequisitosDocumentalesEndpoints.ServirPlantillaAsync(
            Id, new RepositorioConUnaFila(Plantilla(ClaveDelFichero)), Alcance(centrosVisibles: [Guid.NewGuid()]),
            new AlmacenQueNoSeToca(), avisos, CancellationToken.None);
        await resultado.ExecuteAsync(contexto);

        AfirmarNoEncontradoSinAviso(contexto, avisos);
    }

    // ── Adjunto de un mensaje ─────────────────────────────────────────────

    [Fact]
    public async Task Adjunto_de_mensaje_cuyo_fichero_falta_responde_404_y_avisa_sin_la_clave()
    {
        var (contexto, avisos) = (Contexto(), new FabricaDeRegistroEspia());

        var resultado = await ComunicacionesEndpoints.ServirAdjuntoAsync(
            Id, new MediatorFalso(Adjunto()), new AlmacenSinElFichero(), avisos, CancellationToken.None);
        await resultado.ExecuteAsync(contexto);

        AfirmarAusente(contexto, avisos, idEnElAviso: Id);
    }

    [Fact]
    public async Task Adjunto_de_mensaje_presente_se_sirve()
    {
        var (contexto, avisos) = (Contexto(), new FabricaDeRegistroEspia());

        var resultado = await ComunicacionesEndpoints.ServirAdjuntoAsync(
            Id, new MediatorFalso(Adjunto()), new AlmacenConElFichero(), avisos, CancellationToken.None);
        await resultado.ExecuteAsync(contexto);

        AfirmarServido(contexto, avisos);
    }

    [Fact]
    public async Task Adjunto_que_la_consulta_no_devuelve_sigue_respondiendo_404_sin_tocar_el_almacen()
    {
        var (contexto, avisos) = (Contexto(), new FabricaDeRegistroEspia());

        var resultado = await ComunicacionesEndpoints.ServirAdjuntoAsync(
            Id, new MediatorFalso(null), new AlmacenQueNoSeToca(), avisos, CancellationToken.None);
        await resultado.ExecuteAsync(contexto);

        AfirmarNoEncontradoSinAviso(contexto, avisos);
    }

    // ── Firma guardada de la cuenta en sesión ─────────────────────────────

    [Fact]
    public async Task Firma_guardada_cuya_imagen_falta_responde_404_y_avisa_sin_la_clave()
    {
        var (contexto, avisos) = (Contexto(), new FabricaDeRegistroEspia());

        var resultado = await FirmasGuardadasEndpoints.ServirFirmaAsync(
            contexto, new MediatorFalso(new FirmaGuardadaUsuarioDto(ClaveDelFichero, DateTime.UtcNow)),
            new AlmacenSinElFichero(), avisos, CancellationToken.None);
        await resultado.ExecuteAsync(contexto);

        // La firma no tiene Id que citar (ver el comentario del manejador): el aviso solo la nombra.
        AfirmarAusente(contexto, avisos, idEnElAviso: null);
        avisos.Entradas.Single().Mensaje.Should().Contain("firma guardada");
    }

    [Fact]
    public async Task Firma_guardada_presente_se_sirve()
    {
        var (contexto, avisos) = (Contexto(), new FabricaDeRegistroEspia());

        var resultado = await FirmasGuardadasEndpoints.ServirFirmaAsync(
            contexto, new MediatorFalso(new FirmaGuardadaUsuarioDto(ClaveDelFichero, DateTime.UtcNow)),
            new AlmacenConElFichero(), avisos, CancellationToken.None);
        await resultado.ExecuteAsync(contexto);

        AfirmarServido(contexto, avisos);
    }

    [Fact]
    public async Task Cuenta_sin_firma_guardada_sigue_respondiendo_404_sin_tocar_el_almacen()
    {
        var (contexto, avisos) = (Contexto(), new FabricaDeRegistroEspia());

        var resultado = await FirmasGuardadasEndpoints.ServirFirmaAsync(
            contexto, new MediatorFalso(null), new AlmacenQueNoSeToca(), avisos, CancellationToken.None);
        await resultado.ExecuteAsync(contexto);

        AfirmarNoEncontradoSinAviso(contexto, avisos);
    }

    // ── Sello guardado de una Empresa ─────────────────────────────────────

    [Fact]
    public async Task Sello_de_Empresa_cuya_imagen_falta_responde_404_y_avisa_sin_la_clave()
    {
        var (contexto, avisos) = (Contexto(), new FabricaDeRegistroEspia());

        var resultado = await FirmasGuardadasEndpoints.ServirSelloAsync(
            Id, contexto, new MediatorFalso(new SelloEmpresaDto(ClaveDelFichero, DateTime.UtcNow)),
            new AlmacenSinElFichero(), avisos, CancellationToken.None);
        await resultado.ExecuteAsync(contexto);

        AfirmarAusente(contexto, avisos, idEnElAviso: Id);
    }

    [Fact]
    public async Task Sello_de_Empresa_presente_se_sirve()
    {
        var (contexto, avisos) = (Contexto(), new FabricaDeRegistroEspia());

        var resultado = await FirmasGuardadasEndpoints.ServirSelloAsync(
            Id, contexto, new MediatorFalso(new SelloEmpresaDto(ClaveDelFichero, DateTime.UtcNow)),
            new AlmacenConElFichero(), avisos, CancellationToken.None);
        await resultado.ExecuteAsync(contexto);

        AfirmarServido(contexto, avisos);
    }

    [Fact]
    public async Task Sello_que_la_consulta_no_devuelve_sigue_respondiendo_404_sin_tocar_el_almacen()
    {
        var (contexto, avisos) = (Contexto(), new FabricaDeRegistroEspia());

        var resultado = await FirmasGuardadasEndpoints.ServirSelloAsync(
            Id, contexto, new MediatorFalso(null), new AlmacenQueNoSeToca(), avisos, CancellationToken.None);
        await resultado.ExecuteAsync(contexto);

        AfirmarNoEncontradoSinAviso(contexto, avisos);
    }

    // ── Evidencia de una verificación externa (la única que registra acceso, DEC-36) ──

    [Fact]
    public async Task Evidencia_cuyo_fichero_falta_responde_404_avisa_sin_la_clave_y_no_registra_acceso()
    {
        var (contexto, avisos, registro) = (Contexto(), new FabricaDeRegistroEspia(), new RegistroDeAccesosEspia());

        var resultado = await SubcontratasEndpoints.ServirEvidenciaAsync(
            Id, new MediatorFalso(Evidencia()), new AlmacenSinElFichero(), registro, avisos, CancellationToken.None);
        await resultado.ExecuteAsync(contexto);

        AfirmarAusente(contexto, avisos, idEnElAviso: Id);
        registro.Accesos.Should().BeEmpty("DEC-36: no se registra un acceso que no entregó contenido");
    }

    [Fact]
    public async Task Evidencia_presente_se_sirve_y_registra_el_acceso()
    {
        var (contexto, avisos, registro) = (Contexto(), new FabricaDeRegistroEspia(), new RegistroDeAccesosEspia());

        var resultado = await SubcontratasEndpoints.ServirEvidenciaAsync(
            Id, new MediatorFalso(Evidencia()), new AlmacenConElFichero(), registro, avisos, CancellationToken.None);
        await resultado.ExecuteAsync(contexto);

        AfirmarServido(contexto, avisos);
        registro.Accesos.Should().Equal((Id, TipoDocumentoId, TipoAccesoDocumentoSensible.Apertura));
    }

    [Fact]
    public async Task Evidencia_que_la_consulta_no_devuelve_sigue_respondiendo_404_sin_tocar_el_almacen_ni_registrar()
    {
        var (contexto, avisos, registro) = (Contexto(), new FabricaDeRegistroEspia(), new RegistroDeAccesosEspia());

        var resultado = await SubcontratasEndpoints.ServirEvidenciaAsync(
            Id, new MediatorFalso(null), new AlmacenQueNoSeToca(), registro, avisos, CancellationToken.None);
        await resultado.ExecuteAsync(contexto);

        AfirmarNoEncontradoSinAviso(contexto, avisos);
        registro.Accesos.Should().BeEmpty();
    }

    // ── Afirmaciones comunes ──────────────────────────────────────────────

    private static void AfirmarAusente(HttpContext contexto, FabricaDeRegistroEspia avisos, Guid? idEnElAviso)
    {
        contexto.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        LeerCuerpo(contexto).Should().NotContain(ClaveDelFichero, "la respuesta no revela dónde vive el fichero");

        var aviso = avisos.Entradas.Should().ContainSingle().Subject;
        aviso.Nivel.Should().Be(LogLevel.Warning);
        aviso.Mensaje.Should().NotContain(ClaveDelFichero, "el registro tampoco revela dónde vive el fichero");
        if (idEnElAviso is { } id)
            aviso.Mensaje.Should().Contain(id.ToString(), "quien lea el aviso tiene que poder encontrar el registro inconsistente");
    }

    private static void AfirmarServido(HttpContext contexto, FabricaDeRegistroEspia avisos)
    {
        contexto.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        LeerCuerpo(contexto).Should().Be(AlmacenConElFichero.Contenido);
        avisos.Entradas.Should().BeEmpty();
    }

    private static void AfirmarNoEncontradoSinAviso(HttpContext contexto, FabricaDeRegistroEspia avisos)
    {
        contexto.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        avisos.Entradas.Should().BeEmpty("que el recurso no exista o no tenga fichero no es una anomalía del almacén");
    }

    // ── Andamiaje ─────────────────────────────────────────────────────────

    private static TipoDocumentoCentro Plantilla(string? archivoUrl) =>
        new(TipoDocumentoId, CentroId, archivoUrl: archivoUrl, nombreArchivoOriginal: archivoUrl is null ? null : "formulario.pdf");

    private static AdjuntoParaDescargaDto Adjunto() => new("parte.pdf", "application/pdf", ClaveDelFichero);

    private static EvidenciaParaDescargaDto Evidencia() => new("captura.pdf", ClaveDelFichero, TipoDocumentoId);

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

    /// <summary>
    /// Alcance que solo sabe contestar qué Centros se ven (<c>null</c> = todos), que es lo único que
    /// pregunta <c>CentroVisibleAsync</c>. Es un proxy y no una clase que implemente la interfaz para
    /// no tener que seguirla miembro a miembro: cualquier otra pregunta falla diciendo cuál fue.
    /// </summary>
    private static IAlcanceDatosService Alcance(IReadOnlyList<Guid>? centrosVisibles)
    {
        var alcance = DispatchProxy.Create<IAlcanceDatosService, AlcanceSoloDeCentros>();
        ((AlcanceSoloDeCentros)(object)alcance).CentrosVisibles = centrosVisibles;
        return alcance;
    }

    /// <summary>Público y sin sellar porque <see cref="DispatchProxy"/> genera una clase que hereda de esta.</summary>
    public class AlcanceSoloDeCentros : DispatchProxy
    {
        public IReadOnlyList<Guid>? CentrosVisibles { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == nameof(IAlcanceDatosService.ObtenerCentroIdsVisiblesAsync)
                ? Task.FromResult(CentrosVisibles)
                : throw new NotSupportedException($"Pregunta de alcance no prevista en este test: {targetMethod?.Name}.");
    }

    private sealed class RepositorioConUnaFila(TipoDocumentoCentro fila) : ITipoDocumentoCentroRepository
    {
        public Task<TipoDocumentoCentro?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<TipoDocumentoCentro?>(fila);

        public Task<IReadOnlyList<TipoDocumentoCentro>> ObtenerPorTipoDocumentoAsync(Guid tipoDocumentoId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TipoDocumentoCentro>> ObtenerPorCentroAsync(Guid centroId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TipoDocumentoCentro?> ObtenerPorParAsync(Guid tipoDocumentoId, Guid centroId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void Agregar(TipoDocumentoCentro tipoDocumentoCentro) => throw new NotSupportedException();

        public void Eliminar(TipoDocumentoCentro tipoDocumentoCentro) => throw new NotSupportedException();
    }

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
        internal const string Contenido = "contenido-de-prueba";

        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(Contenido)));

        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class AlmacenQueNoSeToca : IFileStorageService
    {
        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Sin recurso que servir no hay nada que abrir.");

        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RegistroDeAccesosEspia : IRegistroAccesoDocumentoSensibleService
    {
        public List<(Guid RecursoId, Guid TipoDocumentoId, TipoAccesoDocumentoSensible Tipo)> Accesos { get; } = [];

        public Task RegistrarSiSensibleAsync(Guid documentoId, TipoAccesoDocumentoSensible tipoAcceso, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("La evidencia se registra con su TipoDocumentoId, no como un Documento.");

        public Task RegistrarSiSensibleAsync(
            Guid recursoId, Guid tipoDocumentoId, TipoAccesoDocumentoSensible tipoAcceso, CancellationToken cancellationToken = default)
        {
            Accesos.Add((recursoId, tipoDocumentoId, tipoAcceso));
            return Task.CompletedTask;
        }
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
