using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Domain.Centros;
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
/// <b>El semáforo de /centros de la demo no puede salir monocromo.</b>
///
/// <para>
/// La siembra de demo repartía los estados de <i>Documento</i> a propósito
/// ("mayoría vigente, con próximos, urgentes y vencidos", ver
/// <see cref="DatosPruebaSeeder"/>), pero nadie había medido el estado del
/// <i>Centro</i>, que es lo que se ve en pantalla. Y ahí el reparto no
/// sobrevive a dos cosas que se combinan:
/// </para>
///
/// <list type="number">
///   <item><description>
///     <c>DocumentacionEstandarTrabajador</c> no incluía dos TipoDocumento
///     marcados <c>EsObligatorio</c> ("Información Art. 18" y "DNI o NIE en
///     vigor"), así que <b>a todo trabajador sembrado le faltaban los dos</b>.
///   </description></item>
///   <item><description>
///     <see cref="CalculadoraEstadoCentro"/> evalúa <c>Faltante</c> antes que
///     <c>Vencido/Urgente/Proximo</c>, así que ese hueco universal tapaba
///     cualquier otro estado.
///   </description></item>
/// </list>
///
/// <para>
/// Y hay un tercer factor, independiente de los dos anteriores: el estado de
/// un Centro es el <b>peor caso</b> de decenas de documentos, así que un
/// sorteo por documento —por muy "mayoría vigente" que fuera— no deja
/// prácticamente ningún centro en verde. De ahí el
/// <c>PerfilCumplimiento</c> por empresa.
/// </para>
///
/// <para>
/// <b>Medido</b> sobre <c>SembrarSoloDatosCompletosAsync</c> (41 centros con
/// plantilla):
/// </para>
/// <list type="bullet">
///   <item><description>
///     Antes: <c>Faltante=40, Bloqueado=1</c> — un solo color.
///   </description></item>
///   <item><description>
///     Después: <c>Vigente=18, Proximo=7, Urgente=4, Vencido=9, Faltante=2,
///     Bloqueado=1</c> — los seis estados representados.
///   </description></item>
/// </list>
///
/// <para>
/// Este test MIDE el reparto real de <see cref="EstadoCentro"/> sobre la
/// siembra de producción de la demo (<see
/// cref="DatosPruebaSeeder.SembrarSoloDatosCompletosAsync"/>, semilla fija) con
/// el <b>mismo servicio que pinta la pantalla</b>
/// (<see cref="CalculoEstadoCentroService"/>) — no con una réplica de su lógica.
/// </para>
/// </summary>
public class SemaforoDeCentrosDemoTests
{
    /// <summary>
    /// Los estados "de vigencia": los que solo pueden verse si el hueco
    /// universal de documentación obligatoria ya no existe.
    /// </summary>
    private static readonly EstadoCentro[] EstadosDeVigencia =
        [EstadoCentro.Vigente, EstadoCentro.Proximo, EstadoCentro.Urgente, EstadoCentro.Vencido];

    [Fact]
    public async Task La_siembra_de_demo_reparte_los_centros_en_varios_estados_de_semaforo()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: true);
        var tenantId = await CrearTenantConCatalogoAsync(arnes, "Tenant Semaforo Demo A");
        await SembrarAsync(arnes, tenantId);

        var reparto = await MedirRepartoAsync(arnes, tenantId);
        var detalle = Describir(reparto);

        reparto.Values.Sum().Should().BeGreaterThan(0,
            "MEDIDO: la siembra de demo tiene que producir centros con plantilla activa");

        reparto.Keys.Should().Contain(EstadosDeVigencia,
            $"MEDIDO ({detalle}): los cuatro estados de vigencia tienen que estar representados — " +
            "si ninguno aparece es que un hueco de documentación obligatoria los está tapando a todos " +
            "(Faltante se evalúa antes que Vencido en CalculadoraEstadoCentro)");

        reparto.Keys.Should().Contain(EstadoCentro.Faltante,
            $"MEDIDO ({detalle}): el guion de la demo enseña también huecos documentales reales");

        reparto.Keys.Should().Contain(EstadoCentro.Bloqueado,
            $"MEDIDO ({detalle}): la siembra garantiza un centro con requisito bloqueante sin cumplir " +
            "(ver DatosPruebaSeederDeterminismoTests) — ese centro tiene que llegar a EstadoCentro.Bloqueado, " +
            "no quedarse en el papel");

        var total = reparto.Values.Sum();
        var mayoritario = reparto.MaxBy(par => par.Value);
        (mayoritario.Value * 100.0 / total).Should().BeLessThan(90,
            $"MEDIDO ({detalle}): ningún estado puede acaparar el semáforo — con más del 90 % en un solo " +
            "color la pantalla vuelve a ser monocroma aunque técnicamente haya variedad");
    }

    /// <summary>
    /// La misma medida sobre un segundo tenant sembrado de forma independiente:
    /// el reparto tiene que ser una propiedad de la siembra, no de la suerte de
    /// una tanda concreta (mismo criterio que
    /// <see cref="DatosPruebaSeederDeterminismoTests"/>).
    /// </summary>
    [Fact]
    public async Task Dos_siembras_independientes_reparten_igual_el_semaforo()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: true);

        var tenantAId = await CrearTenantConCatalogoAsync(arnes, "Tenant Semaforo Demo B");
        var tenantBId = await CrearTenantConCatalogoAsync(arnes, "Tenant Semaforo Demo C");
        await SembrarAsync(arnes, tenantAId);
        await SembrarAsync(arnes, tenantBId);

        var repartoA = await MedirRepartoAsync(arnes, tenantAId);
        var repartoB = await MedirRepartoAsync(arnes, tenantBId);

        Describir(repartoB).Should().Be(Describir(repartoA),
            "MEDIDO: la semilla de SembrarSoloDatosCompletosAsync es fija (20260803), así que dos " +
            "siembras independientes tienen que producir exactamente el mismo reparto de semáforo");
    }

    private static async Task<IReadOnlyDictionary<EstadoCentro, int>> MedirRepartoAsync(
        ArnesDeArranqueRuntime arnes, Guid tenantId)
    {
        using var ambito = arnes.Servicios.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();

        using (AmbitoTenantExplicito.Establecer(tenantId))
        {
            var centroIdsConPlantilla = await contexto.Asignaciones
                .Where(a => a.FechaBaja == null)
                .Select(a => a.CentroId)
                .Distinct()
                .ToListAsync();

            // El servicio real, con el DbContext haciendo de los seis contextos
            // de consulta que implementa — el mismo cálculo que alimenta el
            // badge de /centros y el Centro 360, no una réplica.
            var servicio = new CalculoEstadoCentroService(
                contexto, contexto, contexto, contexto, contexto, contexto);

            var resultado = await servicio.CalcularAsync(centroIdsConPlantilla, CancellationToken.None);

            return resultado.Values
                .GroupBy(r => r.Estado)
                .ToDictionary(g => g.Key, g => g.Count());
        }
    }

    private static string Describir(IReadOnlyDictionary<EstadoCentro, int> reparto) =>
        string.Join(", ", reparto.OrderBy(par => par.Key).Select(par => $"{par.Key}={par.Value}"));

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
