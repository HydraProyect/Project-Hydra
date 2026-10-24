using CaeManager.Application.Common;
using CaeManager.Domain.Seguridad;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Seed;
using CaeManager.IntegrationTests.Arranque;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Auditoria;

/// <summary>
/// <c>RegistrosInicioSesion</c> y <c>PoliticasPaisesInicioSesion</c> bajo la
/// identidad real de la aplicación (<c>cae_app_runtime</c>): RLS del Tenant
/// propietario de la cuenta y purga acotada a 12 meses.
/// </summary>
public class RegistroInicioSesionBajoRuntimeTests
{
    [Fact]
    public async Task El_registro_solo_lo_ve_su_Tenant_y_no_se_puede_escribir_en_otro()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false);
        var usuario = Guid.NewGuid();
        Guid registroId;

        // Camino real: EF bajo el runtime, con el Tenant explícito como en el login.
        using (var ambito = arnes.Servicios.CreateScope())
        {
            var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
            using var ambitoTenant = AmbitoTenantExplicito.Establecer(TenantSeedData.IdPorDefecto);
            var registro = new RegistroInicioSesion(usuario, null, DateTime.UtcNow, MetodoInicioSesion.Contrasena,
                ResultadoInicioSesion.Correcto, "203.0.113.7", "ES", "Madrid", 40.4, -3.7, "huella", "Chrome · Windows",
                SenalesInicioSesion.DispositivoNuevo);
            contexto.RegistrosInicioSesion.Add(registro);
            await contexto.SaveChangesAsync();
            registroId = registro.Id;
        }

        var otroTenant = Guid.NewGuid();

        await using (var propio = await ConexionRuntimeAsync(arnes, TenantSeedData.IdPorDefecto))
        {
            (await ContarAsync(propio, usuario)).Should().Be(1, "control positivo: el Tenant propietario ve su fila");

            // El registro de inicios de sesión es ya la auditoría, con purga a 12
            // meses: no se duplica en RegistrosAuditoria, que no se purga y
            // guardaría IP, ciudad y huella para siempre.
            await using var auditoria = new NpgsqlCommand("""SELECT COUNT(*) FROM "RegistrosAuditoria" WHERE "EntidadId" = @id;""", propio);
            auditoria.Parameters.AddWithValue("id", registroId);
            ((long)(await auditoria.ExecuteScalarAsync())!).Should().Be(0);
        }

        await using var ajeno = await ConexionRuntimeAsync(arnes, otroTenant);
        (await ContarAsync(ajeno, usuario)).Should().Be(0, "la RLS oculta la fila a cualquier otro Tenant");

        await using var insertarEnOtro = new NpgsqlCommand(
            """
            INSERT INTO "RegistrosInicioSesion" ("Id", "UsuarioId", "OcurridoEnUtc", "Metodo", "Resultado", "Senales", "TenantId")
            VALUES (@id, @usuario, now(), 'Contrasena', 'Correcto', 0, @tenant);
            """, ajeno);
        insertarEnOtro.Parameters.AddWithValue("id", Guid.CreateVersion7());
        insertarEnOtro.Parameters.AddWithValue("usuario", usuario);
        insertarEnOtro.Parameters.AddWithValue("tenant", TenantSeedData.IdPorDefecto);
        var intento = () => insertarEnOtro.ExecuteNonQueryAsync();
        (await intento.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501",
            "el WITH CHECK de aislamiento_tenant impide escribir una fila de otro Tenant");
    }

    [Fact]
    public async Task La_politica_de_paises_tampoco_cruza_Tenants()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false);

        using (var ambito = arnes.Servicios.CreateScope())
        {
            var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
            using var ambitoTenant = AmbitoTenantExplicito.Establecer(TenantSeedData.IdPorDefecto);
            contexto.PoliticasPaisesInicioSesion.Add(new PoliticaPaisesInicioSesion(activa: true, ["ES", "PT"]));
            await contexto.SaveChangesAsync();
        }

        await using (var propio = await ConexionRuntimeAsync(arnes, TenantSeedData.IdPorDefecto))
        await using (var leer = new NpgsqlCommand("""SELECT "PaisesPermitidos" FROM "PoliticasPaisesInicioSesion";""", propio))
            ((string[])(await leer.ExecuteScalarAsync())!).Should().Equal("ES", "PT");

        await using var ajeno = await ConexionRuntimeAsync(arnes, Guid.NewGuid());
        await using var contar = new NpgsqlCommand("""SELECT COUNT(*) FROM "PoliticasPaisesInicioSesion";""", ajeno);
        ((long)(await contar.ExecuteScalarAsync())!).Should().Be(0);
    }

    [Fact]
    public async Task La_purga_borra_solo_lo_que_pasa_de_12_meses_y_el_runtime_solo_puede_pedirla()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false);
        var usuario = Guid.NewGuid();
        var viejo = Guid.CreateVersion7();
        var reciente = Guid.CreateVersion7();

        // Siembra como propietario: el runtime no puede fechar una fila en el pasado a través de la aplicación.
        await using (var propietario = new NpgsqlConnection(arnes.CadenaPropietario))
        {
            await propietario.OpenAsync();
            await using var sembrar = new NpgsqlCommand(
                """
                INSERT INTO "RegistrosInicioSesion" ("Id", "UsuarioId", "OcurridoEnUtc", "Metodo", "Resultado", "Senales", "TenantId")
                VALUES (@viejo, @usuario, now() - interval '13 months', 'Contrasena', 'Correcto', 0, @tenant),
                       (@reciente, @usuario, now() - interval '11 months', 'Contrasena', 'Correcto', 0, @tenant);
                """, propietario);
            sembrar.Parameters.AddWithValue("viejo", viejo);
            sembrar.Parameters.AddWithValue("reciente", reciente);
            sembrar.Parameters.AddWithValue("usuario", usuario);
            sembrar.Parameters.AddWithValue("tenant", TenantSeedData.IdPorDefecto);
            await sembrar.ExecuteNonQueryAsync();
        }

        await using var runtime = await ConexionRuntimeAsync(arnes, TenantSeedData.IdPorDefecto);

        await using (var borrar = new NpgsqlCommand("""DELETE FROM "RegistrosInicioSesion";""", runtime))
        {
            var intento = () => borrar.ExecuteNonQueryAsync();
            (await intento.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501",
                "el runtime no borra filas sueltas: solo la función de purga");
        }

        await using (var purgar = new NpgsqlCommand("SELECT public.app_purgar_inicios_sesion();", runtime))
            ((int)(await purgar.ExecuteScalarAsync())!).Should().BeGreaterThanOrEqualTo(1);

        await using var leer = new NpgsqlCommand("""SELECT "Id" FROM "RegistrosInicioSesion" WHERE "UsuarioId" = @usuario;""", runtime);
        leer.Parameters.AddWithValue("usuario", usuario);
        var quedan = new List<Guid>();
        await using (var lector = await leer.ExecuteReaderAsync())
            while (await lector.ReadAsync())
                quedan.Add(lector.GetGuid(0));

        quedan.Should().Equal(reciente);
    }

    private static async Task<NpgsqlConnection> ConexionRuntimeAsync(ArnesDeArranqueRuntime arnes, Guid tenantId)
    {
        var conexion = new NpgsqlConnection(BaseDatosPostgresDePruebas.CadenaComoRuntime(arnes.CadenaPropietario));
        await conexion.OpenAsync();
        await using var fijar = new NpgsqlCommand("SELECT set_config('app.tenant_id', @tenant, false);", conexion);
        fijar.Parameters.AddWithValue("tenant", tenantId.ToString());
        await fijar.ExecuteNonQueryAsync();
        return conexion;
    }

    private static async Task<long> ContarAsync(NpgsqlConnection conexion, Guid usuario)
    {
        await using var contar = new NpgsqlCommand("""SELECT COUNT(*) FROM "RegistrosInicioSesion" WHERE "UsuarioId" = @usuario;""", conexion);
        contar.Parameters.AddWithValue("usuario", usuario);
        return (long)(await contar.ExecuteScalarAsync())!;
    }
}
