using CaeManager.Application.Common;
using CaeManager.Infrastructure.Integraciones;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CaeManager.IntegrationTests.Integraciones;

/// <summary>
/// Los logs de aplicación no son un canal protegido: el aviso de la compensación best-effort de la ingesta
/// de webhooks identifica el evento y el tipo de la excepción, nunca la clave del fichero ni el mensaje de
/// la excepción (suele repetirla). El helper es <c>internal</c> de Infrastructure y solo lo ve este proyecto,
/// de ahí que el test viva aquí; no usa base de datos.
/// </summary>
public class CompensacionBlobsHuerfanosIngestaTests
{
    [Fact]
    public async Task El_aviso_del_blob_huerfano_que_no_se_pudo_eliminar_identifica_el_evento_sin_revelar_la_clave()
    {
        var eventoId = Guid.NewGuid();
        const string clave = "0f1e2d3c4b5a69788796a5b4c3d2e1f0/adjunto-secreto.pdf";
        var almacenamiento = new AlmacenamientoQueFallaAlEliminar(new IOException($"Acceso denegado a {clave}"));
        var registro = new RegistroDeAvisos();

        await CompensacionBlobsHuerfanosIngesta.EliminarSiOrfanosAsync(eventoId, [clave], almacenamiento, registro);

        almacenamiento.Intentos.Should().Be(1);
        var aviso = registro.Entradas.Should().ContainSingle().Subject;
        aviso.Nivel.Should().Be(LogLevel.Warning);
        aviso.Texto.Should().Contain(eventoId.ToString()).And.Contain(nameof(IOException))
            .And.NotContain(clave).And.NotContain("adjunto-secreto").And.NotContain("Acceso denegado");
    }

    [Fact]
    public async Task Un_fallo_al_eliminar_un_blob_no_impide_intentar_los_demas()
    {
        var almacenamiento = new AlmacenamientoQueFallaAlEliminar(new IOException("disco no disponible"));
        var registro = new RegistroDeAvisos();

        await CompensacionBlobsHuerfanosIngesta.EliminarSiOrfanosAsync(Guid.NewGuid(), ["a.pdf", "b.pdf"], almacenamiento, registro);

        almacenamiento.Intentos.Should().Be(2);
        registro.Entradas.Should().HaveCount(2);
    }

    private sealed class AlmacenamientoQueFallaAlEliminar(Exception excepcion) : IFileStorageService
    {
        public int Intentos { get; private set; }

        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default)
        {
            Intentos++;
            throw excepcion;
        }

        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    /// <summary>Conserva cada entrada con lo que un sumidero real escribiría: mensaje formateado y excepción entera.</summary>
    private sealed class RegistroDeAvisos : ILogger
    {
        public List<(LogLevel Nivel, string Texto)> Entradas { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entradas.Add((logLevel, formatter(state, exception) + (exception is null ? string.Empty : " " + exception)));
    }
}
