using System.Reflection;
using System.Text;
using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Queries.ObtenerAdjuntoParaDescarga;
using CaeManager.Application.Documentos.Queries.ObtenerFirmaGuardadaUsuario;
using CaeManager.Application.Documentos.Queries.ObtenerSelloEmpresa;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Features.Centros;
using CaeManager.Web.Features.Comunicaciones;
using CaeManager.Web.Features.Documentos;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CaeManager.Web.Tests;

// Ejecuta los manejadores y sus IResult; no observa routing ni autorización.
public class AdjuntosEndpointsArchivoAusenteTests
{
    private const string Clave = "tenant/clave-privada.png";
    private static readonly Guid RecursoId = Guid.Parse("0199c0de-0000-7000-8000-000000000011");

    [Theory]
    [InlineData("comunicacion")]
    [InlineData("requisito")]
    [InlineData("firma")]
    [InlineData("sello")]
    public async Task El_archivo_ausente_responde_404_vacio_y_avisa_por_Id(string camino)
    {
        var avisos = new FabricaDeRegistroEspia();
        var contexto = Contexto();
        var resultado = await ServirAsync(camino, contexto, new Almacen(false), avisos);
        await resultado.ExecuteAsync(contexto);

        contexto.Response.StatusCode.Should().Be(404);
        ((MemoryStream)contexto.Response.Body).Length.Should().Be(0);
        contexto.Response.Headers.ContentDisposition.Should().BeEmpty();
        avisos.Entradas.Should().ContainSingle().Which.Nivel.Should().Be(LogLevel.Warning);
        avisos.Entradas[0].Mensaje.Should().Contain(RecursoId.ToString()).And.NotContain(Clave);
        avisos.Excepciones.Should().ContainSingle().Which.Should().BeNull();
    }

    [Theory]
    [InlineData("comunicacion")]
    [InlineData("requisito")]
    [InlineData("firma")]
    [InlineData("sello")]
    public async Task El_archivo_presente_sigue_sirviendose(string camino)
    {
        var avisos = new FabricaDeRegistroEspia();
        var contexto = Contexto();
        await (await ServirAsync(camino, contexto, new Almacen(true), avisos)).ExecuteAsync(contexto);

        contexto.Response.StatusCode.Should().Be(200);
        Encoding.UTF8.GetString(((MemoryStream)contexto.Response.Body).ToArray()).Should().Be("contenido");
        avisos.Entradas.Should().BeEmpty();
    }

    private static Task<IResult> ServirAsync(string camino, HttpContext contexto, IFileStorageService almacen, ILoggerFactory avisos) => camino switch
    {
        "comunicacion" => ComunicacionesEndpoints.ServirAdjuntoAsync(RecursoId,
            new MediatorFalso(new AdjuntoParaDescargaDto("adjunto.png", "image/png", Clave)), almacen, avisos, default),
        "requisito" => RequisitosDocumentalesEndpoints.ServirPlantillaAsync(RecursoId,
            DispatchProxy.Create<ITipoDocumentoCentroRepository, RepositorioProxy>(),
            DispatchProxy.Create<IAlcanceDatosService, AlcanceProxy>(), almacen, avisos, default),
        "firma" => FirmasGuardadasEndpoints.ServirFirmaAsync(contexto,
            new MediatorFalso(new FirmaGuardadaUsuarioDto(Clave, DateTime.UtcNow, RecursoId)), almacen, avisos, default),
        "sello" => FirmasGuardadasEndpoints.ServirSelloAsync(RecursoId, contexto,
            new MediatorFalso(new SelloEmpresaDto(Clave, DateTime.UtcNow)), almacen, avisos, default),
        _ => throw new ArgumentOutOfRangeException(nameof(camino))
    };

    public class RepositorioProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod!.Name == nameof(ITipoDocumentoCentroRepository.ObtenerPorIdAsync)
                ? Task.FromResult<TipoDocumentoCentro?>(new TipoDocumentoCentro(Guid.NewGuid(), Guid.NewGuid(), archivoUrl: Clave))
                : throw new NotSupportedException();
    }

    public class AlcanceProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod!.Name == nameof(IAlcanceDatosService.ObtenerCentroIdsVisiblesAsync)
                ? Task.FromResult<IReadOnlyList<Guid>?>(null)
                : throw new NotSupportedException();
    }

    private static DefaultHttpContext Contexto()
    {
        var contexto = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        contexto.Response.Body = new MemoryStream();
        return contexto;
    }

    private sealed class Almacen(bool existe) : IFileStorageService
    {
        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) =>
            existe ? Task.FromResult<Stream>(new MemoryStream("contenido"u8.ToArray()))
                : throw new FileNotFoundException("Falta " + identificador, identificador);
        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class FabricaDeRegistroEspia : ILoggerFactory, ILogger
    {
        public List<(LogLevel Nivel, string Mensaje)> Entradas { get; } = [];

        public List<Exception?> Excepciones { get; } = [];

        public ILogger CreateLogger(string categoryName) => this;

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Excepciones.Add(exception);
            Entradas.Add((logLevel, formatter(state, exception) + (exception is null ? string.Empty : " " + exception)));
        }
    }

    private sealed class MediatorFalso(object? respuesta) : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            respuesta is Exception ex ? Task.FromException<TResponse>(ex) : Task.FromResult((TResponse)respuesta!);

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
