using CaeManager.Application.Comercial.Commands.RegistrarSuscripcionTenant;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Comercial.Commands;

/// <summary>
/// Hallazgo Codex del 2026-09-11: el mensaje de <c>RegistrarSuscripcionTenantCommand</c>
/// prometía que el Id "empieza por sub_", pero el validador solo aplicaba
/// <c>NotEmpty</c>/<c>MaximumLength</c> — cualquier texto no vacío pasaba. La comprobación
/// real (que <c>ObtenerSuscripcionAsync</c> falla si el Id no existe en Stripe) solo
/// ocurre después, gastando una llamada a la API por cada error tipográfico que el
/// prefijo habría descartado gratis.
/// </summary>
public class RegistrarSuscripcionTenantCommandValidatorTests
{
    private readonly RegistrarSuscripcionTenantCommandValidator _validator = new();

    [Fact]
    public void Acepta_un_id_con_el_prefijo_sub_()
    {
        var resultado = _validator.Validate(new RegistrarSuscripcionTenantCommand(Guid.NewGuid(), "sub_123"));

        resultado.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("cus_123")]
    [InlineData("123")]
    [InlineData("SUB_123")]
    public void Rechaza_un_id_sin_el_prefijo_sub_(string idSinPrefijo)
    {
        var resultado = _validator.Validate(new RegistrarSuscripcionTenantCommand(Guid.NewGuid(), idSinPrefijo));

        resultado.IsValid.Should().BeFalse();
        resultado.Errors.Should().Contain(e => e.PropertyName == nameof(RegistrarSuscripcionTenantCommand.StripeSubscriptionId));
    }

    [Fact]
    public void Un_id_vacio_falla_por_NotEmpty_sin_lanzar_por_el_chequeo_de_prefijo()
    {
        var resultado = _validator.Validate(new RegistrarSuscripcionTenantCommand(Guid.NewGuid(), ""));

        resultado.IsValid.Should().BeFalse();
    }
}
