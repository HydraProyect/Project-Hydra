using CaeManager.Domain.Cumplimiento;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Cumplimiento;

public class AceptacionTerminosTests
{
    [Fact]
    public void Constructor_asigna_los_valores()
    {
        var usuarioId = Guid.NewGuid();
        var fecha = DateTime.UtcNow;

        var aceptacion = new AceptacionTerminos(usuarioId, "2026-08-08", fecha);

        aceptacion.UsuarioId.Should().Be(usuarioId);
        aceptacion.VersionDocumento.Should().Be("2026-08-08");
        aceptacion.FechaAceptacionUtc.Should().Be(fecha);
    }

    [Fact]
    public void Constructor_rechaza_usuario_vacio()
    {
        var accion = () => new AceptacionTerminos(Guid.Empty, "2026-08-08", DateTime.UtcNow);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_rechaza_version_vacia()
    {
        var accion = () => new AceptacionTerminos(Guid.NewGuid(), "  ", DateTime.UtcNow);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_rechaza_version_demasiado_larga()
    {
        var accion = () => new AceptacionTerminos(Guid.NewGuid(), new string('x', AceptacionTerminos.LongitudMaximaVersionDocumento + 1), DateTime.UtcNow);

        accion.Should().Throw<ArgumentException>();
    }
}
