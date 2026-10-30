using CaeManager.Application.Plataforma.Commands.AutoConcederPrivilegio;
using CaeManager.Application.Plataforma.Commands.ConcederPrivilegio;
using CaeManager.Domain.Plataforma;
using FluentAssertions;

namespace CaeManager.Application.Tests.Plataforma.ConcederPrivilegio;

public class ConcederPrivilegioCommandValidatorTests
{
    private static ConcederPrivilegioCommand Comando(
        Guid? beneficiario = null, Guid? tenant = null, int dias = 1, string motivo = "Aprovisionamiento inicial",
        CapacidadPrivilegio capacidad = CapacidadPrivilegio.Aprovisionamiento) =>
        new(beneficiario ?? Guid.NewGuid(), tenant ?? Guid.NewGuid(), dias, motivo, capacidad);

    [Theory]
    [InlineData(CapacidadPrivilegio.Aprovisionamiento)]
    [InlineData(CapacidadPrivilegio.RestablecimientoSegundoFactor)]
    [InlineData(CapacidadPrivilegio.RevocacionOperadorCaeExterno)]
    public void Acepta_las_capacidades_concedibles_a_un_tercero(CapacidadPrivilegio capacidad)
    {
        var validador = new ConcederPrivilegioCommandValidator();

        validador.Validate(Comando(capacidad: capacidad)).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(CapacidadPrivilegio.SoporteLectura)]
    [InlineData(CapacidadPrivilegio.AdminPlataforma)]
    [InlineData(CapacidadPrivilegio.BreakGlass)]
    [InlineData(CapacidadPrivilegio.Impersonacion)]
    public void Rechaza_cualquier_otra_capacidad(CapacidadPrivilegio capacidad)
    {
        var validador = new ConcederPrivilegioCommandValidator();

        validador.Validate(Comando(capacidad: capacidad)).IsValid.Should().BeFalse(
            "la lista es cerrada: SoporteLectura se autoconcede y AdminPlataforma nace del acto fundacional");
    }

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

    [Fact]
    public void La_autoconcesion_acepta_el_tope_y_rechaza_un_dia_mas()
    {
        var validador = new AutoConcederPrivilegioCommandValidator();

        validador.Validate(new AutoConcederPrivilegioCommand(CapacidadPrivilegio.SoporteLectura, 90)).IsValid.Should().BeTrue();
        validador.Validate(new AutoConcederPrivilegioCommand(CapacidadPrivilegio.SoporteLectura, 91)).IsValid.Should().BeFalse();
    }
}
