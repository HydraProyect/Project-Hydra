using CaeManager.Application.Comunicaciones.Commands.ActualizarDocumentoDesdeAdjunto;
using CaeManager.Application.Tests.Common;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CaeManager.Application.Tests.Comunicaciones;

public class ActualizarDocumentoDesdeAdjuntoAvisoDeLimpiezaTests
{
    [Fact]
    public async Task Adjunto_sin_blob_devuelve_error_controlado()
    {
        var contexto = new ComunicacionesQueryContextFalso();
        var conversacion = new Conversacion("Documentación", empresaId: Guid.NewGuid());
        var mensaje = new Mensaje(conversacion.Id, DireccionMensaje.Entrante, CanalConversacion.Correo, "remitente", "asunto", DateTime.UtcNow);
        var adjunto = new AdjuntoMensaje(mensaje.Id, "adjunto.pdf", "application/pdf", 2, "inexistente.pdf");
        contexto.ListaConversaciones.Add(conversacion);
        contexto.ListaMensajes.Add(mensaje);
        contexto.ListaAdjuntosMensaje.Add(adjunto);
        var mediador = new MediadorFalso();
        var almacen = new FileStorageServiceFalso();
        var handler = new ActualizarDocumentoDesdeAdjuntoCommandHandler(contexto, new AlcanceDatosServiceFalso(),
            new DocumentosQueryContextFalso(), mediador, mediador, almacen, Microsoft.Extensions.Logging.Abstractions.NullLogger<ActualizarDocumentoDesdeAdjuntoCommandHandler>.Instance);

        var resultado = await handler.Handle(new ActualizarDocumentoDesdeAdjuntoCommand(adjunto.Id, Guid.NewGuid(),
            Guid.NewGuid(), null, DateOnly.FromDateTime(DateTime.UtcNow), null, null), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Adjunto.ArchivoNoValido");
        almacen.ArchivosGuardados.Should().Be(0);
    }

    [Theory]
    [InlineData(CanalConversacion.Correo, false)]
    [InlineData(CanalConversacion.WhatsApp, false)]
    [InlineData(CanalConversacion.Correo, true)]
    [InlineData(CanalConversacion.WhatsApp, true)]
    public async Task Valida_el_contenido_del_adjunto(CanalConversacion canal, bool valido)
    {
        var contexto = new ComunicacionesQueryContextFalso();
        var conversacion = new Conversacion("Documentación", empresaId: Guid.NewGuid());
        var mensaje = new Mensaje(conversacion.Id, DireccionMensaje.Entrante, canal, "remitente", "asunto", DateTime.UtcNow);
        var almacenamiento = new FileStorageServiceFalso();
        var clave = await almacenamiento.GuardarAsync(new MemoryStream(valido ? "%PDF-"u8.ToArray() : "MZ"u8.ToArray()), "adjunto.pdf");
        var adjunto = new AdjuntoMensaje(mensaje.Id, "adjunto.pdf", "application/pdf", 2, clave);
        contexto.ListaConversaciones.Add(conversacion);
        contexto.ListaMensajes.Add(mensaje);
        contexto.ListaAdjuntosMensaje.Add(adjunto);
        var mediador = new MediadorFalso();
        var handler = new ActualizarDocumentoDesdeAdjuntoCommandHandler(contexto, new AlcanceDatosServiceFalso(),
            new DocumentosQueryContextFalso(), mediador, mediador, almacenamiento, Microsoft.Extensions.Logging.Abstractions.NullLogger<ActualizarDocumentoDesdeAdjuntoCommandHandler>.Instance);

        var resultado = await handler.Handle(new ActualizarDocumentoDesdeAdjuntoCommand(adjunto.Id, Guid.NewGuid(),
            Guid.NewGuid(), null, DateOnly.FromDateTime(DateTime.UtcNow), null, null), CancellationToken.None);

        resultado.EsExitoso.Should().Be(valido);
        if (!valido) resultado.Error.Codigo.Should().Be("Adjunto.ArchivoNoValido");
        almacenamiento.ArchivosGuardados.Should().Be(valido ? 2 : 1);
    }

    private sealed class MediadorFalso : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)(object)Result.Exito(Guid.NewGuid()));
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => throw new NotSupportedException();
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    // La limpieza de la copia huérfana no usa ninguna otra dependencia del handler: se prueba directamente.
    private static ActualizarDocumentoDesdeAdjuntoCommandHandler CrearHandler(
        FileStorageServiceFalso almacenamiento, RegistroDeAvisos<ActualizarDocumentoDesdeAdjuntoCommandHandler> registro) =>
        new(null!, null!, null!, null!, null!, almacenamiento, registro);

    [Fact]
    public async Task El_aviso_de_la_copia_huerfana_que_no_se_pudo_borrar_identifica_el_adjunto_sin_revelar_la_clave()
    {
        var adjuntoId = Guid.NewGuid();
        var almacenamiento = new FileStorageServiceFalso();
        var clave = await almacenamiento.GuardarAsync(new MemoryStream([1, 2, 3]), "adjunto.pdf");
        almacenamiento.ExcepcionAlEliminar = new IOException($"Acceso denegado a {clave}");
        var registro = new RegistroDeAvisos<ActualizarDocumentoDesdeAdjuntoCommandHandler>();

        await CrearHandler(almacenamiento, registro).EliminarCopiaHuerfanaAsync(adjuntoId, clave, CancellationToken.None);

        var aviso = registro.Entradas.Should().ContainSingle().Subject;
        aviso.Nivel.Should().Be(LogLevel.Warning);
        aviso.Texto.Should().Contain(adjuntoId.ToString()).And.Contain(nameof(IOException))
            .And.NotContain(clave).And.NotContain("Acceso denegado");
    }

    [Fact]
    public async Task Si_la_copia_se_borra_bien_no_avisa()
    {
        var almacenamiento = new FileStorageServiceFalso();
        var clave = await almacenamiento.GuardarAsync(new MemoryStream([1, 2, 3]), "adjunto.pdf");
        var registro = new RegistroDeAvisos<ActualizarDocumentoDesdeAdjuntoCommandHandler>();

        await CrearHandler(almacenamiento, registro).EliminarCopiaHuerfanaAsync(Guid.NewGuid(), clave, CancellationToken.None);

        registro.Entradas.Should().BeEmpty();
        almacenamiento.ArchivosGuardados.Should().Be(0);
    }
}
