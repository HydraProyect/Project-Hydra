using CaeManager.Infrastructure.Persistence.Seed;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// La línea Outbound de gestión propia (P) de la siembra de Development, sin base de datos: las dos puertas que la dejan inerte y
/// la lista cerrada de Tenants que la reciben. Lo que escribe de verdad, bajo RLS y con el rol propietario, lo fija
/// <c>LineaOutboundGestionPropiaDemoSeederTests</c> de la suite de integración.
/// </summary>
public class LineaOutboundGestionPropiaDemoSeederTests
{
    private static IConfiguration Configuracion(bool datosPrueba) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DatosPrueba:Activo"] = datosPrueba ? "true" : "false"
        }).Build();

    private static IHostEnvironment Entorno(string nombre) => new HostingEnvironment { EnvironmentName = nombre };

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task Fuera_de_Development_no_hace_nada_ni_lanza_aunque_DatosPrueba_este_activo(string entorno)
    {
        // El contexto es null: tocarlo lanzaría NullReferenceException. DatosPrueba:Activo es legítimo en Staging, por eso aquí no lanza.
        var siembra = () => LineaOutboundGestionPropiaDemoSeeder.SeedAsync(
            dbContextBootstrap: null!, Configuracion(datosPrueba: true), Entorno(entorno), NullLogger.Instance);

        await siembra.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Sin_DatosPrueba_activo_no_hace_nada_ni_en_Development()
    {
        var siembra = () => LineaOutboundGestionPropiaDemoSeeder.SeedAsync(
            dbContextBootstrap: null!, Configuracion(datosPrueba: false), Entorno("Development"), NullLogger.Instance);

        await siembra.Should().NotThrowAsync();
    }

    [Fact]
    public async Task En_Development_con_DatosPrueba_activo_si_llega_a_leer_la_base()
    {
        // Control del instrumento: las dos pruebas de arriba pasan también si la guarda cerrara todas las puertas. Con las dos
        // condiciones cumplidas el seeder tiene que pasar de las guardas y usar el contexto, que aquí es null.
        var siembra = () => LineaOutboundGestionPropiaDemoSeeder.SeedAsync(
            dbContextBootstrap: null!, Configuracion(datosPrueba: true), Entorno("Development"), NullLogger.Instance);

        await siembra.Should().ThrowAsync<NullReferenceException>();
    }

    [Fact]
    public void Sin_ninguna_contradiccion_no_hay_motivo_para_omitir_el_Tenant()
    {
        // Control del instrumento: las cuatro pruebas de abajo pasarían también si la función devolviera siempre un motivo.
        LineaOutboundGestionPropiaDemoSeeder.MotivoParaOmitir(
            esPlataforma: false, puedeActuarComoOperadorCaeExterno: false, tieneDelegacionDeOperadorCaeExternoActiva: false,
            tieneAsignacionDeOperacionExterna: false).Should().BeNull();
    }

    [Theory]
    [InlineData(true, false, false, false, "plataforma")]
    [InlineData(false, true, false, false, "Operador CAE externo")]
    [InlineData(false, false, true, false, "delegación activa")]
    [InlineData(false, false, false, true, "Asignación de Operación")]
    public void Cada_guarda_de_gestion_propia_omite_el_Tenant_por_su_propio_motivo(
        bool esPlataforma, bool operadorExterno, bool delegacion, bool operacion, string fragmentoDelMotivo)
    {
        LineaOutboundGestionPropiaDemoSeeder.MotivoParaOmitir(esPlataforma, operadorExterno, delegacion, operacion)
            .Should().NotBeNull().And.Contain(fragmentoDelMotivo);
    }

    [Fact]
    public void Un_Tenant_que_incumple_varias_guardas_se_omite_igualmente()
    {
        LineaOutboundGestionPropiaDemoSeeder.MotivoParaOmitir(true, true, true, true).Should().NotBeNull();
    }

    [Fact]
    public void Solo_el_Tenant_Beta_de_la_cobertura_la_recibe()
    {
        LineaOutboundGestionPropiaDemoSeeder.NombresTenantsDeGestionPropia
            .Should().Equal(CoberturaPantallasSeeder.NombreTenantBeta);
    }

    [Fact]
    public void Ningun_Tenant_de_plataforma_Operador_CAE_externo_gestion_externa_ni_Inbound_esta_en_la_lista()
    {
        var excluidos = new Dictionary<string, IEnumerable<string>>
        {
            ["Tenant de plataforma"] = [TenantSeedData.NombrePorDefecto],
            ["DelegacionDemoSeeder (Operador CAE externo y Tenants propietarios con gestión externa)"] =
            [
                DelegacionDemoSeeder.NombreTenantConsultora, DelegacionDemoSeeder.NombreTenantRefrielectric,
                DelegacionDemoSeeder.NombreTenantClienteDemo, DelegacionDemoSeeder.NombreTenantClienteDemo2,
                DelegacionDemoSeeder.NombreTenantClienteDemo3,
            ],
            ["EscenariosDireccionDemoSeeder (ramas delegadas)"] = CatalogoEscenariosDireccionDemo.Ramas.Select(r => r.NombreTenant),
            ["SiembraDemoDireccionAdministrativa (lote delegado)"] = SiembraDemoDireccionAdministrativa.NombresTenantsDelLote,
            ["GestorCaeCarteraMultiTenantSeeder (Operador CAE externo y beneficiarios)"] = GestorCaeCarteraMultiTenantSeeder.NombresTenants,
            ["PilotoOutboundSeeder (Operador CAE externo y Tenants delegados)"] = CatalogoPilotoOutbound.NombresTenants,
            ["CoberturaPantallasSeeder (Operador CAE externo y Alfa, delegado)"] =
                [CoberturaPantallasSeeder.NombreTenantOperador, CoberturaPantallasSeeder.NombreTenantAlfa],
            ["InboundDemoSeeder (Tenants TCE y TEC)"] = InboundDemoSeeder.NombresTenants,
            // Sin marcador de datos de demo y sin Operador CAE externo ni tipo declarado: no se deduce que gestione su CAE, y no se siembra.
            ["SegundoTenantSeeder (verificación E2E)"] = [SegundoTenantSeeder.NombreSegundoTenant],
        };

        foreach (var (motivo, nombres) in excluidos)
        {
            LineaOutboundGestionPropiaDemoSeeder.NombresTenantsDeGestionPropia.Should().NotIntersectWith(
                nombres, $"los Tenants de {motivo} no gestionan su CAE con su propio personal");
        }
    }
}
