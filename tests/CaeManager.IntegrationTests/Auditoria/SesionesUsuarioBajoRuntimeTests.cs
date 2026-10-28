using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Persistence.Seed;
using CaeManager.IntegrationTests.Arranque;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Auditoria;

/// <summary>
/// <c>SesionesUsuario</c> (revocación de sesión en servidor) bajo la identidad real de la aplicación
/// (<c>cae_app_runtime</c>): RLS del Tenant propietario de la cuenta, solo la propia cuenta revoca,
/// una revocación no se deshace, sin DELETE, <c>app_sesion_vigente</c> sin Tenant en el contexto y
/// purga a 5 años.
/// </summary>
public class SesionesUsuarioBajoRuntimeTests
{
    [Fact]
    public async Task Revocar_una_sesion_deja_viva_la_otra_de_la_misma_cuenta()
    {
        // El arnés no registra el registro de sesiones: se añade el real, el de producción.
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(
            datosDePruebaActivos: false,
            serviciosAdicionales: s => s.AddScoped<IRegistroSesionesUsuario, RegistroSesionesUsuario>());
        var cuenta = Guid.NewGuid();
        var esta = Guid.NewGuid();
        var otra = Guid.NewGuid();

        // Camino real del inicio de sesión: el registro, en el Tenant propietario de la cuenta.
        using (var ambito = arnes.Servicios.CreateScope())
        {
            var sesiones = ambito.ServiceProvider.GetRequiredService<IRegistroSesionesUsuario>();
            await sesiones.RegistrarAsync(cuenta, TenantSeedData.IdPorDefecto, esta);
            await sesiones.RegistrarAsync(cuenta, TenantSeedData.IdPorDefecto, otra);
            (await sesiones.EstaVigenteAsync(cuenta, esta)).Should().BeTrue("control positivo: la sesión recién emitida vale");
        }

        await using (var propia = await ConexionRuntimeAsync(arnes, TenantSeedData.IdPorDefecto, cuenta))
            (await RevocarAsync(propia, esta)).Should().Be(1);

        await using var sinContexto = await ConexionRuntimeAsync(arnes, tenantId: null, usuarioId: null);
        (await VigenteAsync(sinContexto, esta, cuenta)).Should().BeFalse("la sesión revocada deja de valer");
        (await VigenteAsync(sinContexto, otra, cuenta)).Should().BeTrue("la otra sesión de la misma cuenta sigue viva");
        (await VigenteAsync(sinContexto, otra, Guid.NewGuid())).Should().BeFalse("el sid solo vale con su cuenta");
    }

    [Fact]
    public async Task Una_revocacion_no_se_deshace_y_otra_cuenta_no_revoca()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false);
        var cuenta = Guid.NewGuid();
        var sid = Guid.NewGuid();
        await InsertarAsync(arnes, cuenta, sid);

        await using (var otraCuenta = await ConexionRuntimeAsync(arnes, TenantSeedData.IdPorDefecto, Guid.NewGuid()))
            (await RevocarAsync(otraCuenta, sid)).Should().Be(0, "solo_la_propia_cuenta_revoca oculta la fila a otra cuenta del Tenant");

        await using var propia = await ConexionRuntimeAsync(arnes, TenantSeedData.IdPorDefecto, cuenta);
        (await RevocarAsync(propia, sid)).Should().Be(1);

        await using (var reabrir = new NpgsqlCommand("""UPDATE "SesionesUsuario" SET "RevocadaEnUtc" = NULL WHERE "Sid" = @sid;""", propia))
        {
            reabrir.Parameters.AddWithValue("sid", sid);
            var intento = () => reabrir.ExecuteNonQueryAsync();
            (await intento.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514",
                "sesion_revocada_no_se_reabre: una revocación no se deshace");
        }

        await using (var cambiarSid = new NpgsqlCommand("""UPDATE "SesionesUsuario" SET "Sid" = @nuevo WHERE "Sid" = @sid;""", propia))
        {
            cambiarSid.Parameters.AddWithValue("sid", sid);
            cambiarSid.Parameters.AddWithValue("nuevo", Guid.NewGuid());
            var intento = () => cambiarSid.ExecuteNonQueryAsync();
            (await intento.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501",
                "el runtime solo puede actualizar RevocadaEnUtc");
        }

        await using var borrar = new NpgsqlCommand("""DELETE FROM "SesionesUsuario";""", propia);
        var borrado = () => borrar.ExecuteNonQueryAsync();
        (await borrado.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501",
            "el runtime no borra filas sueltas: solo la función de purga");
    }

    [Fact]
    public async Task La_sesion_no_cruza_Tenants()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false);
        var cuenta = Guid.NewGuid();
        var sid = Guid.NewGuid();
        await InsertarAsync(arnes, cuenta, sid);

        await using (var propio = await ConexionRuntimeAsync(arnes, TenantSeedData.IdPorDefecto, cuenta))
            (await ContarAsync(propio, cuenta)).Should().Be(1, "control positivo: el Tenant propietario ve su fila");

        await using var ajeno = await ConexionRuntimeAsync(arnes, Guid.NewGuid(), cuenta);
        (await ContarAsync(ajeno, cuenta)).Should().Be(0, "la RLS oculta la fila a cualquier otro Tenant");
        (await RevocarAsync(ajeno, sid)).Should().Be(0, "ni la propia cuenta revoca desde otro Tenant");

        await using var insertarEnOtro = new NpgsqlCommand(
            """
            INSERT INTO "SesionesUsuario" ("Id", "UsuarioId", "Sid", "EmitidaEnUtc", "TenantId")
            VALUES (@id, @usuario, @sid, now(), @tenant);
            """, ajeno);
        insertarEnOtro.Parameters.AddWithValue("id", Guid.CreateVersion7());
        insertarEnOtro.Parameters.AddWithValue("usuario", cuenta);
        insertarEnOtro.Parameters.AddWithValue("sid", Guid.NewGuid());
        insertarEnOtro.Parameters.AddWithValue("tenant", TenantSeedData.IdPorDefecto);
        var intento = () => insertarEnOtro.ExecuteNonQueryAsync();
        (await intento.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501",
            "el WITH CHECK de aislamiento_tenant impide escribir una fila de otro Tenant");
    }

    [Fact]
    public async Task La_purga_borra_solo_lo_que_pasa_de_5_anos_y_el_runtime_solo_puede_pedirla()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false);
        var cuenta = Guid.NewGuid();
        var viejo = Guid.NewGuid();
        var reciente = Guid.NewGuid();

        // Siembra como propietario: el runtime no puede fechar una fila en el pasado a través de la aplicación.
        await using (var propietario = new NpgsqlConnection(arnes.CadenaPropietario))
        {
            await propietario.OpenAsync();
            await using var sembrar = new NpgsqlCommand(
                """
                INSERT INTO "SesionesUsuario" ("Id", "UsuarioId", "Sid", "EmitidaEnUtc", "TenantId")
                VALUES (@id1, @usuario, @viejo, now() - interval '61 months', @tenant),
                       (@id2, @usuario, @reciente, now() - interval '59 months', @tenant);
                """, propietario);
            sembrar.Parameters.AddWithValue("id1", Guid.CreateVersion7());
            sembrar.Parameters.AddWithValue("id2", Guid.CreateVersion7());
            sembrar.Parameters.AddWithValue("viejo", viejo);
            sembrar.Parameters.AddWithValue("reciente", reciente);
            sembrar.Parameters.AddWithValue("usuario", cuenta);
            sembrar.Parameters.AddWithValue("tenant", TenantSeedData.IdPorDefecto);
            await sembrar.ExecuteNonQueryAsync();
        }

        await using var runtime = await ConexionRuntimeAsync(arnes, TenantSeedData.IdPorDefecto, cuenta);
        await using (var purgar = new NpgsqlCommand("SELECT public.app_purgar_sesiones_usuario();", runtime))
            ((int)(await purgar.ExecuteScalarAsync())!).Should().BeGreaterThanOrEqualTo(1);

        (await VigenteAsync(runtime, viejo, cuenta)).Should().BeFalse("purgar no reabre nada: sin fila, la sesión no vale");
        (await VigenteAsync(runtime, reciente, cuenta)).Should().BeTrue();
        (await ContarAsync(runtime, cuenta)).Should().Be(1);
    }

    private static async Task InsertarAsync(ArnesDeArranqueRuntime arnes, Guid cuenta, Guid sid)
    {
        // Como el inicio de sesión: Tenant propietario en el contexto y todavía sin usuario.
        await using var conexion = await ConexionRuntimeAsync(arnes, TenantSeedData.IdPorDefecto, usuarioId: null);
        await using var insertar = new NpgsqlCommand(
            """
            INSERT INTO "SesionesUsuario" ("Id", "UsuarioId", "Sid", "EmitidaEnUtc", "TenantId")
            VALUES (@id, @usuario, @sid, now(), @tenant);
            """, conexion);
        insertar.Parameters.AddWithValue("id", Guid.CreateVersion7());
        insertar.Parameters.AddWithValue("usuario", cuenta);
        insertar.Parameters.AddWithValue("sid", sid);
        insertar.Parameters.AddWithValue("tenant", TenantSeedData.IdPorDefecto);
        await insertar.ExecuteNonQueryAsync();
    }

    private static async Task<int> RevocarAsync(NpgsqlConnection conexion, Guid sid)
    {
        await using var revocar = new NpgsqlCommand(
            """UPDATE "SesionesUsuario" SET "RevocadaEnUtc" = now() WHERE "Sid" = @sid AND "RevocadaEnUtc" IS NULL;""", conexion);
        revocar.Parameters.AddWithValue("sid", sid);
        return await revocar.ExecuteNonQueryAsync();
    }

    private static async Task<bool> VigenteAsync(NpgsqlConnection conexion, Guid sid, Guid cuenta)
    {
        await using var vigente = new NpgsqlCommand("SELECT public.app_sesion_vigente(@sid, @usuario);", conexion);
        vigente.Parameters.AddWithValue("sid", sid);
        vigente.Parameters.AddWithValue("usuario", cuenta);
        return (bool)(await vigente.ExecuteScalarAsync())!;
    }

    private static async Task<NpgsqlConnection> ConexionRuntimeAsync(ArnesDeArranqueRuntime arnes, Guid? tenantId, Guid? usuarioId)
    {
        var conexion = new NpgsqlConnection(BaseDatosPostgresDePruebas.CadenaComoRuntime(arnes.CadenaPropietario));
        await conexion.OpenAsync();
        if (tenantId is not null || usuarioId is not null)
            await ContextoRlsDePruebas.FijarAsync(conexion, tenantId: tenantId, usuarioId: usuarioId);
        return conexion;
    }

    private static async Task<long> ContarAsync(NpgsqlConnection conexion, Guid cuenta)
    {
        await using var contar = new NpgsqlCommand("""SELECT COUNT(*) FROM "SesionesUsuario" WHERE "UsuarioId" = @usuario;""", conexion);
        contar.Parameters.AddWithValue("usuario", cuenta);
        return (long)(await contar.ExecuteScalarAsync())!;
    }
}
