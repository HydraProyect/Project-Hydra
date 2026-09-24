using CaeManager.Domain.Tenants;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Tenants;

/// <summary>
/// P8: la revocación de una asignación de Operador Delegado es definitiva y
/// siempre lleva motivo. No existe operación inversa.
/// </summary>
public class AsignacionOperadorDelegadoTests
{
    private static AsignacionOperadorDelegado Crear() => new(Guid.NewGuid(), Guid.NewGuid(), "GestorCae");

    [Fact]
    public void Nace_vigente()
    {
        var asignacion = Crear();

        asignacion.EstaRevocada.Should().BeFalse();
        asignacion.RevocadaEnUtc.Should().BeNull();
        asignacion.MotivoRevocacion.Should().BeNull();
    }

    [Fact]
    public void Revocar_marca_fecha_y_motivo()
    {
        var asignacion = Crear();
        var ahora = new DateTime(2026, 9, 24, 1, 0, 0, DateTimeKind.Utc);

        asignacion.Revocar("Rol de Propiedad no delegable", ahora);

        asignacion.EstaRevocada.Should().BeTrue();
        asignacion.RevocadaEnUtc.Should().Be(ahora);
        asignacion.MotivoRevocacion.Should().Be("Rol de Propiedad no delegable");
    }

    [Fact]
    public void Revocar_dos_veces_falla()
    {
        var asignacion = Crear();
        asignacion.Revocar("Primera", DateTime.UtcNow);

        asignacion.Invoking(a => a.Revocar("Segunda", DateTime.UtcNow)).Should().Throw<InvalidOperationException>();
        asignacion.MotivoRevocacion.Should().Be("Primera");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Revocar_sin_motivo_falla(string motivo)
    {
        Crear().Invoking(a => a.Revocar(motivo, DateTime.UtcNow)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Revocar_con_motivo_demasiado_largo_falla()
    {
        var motivo = new string('x', AsignacionOperadorDelegado.LongitudMaximaMotivoRevocacion + 1);

        Crear().Invoking(a => a.Revocar(motivo, DateTime.UtcNow)).Should().Throw<ArgumentException>();
    }
}
