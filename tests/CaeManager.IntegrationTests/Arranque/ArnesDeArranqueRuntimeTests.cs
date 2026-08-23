using CaeManager.Application.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Seed;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Arranque;

/// <summary>
/// <b>El arnés antes que lo que se prueba con él.</b>
///
/// <para>
/// Todo lo que venga después —los seeders bajo RLS— descansa en que este
/// contenedor sea lo que dice ser. Si Identity funcionara pero el
/// <c>DbContext</c> subyacente estuviera conectando como propietario, los tests
/// pasarían en verde <b>sin haber ejercitado RLS ni una vez</b>. Por eso el arnés
/// se valida por capas y por separado, en vez de darse por bueno porque
/// <c>UserManager</c> no lance.
/// </para>
/// </summary>
public class ArnesDeArranqueRuntimeTests
{
    /// <summary>Capa 1 — Identity funciona: puede crear y consultar un usuario.</summary>
    [Fact]
    public async Task Identity_puede_crear_y_consultar_un_usuario()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false);
        using var ambito = arnes.Servicios.CreateScope();

        var userManager = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var usuario = new ApplicationUser
        {
            UserName = "arnes@caemanager.local",
            Email = "arnes@caemanager.local",
            NombreCompleto = "Usuario del arnés",
            EmailConfirmed = true,
            TenantId = TenantSeedData.IdPorDefecto,
        };

        var resultado = await userManager.CreateAsync(usuario, "Arnes#2026Seguro");

        resultado.Succeeded.Should().BeTrue(
            "AspNetUsers no tiene RLS, así que Identity debe funcionar igual bajo el rol restringido: " +
            string.Join(", ", resultado.Errors.Select(e => e.Description)));

        (await userManager.FindByEmailAsync("arnes@caemanager.local")).Should().NotBeNull();
    }

    /// <summary>
    /// Capa 2 — la identidad efectiva del <c>DbContext</c> sigue siendo la
    /// restringida. Es la comprobación que impide el falso verde más peligroso del
    /// arnés: que Identity funcione porque por debajo estamos conectando como
    /// propietario.
    /// </summary>
    [Fact]
    public async Task El_DbContext_del_arnes_conecta_como_cae_app_runtime()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false);
        using var ambito = arnes.Servicios.CreateScope();

        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        await contexto.Database.OpenConnectionAsync();

        var conexion = (NpgsqlConnection)contexto.Database.GetDbConnection();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = "SELECT session_user, current_user, current_setting('is_superuser');";

        await using var lector = await comando.ExecuteReaderAsync();
        (await lector.ReadAsync()).Should().BeTrue();

        lector.GetString(0).Should().Be("cae_app_runtime",
            "si el arnés conectara como propietario, todo lo que se pruebe encima pasaría sin " +
            "ejercitar RLS ni una vez");
        lector.GetString(1).Should().Be("cae_app_runtime");
        lector.GetString(2).Should().Be("off");
    }

    /// <summary>
    /// Capa 3 — RLS sigue efectiva a través del arnés, y el interceptor propaga el
    /// ámbito. Con control negativo: sin ámbito no se ve nada, con ámbito se ve lo
    /// propio, y el propietario lo ve todo.
    /// </summary>
    [Fact]
    public async Task RLS_sigue_efectiva_y_el_interceptor_propaga_el_ambito()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false);

        var tenant = Guid.NewGuid();
        await SembrarClienteComoPropietarioAsync(arnes.CadenaPropietario, tenant);

        using (var ambito = arnes.Servicios.CreateScope())
        {
            var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();

            (await contexto.Clientes.IgnoreQueryFilters().CountAsync()).Should().Be(0,
                "sin AmbitoTenantExplicito el interceptor no fija app.tenant_id y RLS no empareja nada: " +
                "la ausencia de coordenada cierra, no abre. IgnoreQueryFilters descarta el filtro de EF, " +
                "así que lo que queda observado es RLS y no la primera capa");

            using (AmbitoTenantExplicito.Establecer(tenant))
            {
                (await contexto.Clientes.IgnoreQueryFilters().CountAsync()).Should().Be(1,
                    "con el ámbito puesto, el interceptor propaga app.tenant_id y la fila aparece");
            }
        }

        await using var propietario = new NpgsqlConnection(arnes.CadenaPropietario);
        await propietario.OpenAsync();
        await using var comando = propietario.CreateCommand();
        comando.CommandText = @"SELECT count(*) FROM ""Clientes"";";

        Convert.ToInt32(await comando.ExecuteScalarAsync()).Should().Be(1,
            "control negativo: el propietario la ve siempre, así que el cero de arriba es RLS filtrando " +
            "y no una fila que no llegó a escribirse");
    }

    private static async Task SembrarClienteComoPropietarioAsync(string cadena, Guid tenant)
    {
        await using var conexion = new NpgsqlConnection(cadena);
        await conexion.OpenAsync();

        await using var comando = conexion.CreateCommand();
        // Columnas obligatorias consultadas contra information_schema, no
        // supuestas: escribir este INSERT de memoria ya fallo antes por dejarse
        // una columna NOT NULL, y el sintoma (23502 / 42703) no se parece en nada
        // a lo que el test pretende medir.
        comando.CommandText = @"
INSERT INTO ""Clientes""
    (""Id"", ""TenantId"", ""RazonSocial"", ""Cif"", ""EsCritico"", ""Version"", ""CreadoEnUtc"", ""EstaEliminado"")
VALUES (@id, @tenant, 'Cliente del arnés', @cif, false, gen_random_uuid(), now(), false);";
        comando.Parameters.AddWithValue("id", Guid.NewGuid());
        comando.Parameters.AddWithValue("tenant", tenant);
        comando.Parameters.AddWithValue("cif", $"B{Random.Shared.Next(10000000, 99999999)}");
        await comando.ExecuteNonQueryAsync();
    }
}
