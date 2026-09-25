using CaeManager.Application.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Seed;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Arranque;

/// <summary>
/// P0-1 (MATURITY_REVIEW_2026-09-24) y decisión D-5: fuera de Development,
/// ninguna cuenta sembrada —el Administrador inicial ni las cuentas de demo—
/// puede nacer con la clave TOTP fija, que es pública en el código fuente.
/// Nace sin segundo factor y, si es Administrador, da de alta su propio
/// autenticador en el primer acceso (MainLayout lo lleva a /cuenta/configurar-2fa).
///
/// <para>
/// Se ejecuta <see cref="IdentitySeeder.SeedAsync"/> real contra PostgreSQL,
/// con el mismo contexto de bootstrap y el mismo ámbito de tenant que usa
/// Program.cs, y se lee el resultado de vuelta por UserManager. El caso de
/// Development es el control positivo: demuestra que el instrumento ve la
/// clave cuando se asigna, así que su ausencia en Production no es un vacío
/// del arnés.
/// </para>
/// </summary>
public class SegundoFactorDeLaSiembraTests
{
    private const string EmailConfigurado = "admin-p01@talveg.test";

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task Fuera_de_Development_el_Administrador_inicial_nace_sin_la_clave_TOTP_publica(string entorno)
    {
        var (existe, esAdministrador, dobleFactor, clave) = await SembrarYLeerAsync(new EntornoDePrueba(entorno));

        existe.Should().BeTrue("el seeder tiene que haber creado al Administrador inicial para que el resto signifique algo");
        esAdministrador.Should().BeTrue();
        clave.Should().NotBe(IdentitySeeder.ClaveTotpAdministradorInicial,
            "la clave fija es pública en el repositorio: en {0} sería un segundo factor conocido por cualquiera", entorno);
        clave.Should().BeNull("no se asigna ninguna clave: la da de alta el propio Administrador en su primer acceso");
        dobleFactor.Should().BeFalse(
            "con 2FA activo y sin clave nadie podría entrar; sin él, MainLayout exige el alta en el primer acceso");
    }

    [Fact]
    public async Task En_Development_el_Administrador_inicial_nace_con_la_clave_TOTP_fija_de_los_E2E()
    {
        var (existe, esAdministrador, dobleFactor, clave) = await SembrarYLeerAsync(EntornoDePrueba.Desarrollo);

        existe.Should().BeTrue();
        esAdministrador.Should().BeTrue();
        dobleFactor.Should().BeTrue();
        clave.Should().Be(IdentitySeeder.ClaveTotpAdministradorInicial,
            "el arnés E2E arranca en Development y calcula el código TOTP con esta clave (Ayudas.ClaveTotpAdministrador)");
    }

    /// <summary>
    /// Decisión D-5 (2026-09-24): la regla se extiende a las cuentas con 2FA de
    /// la siembra de demo (DelegacionDemoSeeder, que además invoca
    /// DatosPruebaSeeder, y SegundoTenantSeeder), porque staging arranca como
    /// Production con DatosPrueba:Activo. Se cuenta sobre la base entera, como
    /// propietario y sin RLS, para que ninguna cuenta de ningún Tenant quede
    /// fuera de la medición; Development es el control positivo del recuento.
    /// </summary>
    [Theory]
    [InlineData("Production", false)]
    [InlineData("Development", true)]
    public async Task La_siembra_de_demo_solo_da_la_clave_TOTP_publica_en_Development(string entorno, bool esperaClaveFija)
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: true, segundoTenantActivo: true);

        // En Production las credenciales de demo no tienen valor por defecto
        // (CredencialesDemo): se configuran explícitamente, con cualquier valor.
        var configuracion = new ConfigurationBuilder()
            .AddConfiguration(arnes.Servicios.GetRequiredService<IConfiguration>())
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DatosPrueba:Contrasena"] = CredencialesDemo.ContrasenaPorDefecto,
                ["DatosPrueba:ClaveApiActiva"] = CredencialesDemo.ClaveApiActivaPorDefecto,
                ["DatosPrueba:ClaveApiRevocada"] = CredencialesDemo.ClaveApiRevocadaPorDefecto,
                [SegundoTenantSeeder.ClaveContrasenaConfiguracion] = SegundoTenantSeeder.ContrasenaAdministradorSegundoTenant,
            })
            .Build();
        var host = new EntornoDePrueba(entorno);

        using (var ambito = arnes.Servicios.CreateScope())
        {
            var sp = ambito.ServiceProvider;
            await DelegacionDemoSeeder.SeedAsync(
                sp.GetRequiredService<CaeManagerDbContext>(),
                sp.GetRequiredService<UserManager<ApplicationUser>>(),
                sp.GetRequiredService<IUserStore<ApplicationUser>>(),
                configuracion, host, NullLogger.Instance);
            await SegundoTenantSeeder.SeedAsync(
                sp.GetRequiredService<CaeManagerDbContext>(),
                sp.GetRequiredService<UserManager<ApplicationUser>>(),
                sp.GetRequiredService<IUserStore<ApplicationUser>>(),
                configuracion, host, NullLogger.Instance);
        }

        await using var conexion = new NpgsqlConnection(arnes.CadenaPropietario);
        await conexion.OpenAsync();

        var usuarios = await ContarAsync(conexion, """SELECT count(*) FROM "AspNetUsers" """);
        var conDobleFactor = await ContarAsync(conexion, """SELECT count(*) FROM "AspNetUsers" WHERE "TwoFactorEnabled" """);
        var conClaveFija = await ContarAsync(conexion,
            $"""SELECT count(*) FROM "AspNetUserTokens" WHERE "Value" = '{IdentitySeeder.ClaveTotpAdministradorInicial}'""");

        usuarios.Should().BeGreaterThan(10, "la siembra de demo tiene que haber creado sus cuentas para que el recuento signifique algo");

        if (esperaClaveFija)
        {
            // Control positivo: el recuento ve la clave cuando se asigna. Los
            // Administradores de ArcoSPA, Refrielectric, demo 2, las tres cuentas
            // de DatosPruebaSeeder, prueba.con2fa1 y el del segundo Tenant.
            conDobleFactor.Should().BeGreaterThanOrEqualTo(8);
            conClaveFija.Should().Be(conDobleFactor, "en Development toda cuenta sembrada con 2FA lleva la clave de los E2E");
        }
        else
        {
            conClaveFija.Should().Be(0, "fuera de Development ninguna cuenta sembrada puede llevar la clave TOTP pública");
            conDobleFactor.Should().Be(0, "sin clave, 2FA activo dejaría la cuenta sin acceso: da de alta la suya en el primer acceso");
        }
    }

    private static async Task<long> ContarAsync(NpgsqlConnection conexion, string sql)
    {
        await using var comando = new NpgsqlCommand(sql, conexion);
        return (long)(await comando.ExecuteScalarAsync())!;
    }

    private static async Task<(bool Existe, bool EsAdministrador, bool DobleFactor, string? Clave)> SembrarYLeerAsync(
        EntornoDePrueba entorno)
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false);

        // Fuera de Development el seeder exige email y contraseña configurados
        // (P0-2); se pasan también en Development para leer la misma cuenta.
        var configuracion = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AdministradorInicial:Email"] = EmailConfigurado,
                ["AdministradorInicial:Contrasena"] = "Produccion#2026x",
            })
            .Build();

        using (var ambito = arnes.Servicios.CreateScope())
        {
            var sp = ambito.ServiceProvider;
            await using var contextoBootstrap = sp.GetRequiredService<FabricaContextoDeBootstrap>().Crear();

            using (AmbitoTenantExplicito.Establecer(TenantSeedData.IdPorDefecto))
            {
                await IdentitySeeder.SeedAsync(
                    sp.GetRequiredService<UserManager<ApplicationUser>>(),
                    sp.GetRequiredService<RoleManager<IdentityRole<Guid>>>(),
                    sp.GetRequiredService<IUserStore<ApplicationUser>>(),
                    NullLogger.Instance,
                    configuracion,
                    entorno,
                    contextoBootstrap);
            }
        }

        using var lectura = arnes.Servicios.CreateScope();
        var userManager = lectura.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        using (AmbitoTenantExplicito.Establecer(TenantSeedData.IdPorDefecto))
        {
            var administrador = await userManager.FindByEmailAsync(EmailConfigurado);
            if (administrador is null)
                return (false, false, false, null);

            return (
                true,
                await userManager.IsInRoleAsync(administrador, Roles.Administrador),
                await userManager.GetTwoFactorEnabledAsync(administrador),
                await userManager.GetAuthenticatorKeyAsync(administrador));
        }
    }
}
