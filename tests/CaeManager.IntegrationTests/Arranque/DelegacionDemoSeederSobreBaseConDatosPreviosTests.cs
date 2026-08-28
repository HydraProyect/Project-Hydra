using CaeManager.Application.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Seed;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.IntegrationTests.Arranque;

/// <summary>
/// Reproduce el incidente de producción del 2026-08-28: <c>DatosPrueba:Activo</c>
/// se enciende sobre una base que YA trae tenants de demo con datos parciales
/// (el volcado del 13-15 de agosto), no sobre una base virgen — el escenario que
/// <see cref="CuatroPropiedadesDelArranqueTests"/> y
/// <see cref="SeedersBajoRuntimeTests"/> cubren.
///
/// Pregunta que responde, medida y no asumida (así lo pidió el encargo): el
/// guard de "ya hay Clientes" de <see cref="DatosPruebaSeeder"/> —¿ve el tenant
/// correcto, o una consulta sin acotar que un tenant con datos previos
/// contamina a los demás?
/// </summary>
public class DelegacionDemoSeederSobreBaseConDatosPreviosTests
{
    /// <summary>
    /// Laboratorios Dexter ya existe con UN Cliente propio (simula el volcado),
    /// antes de que el seeder de hoy corra. Refrielectric todavía no existe —
    /// se crea hoy, tenant nuevo, sin ninguna fila.
    ///
    /// Si el guard de Dexter contaminara a Refrielectric (consulta sin acotar
    /// por tenant), Refrielectric terminaría sin cartera propia pese a ser un
    /// tenant recién creado — "pantallas vacías" para la demo. Si el guard está
    /// bien acotado, Dexter se queda tal cual estaba (no se resiembra encima de
    /// datos previos) y Refrielectric recibe su cartera completa.
    /// </summary>
    [Fact]
    public async Task Un_tenant_de_demo_con_datos_previos_no_bloquea_la_cartera_de_uno_nuevo()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: true);

        var tenantDexterPrevioId = await SembrarTenantDexterConUnClientePrevioAsync(arnes);

        using (var ambitoSiembraHoy = arnes.Servicios.CreateScope())
        {
            var sp = ambitoSiembraHoy.ServiceProvider;
            await DelegacionDemoSeeder.SeedAsync(
                sp.GetRequiredService<CaeManagerDbContext>(),
                sp.GetRequiredService<UserManager<ApplicationUser>>(),
                sp.GetRequiredService<IUserStore<ApplicationUser>>(),
                sp.GetRequiredService<IConfiguration>(),
                EntornoDePrueba.Desarrollo,
                NullLogger.Instance);
        }

        using var ambitoLectura = arnes.Servicios.CreateScope();
        var contexto = ambitoLectura.ServiceProvider.GetRequiredService<CaeManagerDbContext>();

        var tenantRefrielectricId = await contexto.Tenants
            .SingleAsync(t => t.Nombre == DelegacionDemoSeeder.NombreTenantRefrielectric);
        tenantRefrielectricId.Id.Should().NotBe(tenantDexterPrevioId,
            "Refrielectric tiene que ser un tenant nuevo, distinto del Dexter sembrado a mano arriba");

        int clientesRefrielectric;
        using (AmbitoTenantExplicito.Establecer(tenantRefrielectricId.Id))
        {
            clientesRefrielectric = await contexto.Empresas.CountAsync(e => e.EsCritico != null);
        }

        clientesRefrielectric.Should().BeGreaterThan(0,
            "MEDIDO: Refrielectric es un tenant nuevo — su cartera no debe verse bloqueada porque " +
            "Dexter ya tuviera Clientes antes de que el seeder de hoy corriera");

        int clientesDexter;
        using (AmbitoTenantExplicito.Establecer(tenantDexterPrevioId))
        {
            clientesDexter = await contexto.Empresas.CountAsync(e => e.EsCritico != null);
        }

        clientesDexter.Should().Be(1,
            "Dexter ya tenía datos: el guard de 'ya hay Clientes' debe respetarlos tal cual estaban, " +
            "sin resembrar encima ni duplicar");

        // El arreglo real del incidente: la marca explícita distingue
        // "completo de verdad" de "tiene algo suelto". Refrielectric, recién
        // sembrado a fondo hoy, queda marcado; Dexter, con datos previos que
        // esta pasada NO completó, se queda sin marcar — visible y accionable
        // en vez de indistinguible de "todo en orden".
        var refrielectricTrasSiembra = await contexto.Tenants.SingleAsync(t => t.Id == tenantRefrielectricId.Id);
        refrielectricTrasSiembra.DatosDemoCompletadosEnUtc.Should().NotBeNull(
            "MEDIDO: una siembra que termina sin cortarse debe marcar el tenant como completo");

        var dexterTrasSiembra = await contexto.Tenants.SingleAsync(t => t.Id == tenantDexterPrevioId);
        dexterTrasSiembra.DatosDemoCompletadosEnUtc.Should().BeNull(
            "MEDIDO: Dexter se omitió, no se completó — no debe quedar marcado como si lo estuviera");
    }

    /// <summary>
    /// El otro lado del arreglo: un tenant que SÍ completó su siembra no debe
    /// tocarse en un segundo arranque, aunque <c>DatosPrueba:Activo</c> siga
    /// en true — mismo comportamiento de siempre (idempotencia), ahora
    /// decidido por la marca explícita en vez de por "¿hay algún Cliente?".
    /// </summary>
    [Fact]
    public async Task Un_tenant_ya_marcado_como_completo_no_se_resiembra_en_un_segundo_arranque()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: true);

        async Task SembrarAsync()
        {
            using var ambito = arnes.Servicios.CreateScope();
            var sp = ambito.ServiceProvider;
            await DelegacionDemoSeeder.SeedAsync(
                sp.GetRequiredService<CaeManagerDbContext>(),
                sp.GetRequiredService<UserManager<ApplicationUser>>(),
                sp.GetRequiredService<IUserStore<ApplicationUser>>(),
                sp.GetRequiredService<IConfiguration>(),
                EntornoDePrueba.Desarrollo,
                NullLogger.Instance);
        }

        await SembrarAsync();

        Guid tenantRefrielectricId;
        int clientesAntes;
        DateTime? marcaAntes;
        using (var ambitoLectura = arnes.Servicios.CreateScope())
        {
            var contexto = ambitoLectura.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
            var tenant = await contexto.Tenants.SingleAsync(t => t.Nombre == DelegacionDemoSeeder.NombreTenantRefrielectric);
            tenantRefrielectricId = tenant.Id;
            marcaAntes = tenant.DatosDemoCompletadosEnUtc;
            marcaAntes.Should().NotBeNull();

            using (AmbitoTenantExplicito.Establecer(tenantRefrielectricId))
                clientesAntes = await contexto.Empresas.CountAsync();
        }

        await SembrarAsync();

        using var ambitoFinal = arnes.Servicios.CreateScope();
        var contextoFinal = ambitoFinal.ServiceProvider.GetRequiredService<CaeManagerDbContext>();

        var tenantTrasSegundaPasada = await contextoFinal.Tenants.SingleAsync(t => t.Id == tenantRefrielectricId);
        tenantTrasSegundaPasada.DatosDemoCompletadosEnUtc.Should().Be(marcaAntes,
            "MEDIDO: la segunda pasada ni siquiera reescribe la marca — el guard corta antes de tocar nada");

        int clientesDespues;
        using (AmbitoTenantExplicito.Establecer(tenantRefrielectricId))
            clientesDespues = await contextoFinal.Empresas.CountAsync();

        clientesDespues.Should().Be(clientesAntes, "MEDIDO: el segundo arranque no duplica ni una fila de cartera");
    }

    /// <summary>
    /// Inserta el tenant Dexter exactamente con el nombre que
    /// <see cref="DelegacionDemoSeeder.AprovisionarTenantAsync"/> busca, más UN
    /// Cliente (<c>EsCritico != null</c>) — la señal mínima que el guard de
    /// <see cref="DatosPruebaSeeder.SeedAsync"/> usa para "ya se sembró". Mismo
    /// patrón que el propio <c>AprovisionarTenantAsync</c>: tenant + parámetros +
    /// catálogo en el mismo ámbito, porque las políticas RLS lo exigen así.
    /// </summary>
    private static async Task<Guid> SembrarTenantDexterConUnClientePrevioAsync(ArnesDeArranqueRuntime arnes)
    {
        using var ambito = arnes.Servicios.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();

        var tenantDexter = new Tenant(DelegacionDemoSeeder.NombreTenantClienteDemo);

        using (AmbitoTenantExplicito.Establecer(tenantDexter.Id))
        {
            contexto.Tenants.Add(tenantDexter);
            contexto.ParametrosSistema.Add(new ParametroSistema(
                ParametroSistemaSeedData.UmbralAmbarDias, ParametroSistemaSeedData.UmbralRojoDias));
            contexto.TiposDocumento.AddRange(TipoDocumentoSeedData.CrearCopiasParaTenant());
            await contexto.SaveChangesAsync();

            contexto.Empresas.Add(Empresa.CrearComoCliente(
                "Cliente preexistente del volcado S.L.", cif: "B00000109",
                esCritico: true, notas: null, ejecutivoUsuarioId: null));
            await contexto.SaveChangesAsync();
        }

        return tenantDexter.Id;
    }
}
