using CaeManager.Domain.Comunicaciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Comunicaciones;

public class AdjuntoMensajeTests
{
    [Fact]
    public void Mensaje_agrega_un_adjunto_valido()
    {
        var mensaje = new Mensaje(Guid.NewGuid(), DireccionMensaje.Saliente, CanalConversacion.Correo, "gestor@hydra.local", "<p>Adjunto el formato.</p>", DateTime.UtcNow);

        var adjunto = mensaje.AgregarAdjunto("formato-centro.pdf", "application/pdf", 12_345, "storage-key-1");

        mensaje.Adjuntos.Should().ContainSingle().Which.Should().BeSameAs(adjunto);
        adjunto.MensajeId.Should().Be(mensaje.Id);
        adjunto.NombreArchivo.Should().Be("formato-centro.pdf");
        adjunto.TipoContenido.Should().Be("application/pdf");
        adjunto.TamanoBytes.Should().Be(12_345);
        adjunto.ArchivoUrl.Should().Be("storage-key-1");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Rechaza_un_adjunto_sin_nombre_de_archivo(string nombreInvalido)
    {
        var accion = () => new AdjuntoMensaje(Guid.NewGuid(), nombreInvalido, "application/pdf", 100, "storage-key-1");

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rechaza_un_adjunto_sin_archivo_guardado()
    {
        var accion = () => new AdjuntoMensaje(Guid.NewGuid(), "formato.pdf", "application/pdf", 100, "");

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rechaza_un_tamano_negativo()
    {
        var accion = () => new AdjuntoMensaje(Guid.NewGuid(), "formato.pdf", "application/pdf", -1, "storage-key-1");

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rechaza_pertenecer_a_un_mensaje_vacio()
    {
        var accion = () => new AdjuntoMensaje(Guid.Empty, "formato.pdf", "application/pdf", 100, "storage-key-1");

        accion.Should().Throw<ArgumentException>();
    }
}
