using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Documentos;

public class SelloEmpresaTests
{
    [Fact]
    public void Constructor_asigna_los_campos_recibidos()
    {
        var empresaId = Guid.NewGuid();
        var actualizadaEnUtc = DateTime.UtcNow;

        var sello = new SelloEmpresa(empresaId, "url/sello.png", actualizadaEnUtc);

        sello.EmpresaId.Should().Be(empresaId);
        sello.ImagenUrl.Should().Be("url/sello.png");
        sello.ActualizadaEnUtc.Should().Be(actualizadaEnUtc);
    }

    [Fact]
    public void Constructor_rechaza_empresa_vacia()
    {
        var accion = () => new SelloEmpresa(Guid.Empty, "url/sello.png", DateTime.UtcNow);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_rechaza_url_vacia()
    {
        var accion = () => new SelloEmpresa(Guid.NewGuid(), "  ", DateTime.UtcNow);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Reemplazar_actualiza_url_y_fecha()
    {
        var sello = new SelloEmpresa(Guid.NewGuid(), "url/viejo.png", DateTime.UtcNow.AddDays(-1));
        var nuevaFecha = DateTime.UtcNow;

        sello.Reemplazar("url/nuevo.png", nuevaFecha);

        sello.ImagenUrl.Should().Be("url/nuevo.png");
        sello.ActualizadaEnUtc.Should().Be(nuevaFecha);
    }
}
