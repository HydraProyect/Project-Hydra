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
/// que haya escrito en el tenant esperado ni que no haya escrito en otro.
/// "No lanzó excepción" es evidencia de compatibilidad con RLS, no de
/// comportamiento. De las cuatro propiedades —identidad, ámbito, exclusión y
/// resultado— aquí solo está cubierta una parte del <b>resultado</b>: las dos
/// cuentas con las que se presenta el guion de la demo (ver
/// <see cref="DelegacionDemoSeeder_crea_las_cuentas_de_direccion_cae_y_de_gestor_del_guion"/>).
/// Las demás llegan en un incremento aparte.
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
    /// <b>Los dos usuarios que el guion de la demo necesita, medidos contra la
    /// base — no leídos de la documentación.</b>
    ///
    /// <para>
    /// El guion se presenta con una cuenta de <b>Dirección CAE</b> (visión
    /// completa del negocio) y otra de <b>nivel gestor</b> (cartera acotada,
    /// <c>IAlcanceDatosService</c>). Que existan estaba dado por hecho: el
    /// fichero de al lado solo comprobaba que el seeder no lanzara, y "no lanzó
    /// excepción" no es "creó las cuentas". Un fallo silencioso de
    /// <c>UserManager.CreateAsync</c> se registra como <c>LogWarning</c> y la
    /// siembra continúa (ver <c>DelegacionDemoSeeder.SembrarUsuariosRefrielectricAsync</c>),
    /// así que el modo de fallo real es exactamente "todo verde y sin usuarios".
    /// </para>
    ///
    /// <para>
    /// Se comprueban sobre <b>Refrielectric</b>, el tenant que la siembra usa
    /// como referencia principal de "empresa final" — no sobre el tenant de
    /// plataforma ni sobre la Consultora.
    /// </para>
    /// </summary>
    [Fact]
    public async Task DelegacionDemoSeeder_crea_las_cuentas_de_direccion_cae_y_de_gestor_del_guion()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: true);
        using var ambito = arnes.Servicios.CreateScope();
        var sp = ambito.ServiceProvider;
        var gestorUsuarios = sp.GetRequiredService<UserManager<ApplicationUser>>();

        await DelegacionDemoSeeder.SeedAsync(
            sp.GetRequiredService<CaeManagerDbContext>(),
            gestorUsuarios,
            sp.GetRequiredService<IUserStore<ApplicationUser>>(),
            sp.GetRequiredService<IConfiguration>(),
            EntornoDePrueba.Desarrollo,
            NullLogger.Instance);

        var esperados = new (string Email, string Rol)[]
        {
            ($"{DelegacionDemoSeeder.PrefijoEmailRefrielectric}direccioncae1@caemanager.local", Roles.DireccionCae),
            ($"{DelegacionDemoSeeder.PrefijoEmailRefrielectric}gestorcae1@caemanager.local", Roles.GestorCae)
        };

        foreach (var (email, rol) in esperados)
        {
            var usuario = await gestorUsuarios.FindByEmailAsync(email);

            usuario.Should().NotBeNull(
                $"MEDIDO: el guion de la demo entra con {email} — si el seeder no lo creó, " +
                "la sesión de demo no tiene con qué empezar");

            (await gestorUsuarios.IsInRoleAsync(usuario!, rol)).Should().BeTrue(
                $"MEDIDO: {email} tiene que llevar el rol {rol} — sin rol aterriza en " +
                "/cuenta/pendiente-de-rol, que no es ninguna de las pantallas del guion");

            usuario!.DebeCambiarContrasena.Should().BeFalse(
                $"MEDIDO: {email} no puede toparse con el cambio de contraseña forzado al abrir la demo");
        }
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
