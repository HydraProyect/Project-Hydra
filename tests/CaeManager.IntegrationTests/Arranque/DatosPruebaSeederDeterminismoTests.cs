using CaeManager.Application.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Seed;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.IntegrationTests.Arranque;

/// <summary>
/// Auditoría previa: una siembra de demo "no determinista" cuenta una
/// historia distinta cada vez, y a veces ninguna. Cuatro defectos, todos en
/// <see cref="DatosPruebaSeeder"/>: (1) el centro "bloqueante garantizado" se
/// sorteaba entre TODOS los centros y podía caer en uno sin plantilla — sin
/// nadie a quien le falte el documento, no hay rojo que enseñar; (2a) un
/// <c>Guid.NewGuid()</c> fuera de la semilla determinista; (3) una empresa
/// que ningún cliente sorteó como contratista se quedaba sin centro, y su
/// plantilla invisible en cualquier pantalla Centro→Trabajador.
///
/// <para>
/// Este fichero MIDE, no asume: siembra DOS tenants independientes con la
/// misma semilla fija (<see cref="DatosPruebaSeeder.SembrarSoloDatosCompletosAsync"/>
/// usa <c>Random(20260803)</c> siempre) y comprueba que las dos garantías se
/// cumplen en AMBOS — la prueba de que la garantía no depende de dónde caiga
/// el sorteo, sino de una restricción real en el código.
/// </para>
/// </summary>
public class DatosPruebaSeederDeterminismoTests
{
    /// <summary>
    /// Smoke end-to-end a escala de producción (9 clientes / 24 empresas,
    /// seed 20260803 fijo) — <b>NO es la red de seguridad del defecto 1</b>.
    /// Con ese seed concreto, la propiedad se cumple incluso mutando el
    /// arreglo hacia atrás (comprobado a mano): el sorteo de esa tanda en
    /// particular no cae en un centro sin plantilla, así que este test
    /// pasaría igual con el bug de vuelta. La red de seguridad real es
    /// <see cref="SeleccionarCentrosConCanalTests"/>, que fuerza el
    /// escenario y barre 200 semillas — ese sí falsa de forma fiable. Este
    /// test se queda porque valida la integración completa (todos los
    /// seeders encadenados, RLS, Identity), no porque pruebe el defecto.
    /// </summary>
    [Fact]
    public async Task Dos_siembras_independientes_garantizan_un_centro_bloqueado_con_plantilla_real()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: true);

        var tenantAId = await CrearTenantConCatalogoAsync(arnes, "Tenant Determinismo A");
        var tenantBId = await CrearTenantConCatalogoAsync(arnes, "Tenant Determinismo B");

        await SembrarAsync(arnes, tenantAId);
        await SembrarAsync(arnes, tenantBId);

        foreach (var tenantId in new[] { tenantAId, tenantBId })
        {
            using var ambito = arnes.Servicios.CreateScope();
            var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();

            using (AmbitoTenantExplicito.Establecer(tenantId))
            {
                var centroBloqueanteId = await contexto.TiposDocumentoCentros
                    .Where(t => t.BloqueaAcceso)
                    .Select(t => t.CentroId)
                    .SingleOrDefaultAsync();

                centroBloqueanteId.Should().NotBe(Guid.Empty,
                    $"MEDIDO tenant {tenantId}: la siembra completa (>=3 centros con canal) debe producir " +
                    "exactamente un centro con requisito bloqueante");

                var trabajadoresEnElCentro = await contexto.Asignaciones
                    .CountAsync(a => a.CentroId == centroBloqueanteId);

                trabajadoresEnElCentro.Should().BeGreaterThan(0,
                    $"MEDIDO tenant {tenantId}: el centro bloqueante tiene que tener plantilla real — si no, " +
                    "el requisito se graba pero no hay a quién le falte el documento, y el centro no sale rojo");
            }
        }
    }

    /// <summary>
    /// A escala de producción (9 clientes / 24 empresas, seed 20260803) el
    /// hueco es posible pero no garantizado — depende de la suerte del
    /// sorteo. Esta prueba lo fuerza matemáticamente: 2 clientes que sortean
    /// 5-10 empresas cada uno CADA UNO, contra un pool de 30, cubren como
    /// mucho 20 — quedan al menos 10 sin cliente pase lo que pase con el
    /// dado, así que la prueba no depende de qué seed le toque a
    /// <see cref="Random"/> ni puede dar un falso verde por casualidad.
    /// </summary>
    [Fact]
    public async Task Dos_siembras_con_pool_de_empresas_mayor_que_lo_que_los_clientes_pueden_cubrir_no_dejan_ninguna_sin_centro()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: true);

        var tenantAId = await CrearTenantConCatalogoAsync(arnes, "Tenant Determinismo C");
        var tenantBId = await CrearTenantConCatalogoAsync(arnes, "Tenant Determinismo D");

        // Semillas DISTINTAS entre sí (y de la de producción) — la garantía
        // tiene que sostenerse pase lo que pase con el dado, no solo con el
        // seed fijo que usa SembrarSoloDatosCompletosAsync.
        await SembrarConHuecoForzadoAsync(arnes, tenantAId, new Random(1));
        await SembrarConHuecoForzadoAsync(arnes, tenantBId, new Random(2));

        foreach (var tenantId in new[] { tenantAId, tenantBId })
        {
            using var ambito = arnes.Servicios.CreateScope();
            var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();

            using (AmbitoTenantExplicito.Establecer(tenantId))
            {
                var empresaIds = await contexto.Empresas
                    .Where(e => e.EsCritico == null && e.NivelServicio == null)
                    .Select(e => e.Id)
                    .ToListAsync();
                empresaIds.Should().HaveCount(30, "el escenario forzado siembra exactamente 30 empresas");

                var empresasConCentro = await contexto.Centros
                    .Select(c => c.EmpresaId)
                    .Distinct()
                    .ToListAsync();

                var empresasSinCentro = empresaIds.Except(empresasConCentro).ToList();

                empresasSinCentro.Should().BeEmpty(
                    $"MEDIDO tenant {tenantId}: con 2 clientes y 30 empresas, el sorteo deja matemáticamente " +
                    "huecos sin repesca — toda empresa contratista sembrada debe operar al menos un centro, " +
                    "o su plantilla queda invisible en cualquier pantalla Centro→Trabajador");
            }
        }
    }

    [Fact]
    public async Task El_verificador_de_las_verificaciones_externas_es_fijo_entre_siembras()
    {
        // No Guid.NewGuid() por tanda: el mismo valor fijo en cualquier
        // siembra, para que dos siembras produzcan el mismo dato.
        var verificadorEsperado = Guid.Parse("00000000-0000-0000-0000-000000000001");

        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: true);
        var tenantId = await CrearTenantConCatalogoAsync(arnes, "Tenant Determinismo E");
        await SembrarAsync(arnes, tenantId);

        using var ambito = arnes.Servicios.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();

        using (AmbitoTenantExplicito.Establecer(tenantId))
        {
            var verificadores = await contexto.VerificacionesExternaSubcontrata
                .Select(v => v.UsuarioVerificadorId)
                .Distinct()
                .ToListAsync();

            verificadores.Should().NotBeEmpty("MEDIDO: la siembra completa produce verificaciones externas");
            verificadores.Should().AllSatisfy(id => id.Should().Be(verificadorEsperado),
                "MEDIDO: antes de este arreglo era Guid.NewGuid() por tanda — distinto en cada siembra");
        }
    }

    private static async Task SembrarConHuecoForzadoAsync(ArnesDeArranqueRuntime arnes, Guid tenantId, Random aleatorio)
    {
        using var ambito = arnes.Servicios.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        using (AmbitoTenantExplicito.Establecer(tenantId))
        {
            await DatosPruebaSeeder.SembrarDatosOperativosAsync(
                contexto, aleatorio, numeroClientes: 2, numeroEmpresas: 30, numeroSubcontratas: 0, CancellationToken.None);
        }
    }

    private static async Task SembrarAsync(ArnesDeArranqueRuntime arnes, Guid tenantId)
    {
        using var ambito = arnes.Servicios.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        using (AmbitoTenantExplicito.Establecer(tenantId))
        {
            var resumen = await DatosPruebaSeeder.SembrarSoloDatosCompletosAsync(contexto, NullLogger.Instance);
            resumen.Should().NotBeNull("el tenant es nuevo, recién creado con catálogo y sin Clientes");
        }
    }

    private static async Task<Guid> CrearTenantConCatalogoAsync(ArnesDeArranqueRuntime arnes, string nombreTenant)
    {
        using var ambito = arnes.Servicios.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();

        var tenant = new Tenant(nombreTenant);
        using (AmbitoTenantExplicito.Establecer(tenant.Id))
        {
            contexto.Tenants.Add(tenant);
            contexto.ParametrosSistema.Add(new ParametroSistema(
                ParametroSistemaSeedData.UmbralAmbarDias, ParametroSistemaSeedData.UmbralRojoDias));
            contexto.TiposDocumento.AddRange(TipoDocumentoSeedData.CrearCopiasParaTenant());
            await contexto.SaveChangesAsync();
        }

        return tenant.Id;
    }
}
