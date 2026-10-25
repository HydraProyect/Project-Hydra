using CaeManager.Application.Comunicaciones.Commands.ActualizarDocumentoDesdeAdjunto;
using CaeManager.Application.Tests.Common;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CaeManager.Application.Tests.Comunicaciones;

public class ActualizarDocumentoDesdeAdjuntoAvisoDeLimpiezaTests
{
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
