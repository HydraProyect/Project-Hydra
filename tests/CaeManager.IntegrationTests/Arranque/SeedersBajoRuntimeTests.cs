using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Seed;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.IntegrationTests.Arranque;

/// <summary>
/// Los seeders tenant-scoped, ejecutados bajo la identidad runtime real.
///
/// <para>
/// <b>Lo que este fichero demuestra hoy, y solo eso</b>: que la cadena de datos
/// de demo —con sus tres seeders internos— se ejecuta completa bajo
/// <c>cae_app_runtime</c> con RLS efectiva <b>sin violar ninguna política</b>.
/// </para>
///
/// <para>
/// <b>Lo que NO demuestra todavía</b>: que el seeder sea correcto. No comprueba
/// que haya escrito en el tenant esperado, que no haya escrito en otro, ni que
/// haya producido las entidades que promete. "No lanzó excepción" es evidencia
/// de compatibilidad con RLS, no de comportamiento. Las cuatro propiedades
/// —identidad, ámbito, exclusión y resultado— llegan en un incremento aparte.
/// </para>
/// </summary>
public class SeedersBajoRuntimeTests
{
    /// <summary>
    /// La cadena completa de datos de demo, sin excepción. Es la pregunta que
    /// abría el bloque: ¿puede el arranque tenant-scoped correr bajo la identidad
    /// restringida? Sí.
    /// </summary>
    [Fact]
    public async Task DelegacionDemoSeeder_se_ejecuta_completo_sin_violar_ninguna_politica()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: true);
        using var ambito = arnes.Servicios.CreateScope();
        var sp = ambito.ServiceProvider;

        var ejecutar = async () => await DelegacionDemoSeeder.SeedAsync(
            sp.GetRequiredService<CaeManagerDbContext>(),
            sp.GetRequiredService<UserManager<ApplicationUser>>(),
            sp.GetRequiredService<IUserStore<ApplicationUser>>(),
            sp.GetRequiredService<IConfiguration>(),
            EntornoDePrueba.Desarrollo,
            NullLogger.Instance);

        await ejecutar.Should().NotThrowAsync();
    }

    /// <summary>
    /// <b>La escritura tenantizada que al arnés le faltaba por probar.</b>
    ///
    /// <para>
    /// Nació como bisección de un fallo y se queda como regresión, porque cubre
    /// el hueco exacto que dejó #260: allí se validó identidad, conexión y
    /// <b>lectura</b> bajo RLS, y ninguna de las tres capas escribía una fila con
    /// tenant. El arnés montaba uno de los cuatro interceptores de producción, y
    /// sin <c>TenantSelladoInterceptor</c> las filas salían sin <c>TenantId</c> y
    /// morían con <c>42501</c> contra su propia política —un síntoma idéntico al
    /// de un defecto de RLS, con la causa dos capas más arriba—.
    /// </para>
    ///
    /// <para>
    /// Crear el tenant y una fila suya en el <b>mismo</b> <c>SaveChanges</c> es el
    /// patrón que usan los seeders, así que es el que hay que vigilar.
    /// </para>
    /// </summary>
    [Fact]
    public async Task El_arnes_puede_crear_un_tenant_y_una_fila_suya_en_el_mismo_SaveChanges()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false);
        using var ambito = arnes.Servicios.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();

        var tenant = new CaeManager.Domain.Tenants.Tenant("Tenant del repro");

        using (CaeManager.Application.Common.AmbitoTenantExplicito.Establecer(tenant.Id))
        {
            await contexto.Database.OpenConnectionAsync();
            var conexion = contexto.Database.GetDbConnection();
            await using (var sonda = conexion.CreateCommand())
            {
                sonda.CommandText = "SELECT current_setting('app.tenant_id', true), current_user;";
                await using var lector = await sonda.ExecuteReaderAsync();
                await lector.ReadAsync();
                Console.WriteLine($"SONDA app.tenant_id=[{lector.GetValue(0)}] current_user=[{lector.GetValue(1)}] esperado=[{tenant.Id}]");
            }

            contexto.Tenants.Add(tenant);
            contexto.ParametrosSistema.Add(new CaeManager.Domain.Configuracion.ParametroSistema(
                ParametroSistemaSeedData.UmbralAmbarDias, ParametroSistemaSeedData.UmbralRojoDias));

            var guardar = async () => await contexto.SaveChangesAsync();
            await guardar.Should().NotThrowAsync("el ambito y el sellado apuntan al mismo tenant");
        }
    }
}
