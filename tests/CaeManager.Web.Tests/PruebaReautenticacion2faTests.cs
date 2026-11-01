using CaeManager.Web.Components.Account;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;

namespace CaeManager.Web.Tests;

/// <summary>
/// La prueba de reautenticación del alta del TOTP (ASVS 4.0.3 V2.5 y V3.7.1) solo vale para la
/// cuenta y la sesión que la recibieron, y mientras no caduque.
/// </summary>
public class PruebaReautenticacion2faTests
{
    private readonly IDataProtectionProvider _proveedor = new EphemeralDataProtectionProvider();
    private static readonly Guid Usuario = Guid.NewGuid();
    private static readonly Guid Sid = Guid.NewGuid();

    private string EmitirAhora(Guid? sid) => PruebaReautenticacion2fa.Emitir(_proveedor, Usuario, sid, DateTimeOffset.UtcNow);

    [Fact]
    public void Vale_para_la_misma_cuenta_y_la_misma_sesion()
    {
        PruebaReautenticacion2fa.EsValida(_proveedor, EmitirAhora(Sid), Usuario, Sid).Should().BeTrue();
    }

    [Fact]
    public void Una_sesion_sin_sid_solo_vale_sin_sid()
    {
        var prueba = EmitirAhora(null);

        PruebaReautenticacion2fa.EsValida(_proveedor, prueba, Usuario, null).Should().BeTrue();
        PruebaReautenticacion2fa.EsValida(_proveedor, prueba, Usuario, Sid).Should().BeFalse();
    }

    [Fact]
    public void No_vale_para_otra_cuenta()
    {
        PruebaReautenticacion2fa.EsValida(_proveedor, EmitirAhora(Sid), Guid.NewGuid(), Sid).Should().BeFalse();
    }

    [Fact]
    public void No_vale_para_otra_sesion_de_la_misma_cuenta()
    {
        PruebaReautenticacion2fa.EsValida(_proveedor, EmitirAhora(Sid), Usuario, Guid.NewGuid()).Should().BeFalse();
        PruebaReautenticacion2fa.EsValida(_proveedor, EmitirAhora(Sid), Usuario, null).Should().BeFalse();
    }

    [Fact]
    public void Caduca_a_los_quince_minutos()
    {
        PruebaReautenticacion2fa.Vigencia.Should().Be(TimeSpan.FromMinutes(15));

        var casiCaducada = PruebaReautenticacion2fa.Emitir(
            _proveedor, Usuario, Sid, DateTimeOffset.UtcNow - PruebaReautenticacion2fa.Vigencia + TimeSpan.FromMinutes(1));
        var caducada = PruebaReautenticacion2fa.Emitir(
            _proveedor, Usuario, Sid, DateTimeOffset.UtcNow - PruebaReautenticacion2fa.Vigencia - TimeSpan.FromSeconds(1));

        PruebaReautenticacion2fa.EsValida(_proveedor, casiCaducada, Usuario, Sid).Should().BeTrue();
        PruebaReautenticacion2fa.EsValida(_proveedor, caducada, Usuario, Sid).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no-es-una-prueba")]
    public void Una_prueba_vacia_o_ilegible_no_vale(string? prueba)
    {
        PruebaReautenticacion2fa.EsValida(_proveedor, prueba, Usuario, Sid).Should().BeFalse();
    }

    [Fact]
    public void Una_prueba_manipulada_no_vale()
    {
        var prueba = EmitirAhora(Sid).ToCharArray();
        var medio = prueba.Length / 2;
        prueba[medio] = prueba[medio] == 'A' ? 'B' : 'A';

        PruebaReautenticacion2fa.EsValida(_proveedor, new string(prueba), Usuario, Sid).Should().BeFalse();
    }

    [Fact]
    public void Una_prueba_de_otro_almacen_de_claves_no_vale()
    {
        var ajena = PruebaReautenticacion2fa.Emitir(new EphemeralDataProtectionProvider(), Usuario, Sid, DateTimeOffset.UtcNow);

        PruebaReautenticacion2fa.EsValida(_proveedor, ajena, Usuario, Sid).Should().BeFalse();
    }

    [Fact]
    public void Una_carga_protegida_con_otro_proposito_no_vale()
    {
        var otroProposito = _proveedor.CreateProtector("otro").ToTimeLimitedDataProtector()
            .Protect($"{Usuario:N}|{Sid:N}", TimeSpan.FromMinutes(15));

        PruebaReautenticacion2fa.EsValida(_proveedor, otroProposito, Usuario, Sid).Should().BeFalse();
    }
}
