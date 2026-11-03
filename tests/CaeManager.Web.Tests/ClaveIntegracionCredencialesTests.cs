using System.Text;
using CaeManager.Domain.ApiKeys;
using CaeManager.Infrastructure.Autenticacion;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>Las formas que entran por la cabecera y la vida del token, sin base ni host.</summary>
public class ClaveIntegracionCredencialesTests
{
    private static string Basic(string credencial) =>
        $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes(credencial))}";

    [Fact]
    public void Las_credenciales_llevan_el_Id_y_el_secreto_con_su_prefijo()
    {
        var claveId = Guid.NewGuid();
        var secreto = $"{ClaveIntegracionTenant.PrefijoSecreto}abc";

        var credenciales = CabeceraClaveIntegracion.ExtraerCredenciales(Basic($"{claveId}:{secreto}"));

        credenciales.Should().NotBeNull();
        credenciales!.ClaveId.Should().Be(claveId);
        credenciales.Secreto.Should().Be(secreto);
        credenciales.ToString().Should().NotContain(secreto, "el record acaba en los registros");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Basic")]
    [InlineData("Basic %%%")]
    [InlineData("Bearer abc")]
    public void Una_cabecera_que_no_es_Basic_valido_no_da_credenciales(string? cabecera) =>
        CabeceraClaveIntegracion.ExtraerCredenciales(cabecera).Should().BeNull();

    [Theory]
    [InlineData("no-es-guid:talveg_int_abc")]
    [InlineData("sin-dos-puntos")]
    public void Una_credencial_mal_formada_no_da_credenciales(string credencial) =>
        CabeceraClaveIntegracion.ExtraerCredenciales(Basic(credencial)).Should().BeNull();

    [Fact]
    public void Un_secreto_sin_el_prefijo_de_la_Clave_de_Integracion_no_da_credenciales() =>
        CabeceraClaveIntegracion.ExtraerCredenciales(Basic($"{Guid.NewGuid()}:hydra_clave-de-soporte")).Should().BeNull();

    [Theory]
    [InlineData("Bearer abc", "abc")]
    [InlineData("bearer   abc  ", "abc")]
    [InlineData("ApiKey abc", null)]
    [InlineData(null, null)]
    public void ExtraerToken_solo_mira_el_esquema_Bearer(string? cabecera, string? esperado) =>
        CabeceraClaveIntegracion.ExtraerToken(cabecera).Should().Be(esperado);

    [Fact]
    public void El_token_lleva_la_clave_y_el_Tenant_y_nada_mas()
    {
        var proteccion = new EphemeralDataProtectionProvider();
        var claveId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        TokenClaveIntegracion.Leer(proteccion, TokenClaveIntegracion.Proteger(proteccion, claveId, tenantId))
            .Should().Be(new CargaUtilTokenClaveIntegracion(claveId, tenantId));
        TokenClaveIntegracion.Vigencia.Should().Be(TimeSpan.FromMinutes(15));
    }

    [Theory]
    [InlineData("talveg_int_secreto-usado-como-bearer")]
    [InlineData("")]
    public void Lo_que_no_es_un_token_firmado_no_se_lee(string token) =>
        TokenClaveIntegracion.Leer(new EphemeralDataProtectionProvider(), token).Should().BeNull();

    [Fact]
    public void Un_token_de_otras_claves_de_Data_Protection_no_se_lee()
    {
        var ajeno = TokenClaveIntegracion.Proteger(new EphemeralDataProtectionProvider(), Guid.NewGuid(), Guid.NewGuid());

        TokenClaveIntegracion.Leer(new EphemeralDataProtectionProvider(), ajeno).Should().BeNull();
    }
}
