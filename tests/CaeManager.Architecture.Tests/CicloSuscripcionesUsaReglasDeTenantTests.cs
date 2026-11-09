using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <c>RenovacionSuscripcionWebhookHostedService</c> decide qué Tenants propietarios verifican y renuevan, y cuáles solo pasan
/// por el barrido de la guarda transitoria, con dos métodos estáticos que Web.Tests prueba
/// (<c>RenovacionSuscripcionRecorridoTenantsTests</c>): <c>DelTenant</c> y <c>BarrerTenantNoActivoAsync</c>. El cableado
/// que los usa vive en métodos de instancia que necesitan ámbitos y base, sin test que los observe. Este trinquete vigila
/// que ese cableado siga pasando por ellos: volver a escribir <c>t.Estado == EstadoTenant.Activo</c> en línea, o pasar al
/// barrido una lambda que fije el valor de <c>tenantActivo</c>, lo pone en rojo (revisión puente de la ronda 11, 4b).
/// </summary>
public class CicloSuscripcionesUsaReglasDeTenantTests
{
    private static string Fuente() => File.ReadAllText(Path.Combine(
        RaizDelRepositorio(), "src", "CaeManager.Infrastructure", "Integraciones", "RenovacionSuscripcionWebhookHostedService.cs"));

    [Fact]
    public void La_lista_de_Tenants_del_ciclo_se_construye_con_DelTenant()
    {
        var fuente = Fuente();

        fuente.Should().Contain(".Select(t => DelTenant(t.Id, t.Estado))",
            "qué Tenant está activo solo se decide en DelTenant, que tiene su test");
        Regex.Matches(fuente, @"==\s*EstadoTenant\.Activo").Should().ContainSingle(
            "la única comparación con Activo es la de DelTenant");
    }

    [Fact]
    public void El_barrido_de_un_Tenant_no_activo_pasa_el_metodo_del_servicio_a_BarrerTenantNoActivoAsync()
    {
        var fuente = Fuente();

        Regex.IsMatch(fuente,
                @"await\s+BarrerTenantNoActivoAsync\(\s*ambito\.ServiceProvider\.GetRequiredService<CompletarIdentidadBuzonService>\(\)\.SacarTerminalesDeGuardaTransitoriaAsync\s*,")
            .Should().BeTrue("el barrido recibe el método, no una lambda, para que tenantActivo solo se fije en BarrerTenantNoActivoAsync");
        fuente.Should().NotContain("SacarTerminalesDeGuardaTransitoriaAsync(",
            "nadie en el servicio llama al barrido con un valor propio de tenantActivo");
    }

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        return actual?.FullName ?? throw new InvalidOperationException(
            "No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory +
            " — este test necesita el árbol fuente del repositorio.");
    }
}
