using CaeManager.Application.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Seed;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Arranque;

/// <summary>
/// <b>Las cuatro propiedades del arranque tenant-scoped bajo RLS efectiva.</b>
///
/// <para>
/// #261 demostró que la cadena de datos de demo se ejecuta sin violar ninguna
/// política. Eso es compatibilidad, no comportamiento: <i>"no lanzó excepción"</i>
/// no dice dónde escribió. Aquí se comprueban identidad, ámbito, exclusión y
/// resultado.
/// </para>
///
/// <para>
/// <b>No son 51 aserciones.</b> Las 51 tablas son superficie afectada; lo que hace
/// falta son observables que <b>discriminen</b>. Se usa <c>TiposDocumento</c>
/// porque el seeder crea una copia del catálogo <b>por tenant</b>, así que los
/// recuentos por tenant y el total son necesariamente distintos — y esa
/// diferencia es justo lo que separa "ve lo suyo" de "lo ve todo".
/// </para>
/// </summary>
public class CuatroPropiedadesDelArranqueTests
{
    [Fact]
    public async Task DelegacionDemoSeeder_identidad_ambito_exclusion_y_resultado()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: true);

        var identidadDurante = await EjecutarSeederYSondarIdentidadAsync(arnes);

        // ── 1 · IDENTIDAD ──────────────────────────────────────────────────
        identidadDurante.Should().Be("cae_app_runtime",
            "si el seeder hubiera corrido como propietario, las tres propiedades siguientes se " +
            "cumplirían sin haber ejercitado RLS ni una vez");

        // ── 4 · RESULTADO ──────────────────────────────────────────────────
        var tenants = await TenantsPorNombreComoPropietarioAsync(arnes.CadenaPropietario);

        tenants.Keys.Should().Contain(
            [
                DelegacionDemoSeeder.NombreTenantConsultora,
                DelegacionDemoSeeder.NombreTenantRefrielectric,
                DelegacionDemoSeeder.NombreTenantClienteDemo,
                DelegacionDemoSeeder.NombreTenantClienteDemo2,
                DelegacionDemoSeeder.NombreTenantClienteDemo3,
            ],
            "el contrato del seeder es crear los tenants de demo; sin ellos no hay nada que aislar");

        var demo = tenants[DelegacionDemoSeeder.NombreTenantClienteDemo];
        var demo2 = tenants[DelegacionDemoSeeder.NombreTenantClienteDemo2];

        var total = await ContarTiposDocumentoComoPropietarioAsync(arnes.CadenaPropietario);
        total.Should().BeGreaterThan(0, "sin filas que aislar, la exclusión no observaría nada");

        // ── 2 · ÁMBITO y 3 · EXCLUSIÓN ─────────────────────────────────────
        using var ambito = arnes.Servicios.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();

        var (vistosDesdeDemo, ajenosDesdeDemo) = await ObservarDesdeAsync(contexto, demo);
        var (vistosDesdeDemo2, ajenosDesdeDemo2) = await ObservarDesdeAsync(contexto, demo2);

        vistosDesdeDemo.Should().BeGreaterThan(0, "el seeder crea una copia del catálogo por tenant");
        vistosDesdeDemo2.Should().BeGreaterThan(0);

        ajenosDesdeDemo.Should().Be(0,
            "EXCLUSIÓN: desde el ámbito de un tenant no puede verse ni una fila de otro");
        ajenosDesdeDemo2.Should().Be(0);

        vistosDesdeDemo.Should().BeLessThan(total,
            "el control que hace discriminante la exclusión: si 've lo suyo' y 'lo ve todo' dieran el " +
            "mismo número, estas aserciones pasarían igual con RLS desactivada");

        (vistosDesdeDemo + vistosDesdeDemo2).Should().BeLessThanOrEqualTo(total,
            "dos tenants no pueden sumar más filas de las que existen: si lo hicieran, alguna se " +
            "estaría contando desde los dos ámbitos");
    }

    /// <summary>
    /// Punto de entrada independiente, y se comprueba como tal: que
    /// <c>DelegacionDemoSeeder</c> cumpla las cuatro propiedades no dice nada de
    /// este. Superficie mucho menor —un tenant, sus parámetros y su catálogo—,
    /// mismo arnés, mismas cuatro propiedades.
    /// </summary>
    [Fact]
    public async Task SegundoTenantSeeder_identidad_ambito_exclusion_y_resultado()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(
            datosDePruebaActivos: false, segundoTenantActivo: true);

        string identidad;
        using (var ambitoSiembra = arnes.Servicios.CreateScope())
        {
            var sp = ambitoSiembra.ServiceProvider;
            var contextoSiembra = sp.GetRequiredService<CaeManagerDbContext>();

            identidad = await SondarIdentidadAsync(contextoSiembra);

            await SegundoTenantSeeder.SeedAsync(
                contextoSiembra,
                sp.GetRequiredService<UserManager<ApplicationUser>>(),
                sp.GetRequiredService<IUserStore<ApplicationUser>>(),
                sp.GetRequiredService<IConfiguration>(),
                NullLogger.Instance);
        }

        identidad.Should().Be("cae_app_runtime");

        var tenants = await TenantsPorNombreComoPropietarioAsync(arnes.CadenaPropietario);
        tenants.Keys.Should().Contain(SegundoTenantSeeder.NombreSegundoTenant,
            "el contrato del seeder es crear ese tenant");

        var segundo = tenants[SegundoTenantSeeder.NombreSegundoTenant];
        var total = await ContarTiposDocumentoComoPropietarioAsync(arnes.CadenaPropietario);

        using var ambito = arnes.Servicios.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();

        var (vistos, ajenos) = await ObservarDesdeAsync(contexto, segundo);

        vistos.Should().BeGreaterThan(0, "el seeder le copia el catálogo de tipos de documento");
        ajenos.Should().Be(0, "EXCLUSIÓN: desde su ámbito no puede verse ni una fila de otro tenant");
        vistos.Should().BeLessThan(total,
            "el contraste que hace discriminante la exclusión: el tenant sembrado por la migración " +
            "también tiene catálogo, así que 've lo suyo' y 'lo ve todo' son números distintos");
    }

    // ── Andamiaje ──────────────────────────────────────────────────────────

    /// <summary>
    /// Ejecuta el seeder y devuelve el <c>current_user</c> observado <b>en la misma
    /// conexión</b>, no en una aparte: la identidad que interesa es la que tuvo la
    /// operación, no la que tendría otra.
    /// </summary>
    private static async Task<string> EjecutarSeederYSondarIdentidadAsync(ArnesDeArranqueRuntime arnes)
    {
        using var ambito = arnes.Servicios.CreateScope();
        var sp = ambito.ServiceProvider;
        var contexto = sp.GetRequiredService<CaeManagerDbContext>();

        // La sonda abre y CIERRA. Mantenerla abierta rompe el seeder, y el motivo
        // merece quedar escrito: TenantRlsConnectionInterceptor fija app.tenant_id
        // al ABRIR la conexion. Una conexion sostenida a traves de cambios de
        // ambito conserva la coordenada del momento en que se abrio —vacia, si se
        // abrio antes del primer ambito— y toda escritura tenantizada choca contra
        // su propia politica con 42501. En produccion no muerde porque EF abre y
        // cierra por operacion; aqui lo provoco la propia sonda.
        var identidad = await SondarIdentidadAsync(contexto);

        await DelegacionDemoSeeder.SeedAsync(
            contexto,
            sp.GetRequiredService<UserManager<ApplicationUser>>(),
            sp.GetRequiredService<IUserStore<ApplicationUser>>(),
            sp.GetRequiredService<IConfiguration>(),
            NullLogger.Instance);

        return identidad;
    }

    /// <summary>
    /// Cuántas filas ve un ámbito, y cuántas de ellas son de OTRO tenant.
    /// <c>IgnoreQueryFilters</c> descarta el filtro global de EF: lo que quede
    /// observado es RLS y no la primera capa.
    /// </summary>
    /// <summary>
    /// Abre y CIERRA. Mantener la conexión abierta rompe los seeders, y el motivo
    /// merece quedar escrito: <c>TenantRlsConnectionInterceptor</c> fija
    /// <c>app.tenant_id</c> al <b>abrir</b>. Una conexión sostenida a través de
    /// cambios de ámbito conserva la coordenada del momento en que se abrió
    /// —vacía, si fue antes del primer ámbito— y toda escritura tenantizada choca
    /// contra su propia política con <c>42501</c>.
    ///
    /// <para>
    /// El contrato vigente es ese, y el test lo refleja en vez de forzarlo:
    /// <b>la conexión se abre después de establecer el ámbito</b>. Que
    /// <c>app.tenant_id</c> tenga semántica de conexión mientras
    /// <c>AmbitoTenantExplicito</c> la tiene de operación es una fragilidad
    /// registrada aparte, no algo que se corrija aquí.
    /// </para>
    /// </summary>
    private static async Task<string> SondarIdentidadAsync(CaeManagerDbContext contexto)
    {
        await contexto.Database.OpenConnectionAsync();
        try
        {
            await using var sonda = contexto.Database.GetDbConnection().CreateCommand();
            sonda.CommandText = "SELECT current_user;";
            return (string)(await sonda.ExecuteScalarAsync())!;
        }
        finally
        {
            await contexto.Database.CloseConnectionAsync();
        }
    }

    private static async Task<(int Vistos, int Ajenos)> ObservarDesdeAsync(
        CaeManagerDbContext contexto, Guid tenant)
    {
        using var _ = AmbitoTenantExplicito.Establecer(tenant);

        var vistos = await contexto.TiposDocumento.IgnoreQueryFilters().CountAsync();
        var ajenos = await contexto.TiposDocumento.IgnoreQueryFilters()
            .CountAsync(t => t.TenantId != tenant);

        return (vistos, ajenos);
    }

    private static async Task<Dictionary<string, Guid>> TenantsPorNombreComoPropietarioAsync(string cadena)
    {
        await using var conexion = new NpgsqlConnection(cadena);
        await conexion.OpenAsync();

        await using var comando = conexion.CreateCommand();
        comando.CommandText = @"SELECT ""Nombre"", ""Id"" FROM ""Tenants"";";

        var tenants = new Dictionary<string, Guid>();
        await using var lector = await comando.ExecuteReaderAsync();
        while (await lector.ReadAsync()) tenants[lector.GetString(0)] = lector.GetGuid(1);

        return tenants;
    }

    private static async Task<int> ContarTiposDocumentoComoPropietarioAsync(string cadena)
    {
        await using var conexion = new NpgsqlConnection(cadena);
        await conexion.OpenAsync();

        await using var comando = conexion.CreateCommand();
        comando.CommandText = @"SELECT count(*) FROM ""TiposDocumento"";";
        return Convert.ToInt32(await comando.ExecuteScalarAsync());
    }
}
