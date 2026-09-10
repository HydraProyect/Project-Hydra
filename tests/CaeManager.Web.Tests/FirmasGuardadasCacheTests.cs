using System.Text;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Queries.ObtenerFirmaGuardadaUsuario;
using CaeManager.Application.Documentos.Queries.ObtenerSelloEmpresa;
using CaeManager.Web.Features.Documentos;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// <b>Una firma manuscrita no puede quedar en el disco del navegador.</b>
///
/// <para>
/// <c>/mi-firma/archivo</c> y <c>/empresas/{id}/sello/archivo</c> sirven
/// instrumentos de firma: quien recupere el PNG de la caché de un equipo
/// compartido puede estamparlo en cualquier documento. Es el mismo riesgo que
/// motivó <c>CabecerasArchivoSensible</c> para el PDF de un Documento
/// sensible, con un agravante — el PDF se filtra, la firma se reutiliza.
/// </para>
///
/// <para>
/// <b>Qué observa este test y qué no.</b> Ejecuta el <c>IResult</c> contra el
/// <c>HttpContext</c>, no se queda en el valor que el manejador escribió:
/// <c>Results.File(..., enableRangeProcessing: true)</c> escribe sus propias
/// cabeceras al ejecutarse (<c>Content-Type</c>, <c>Accept-Ranges</c>,
/// <c>Content-Length</c>) y la pregunta es qué queda en la respuesta
/// <b>después</b> de todo eso.
/// </para>
///
/// <para>
/// El contexto arranca <b>sin</b> el <c>no-store, private</c> que
/// <c>UseCabecerasSeguridad</c> pone para toda la aplicación, y eso es
/// deliberado: sembrarlo haría pasar el test aunque el endpoint no declarara
/// nada —el valor sembrado seguiría ahí— y el instrumento dejaría de observar
/// la propiedad que dice observar. Sin sembrar, lo que se mide es lo que
/// aporta el endpoint por sí solo.
/// </para>
///
/// <para>
/// No observa el registro del middleware en Program.cs — eso es una propiedad
/// del arranque, no del endpoint. Precisamente por eso el endpoint declara la
/// cabecera él mismo en vez de heredarla: mientras dependiera del middleware,
/// la política de la respuesta más sensible de la aplicación era un efecto del
/// orden de registro, y nada la vigilaba.
/// </para>
/// </summary>
public class FirmasGuardadasCacheTests
{
    private static readonly Guid Empresa = Guid.Parse("55555555-5555-5555-5555-555555555555");

    [Fact]
    public async Task La_firma_del_usuario_no_se_almacena_en_ninguna_cache()
    {
        var contexto = ContextoDeUnaPeticionSinCabecerasDeCache();

        await EjecutarAsync(
            contexto,
            FirmasGuardadasEndpoints.ServirFirmaAsync(
                contexto,
                new MediatorFalso(new FirmaGuardadaUsuarioDto("firma.png", DateTime.UtcNow)),
                new AlmacenamientoFalso(),
                CancellationToken.None));

        var cacheControl = contexto.Response.Headers.CacheControl.ToString();

        cacheControl.Should().Contain("no-store",
            "sin no-store el navegador aplica caducidad heurística y la firma sobrevive al cierre de sesión");
        cacheControl.Should().Contain("private",
            "ningún intermediario compartido puede quedarse una copia, ni siquiera uno que ignore no-store");
        contexto.Response.Headers.Pragma.ToString().Should().Contain("no-cache");
    }

    [Fact]
    public async Task El_sello_de_la_empresa_no_se_almacena_en_ninguna_cache()
    {
        var contexto = ContextoDeUnaPeticionSinCabecerasDeCache();

        await EjecutarAsync(
            contexto,
            FirmasGuardadasEndpoints.ServirSelloAsync(
                Empresa,
                contexto,
                new MediatorFalso(new SelloEmpresaDto("sello.png", DateTime.UtcNow)),
                new AlmacenamientoFalso(),
                CancellationToken.None));

        var cacheControl = contexto.Response.Headers.CacheControl.ToString();

        cacheControl.Should().Contain("no-store");
        cacheControl.Should().Contain("private");
    }

    /// <summary>
    /// El control positivo del par de arriba: confirma que lo que se midió es
    /// la respuesta que <b>de verdad entrega la imagen</b>, y no un camino
    /// donde la cabecera de caché ya no significaría nada. Los dos tests
    /// anteriores solo miran cabeceras: por sí solos no distinguen "el
    /// endpoint declara la política al servir el PNG" de "el endpoint declara
    /// la política y luego no sirve nada".
    /// </summary>
    [Fact]
    public async Task Lo_medido_es_la_respuesta_que_entrega_el_PNG()
    {
        var contexto = ContextoDeUnaPeticionSinCabecerasDeCache();

        await EjecutarAsync(
            contexto,
            FirmasGuardadasEndpoints.ServirFirmaAsync(
                contexto,
                new MediatorFalso(new FirmaGuardadaUsuarioDto("firma.png", DateTime.UtcNow)),
                new AlmacenamientoFalso(),
                CancellationToken.None));

        contexto.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        contexto.Response.ContentType.Should().Be("image/png");
        LeerCuerpo(contexto).Should().Be(AlmacenamientoFalso.Contenido);
    }

    // ── Andamiaje ──────────────────────────────────────────────────────────

    /// <summary>
    /// Un contexto desnudo: sin ninguna cabecera de caché puesta de antemano
    /// (ver el porqué en el resumen de la clase). <c>RequestServices</c> con
    /// <c>ILoggerFactory</c> y un <c>Body</c> escribible son lo que
    /// <c>FileStreamHttpResult.ExecuteAsync</c> exige para poder ejecutarse de
    /// verdad en vez de quedarse en el objeto devuelto.
    /// </summary>
    private static DefaultHttpContext ContextoDeUnaPeticionSinCabecerasDeCache()
    {
        var servicios = new ServiceCollection();
        servicios.AddLogging();

        var contexto = new DefaultHttpContext { RequestServices = servicios.BuildServiceProvider() };
        contexto.Response.Body = new MemoryStream();

        contexto.Response.Headers.CacheControl.ToString().Should().BeEmpty(
            "el punto de partida del test es que nadie ha declarado política de caché todavía");

        return contexto;
    }

    private static async Task EjecutarAsync(HttpContext contexto, Task<IResult> manejador) =>
        await (await manejador).ExecuteAsync(contexto);

    private static string LeerCuerpo(HttpContext contexto)
    {
        var cuerpo = (MemoryStream)contexto.Response.Body;
        return Encoding.UTF8.GetString(cuerpo.ToArray());
    }

    private sealed class AlmacenamientoFalso : IFileStorageService
    {
        internal const string Contenido = "PNG-de-prueba";

        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(Contenido)));

        public Task<string> GuardarAsync(Stream contenido, string nombreArchivo, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class MediatorFalso(object? respuesta) : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)respuesta!);

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
            where TRequest : IRequest => Task.CompletedTask;

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            Task.FromResult(respuesta);

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(
            IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }
}
