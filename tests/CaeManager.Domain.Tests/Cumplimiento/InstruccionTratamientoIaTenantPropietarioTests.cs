using CaeManager.Domain.Cumplimiento;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Cumplimiento;

public class InstruccionTratamientoIaTenantPropietarioTests
{
    private static InstruccionTratamientoIaTenantPropietario Crear(DateTime? fecha = null) => new(
        "Draft-2026-09-03", "Draft-2026-09-03", fecha ?? DateTime.UtcNow,
        OrigenInstruccionTratamientoIa.AltaManualPlataforma, Guid.NewGuid());

    [Fact]
    public void Constructor_asigna_los_valores_y_nace_vigente()
    {
        var fecha = DateTime.UtcNow;
        var registradaPor = Guid.NewGuid();

        var instruccion = new InstruccionTratamientoIaTenantPropietario(
            "v1", "v2", fecha, OrigenInstruccionTratamientoIa.AltaManualPlataforma, registradaPor);

        instruccion.VersionDpaAceptada.Should().Be("v1");
        instruccion.VersionAnexoSubencargadosAceptada.Should().Be("v2");
        instruccion.FechaAceptacionUtc.Should().Be(fecha);
        instruccion.OrigenInstruccion.Should().Be(OrigenInstruccionTratamientoIa.AltaManualPlataforma);
        instruccion.RegistradaPorUsuarioId.Should().Be(registradaPor);
        instruccion.EstaVigente.Should().BeTrue();
        instruccion.RevocadaEnUtc.Should().BeNull();
        instruccion.MotivoRevocacion.Should().BeNull();
    }

    [Theory]
    [InlineData("", "v")]
    [InlineData("  ", "v")]
    [InlineData("v", "")]
    [InlineData("v", "  ")]
    public void Constructor_rechaza_version_vacia(string versionDpa, string versionAnexo)
    {
        var accion = () => new InstruccionTratamientoIaTenantPropietario(
            versionDpa, versionAnexo, DateTime.UtcNow, OrigenInstruccionTratamientoIa.AltaManualPlataforma, Guid.NewGuid());

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_rechaza_version_demasiado_larga()
    {
        var accion = () => new InstruccionTratamientoIaTenantPropietario(
            new string('x', InstruccionTratamientoIaTenantPropietario.LongitudMaximaVersion + 1), "v",
            DateTime.UtcNow, OrigenInstruccionTratamientoIa.AltaManualPlataforma, Guid.NewGuid());

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_rechaza_usuario_registrador_vacio()
    {
        var accion = () => new InstruccionTratamientoIaTenantPropietario(
            "v1", "v2", DateTime.UtcNow, OrigenInstruccionTratamientoIa.AltaManualPlataforma, Guid.Empty);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Revocar_cierra_la_fila_y_dice_estar_vigente_falso()
    {
        var instruccion = Crear();
        var ahora = DateTime.UtcNow;

        instruccion.Revocar("El tenant rescindió el contrato", ahora);

        instruccion.EstaVigente.Should().BeFalse();
        instruccion.RevocadaEnUtc.Should().Be(ahora);
        instruccion.MotivoRevocacion.Should().Be("El tenant rescindió el contrato");
    }

    [Fact]
    public void Revocar_dos_veces_lanza()
    {
        var instruccion = Crear();
        instruccion.Revocar("Motivo inicial", DateTime.UtcNow);

        var accion = () => instruccion.Revocar("Segundo intento", DateTime.UtcNow);

        accion.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Revocar_sin_motivo_lanza()
    {
        var instruccion = Crear();

        var accion = () => instruccion.Revocar("   ", DateTime.UtcNow);

        accion.Should().Throw<ArgumentException>();
        instruccion.EstaVigente.Should().BeTrue("un intento de revocación inválido no debe cerrar la fila a medias");
    }

    [Fact]
    public void Revocar_con_motivo_demasiado_largo_lanza()
    {
        var instruccion = Crear();

        var accion = () => instruccion.Revocar(new string('x', InstruccionTratamientoIaTenantPropietario.LongitudMaximaMotivoRevocacion + 1), DateTime.UtcNow);

        accion.Should().Throw<ArgumentException>();
    }
}
