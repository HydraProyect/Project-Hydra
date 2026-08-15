using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Documentos;

public class FirmaGuardadaUsuarioTests
{
    [Fact]
    public void Constructor_asigna_los_campos_recibidos()
    {
        var usuarioId = Guid.NewGuid();
        var actualizadaEnUtc = DateTime.UtcNow;

        var firma = new FirmaGuardadaUsuario(usuarioId, "url/firma.png", actualizadaEnUtc);

        firma.UsuarioId.Should().Be(usuarioId);
        firma.ImagenUrl.Should().Be("url/firma.png");
        firma.ActualizadaEnUtc.Should().Be(actualizadaEnUtc);
    }

    [Fact]
    public void Constructor_rechaza_usuario_vacio()
    {
        var accion = () => new FirmaGuardadaUsuario(Guid.Empty, "url/firma.png", DateTime.UtcNow);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_rechaza_url_vacia()
    {
        var accion = () => new FirmaGuardadaUsuario(Guid.NewGuid(), "  ", DateTime.UtcNow);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Reemplazar_actualiza_url_y_fecha()
    {
        var firma = new FirmaGuardadaUsuario(Guid.NewGuid(), "url/vieja.png", DateTime.UtcNow.AddDays(-1));
        var nuevaFecha = DateTime.UtcNow;

        firma.Reemplazar("url/nueva.png", nuevaFecha);

        firma.ImagenUrl.Should().Be("url/nueva.png");
        firma.ActualizadaEnUtc.Should().Be(nuevaFecha);
    }

    [Fact]
    public void Reemplazar_rechaza_url_vacia()
    {
        var firma = new FirmaGuardadaUsuario(Guid.NewGuid(), "url/vieja.png", DateTime.UtcNow);

        var accion = () => firma.Reemplazar("", DateTime.UtcNow);

        accion.Should().Throw<ArgumentException>();
        firma.ImagenUrl.Should().Be("url/vieja.png");
    }
}
