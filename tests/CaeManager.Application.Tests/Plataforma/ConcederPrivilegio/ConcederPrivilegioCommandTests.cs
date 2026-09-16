using CaeManager.Application.Plataforma.Commands.AutoConcederPrivilegio;
using CaeManager.Application.Plataforma.Commands.ConcederPrivilegio;
using FluentAssertions;

namespace CaeManager.Application.Tests.Plataforma.ConcederPrivilegio;

public class ConcederPrivilegioCommandValidatorTests
{
    private static ConcederPrivilegioCommand Comando(
        Guid? beneficiario = null, Guid? tenant = null, int dias = 1, string motivo = "Aprovisionamiento inicial") =>
        new(beneficiario ?? Guid.NewGuid(), tenant ?? Guid.NewGuid(), dias, motivo);

    [Fact]
    public void Rechaza_beneficiario_vacio()
    {
        var validador = new ConcederPrivilegioCommandValidator();

        validador.Validate(Comando(beneficiario: Guid.Empty)).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Rechaza_tenant_objetivo_vacio()
    {
        var validador = new ConcederPrivilegioCommandValidator();

        validador.Validate(Comando(tenant: Guid.Empty)).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Rechaza_motivo_vacio()
    {
        var validador = new ConcederPrivilegioCommandValidator();

        validador.Validate(Comando(motivo: "")).IsValid.Should().BeFalse(
            "el motivo es obligatorio aquí, a diferencia de la auto-concesión: involucra a un tercero");
    }

    [Fact]
    public void Acepta_noventa_dias_y_rechaza_noventaiuno()
    {
        var validador = new ConcederPrivilegioCommandValidator();

        validador.Validate(Comando(dias: 90)).IsValid.Should().BeTrue("noventa días es el techo, inclusive");
        validador.Validate(Comando(dias: 91)).IsValid.Should().BeFalse();
    }

    /// <summary>
    /// Mismo motivo que <c>VentanaDePrivilegioCuatroHorasTests</c> para las
    /// ventanas de sesión: los dos comandos de concesión viven en el mismo
    /// dominio de decisión —cuánto puede vivir una concesión de plataforma—
    /// aunque su constante esté deliberadamente duplicada, no compartida, para
    /// no acoplar los dos comandos entre sí.
    /// </summary>
    [Fact]
    public void Los_dos_techos_de_vigencia_son_el_mismo_valor_aunque_esten_duplicados()
    {
        ConcederPrivilegioCommandValidator.MaximoDiasDeVigencia.Should().Be(
            AutoConcederPrivilegioCommandValidator.MaximoDiasDeVigencia,
            "auto-concesión y concesión a un tercero son la misma decisión de producto vista desde dos " +
            "comandos; si divergen aquí, alguien cambió uno sin mirar el otro");
    }
}
