using CaeManager.Application.Common;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Soporte;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Seed;
using CaeManager.IntegrationTests.Arranque;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Auditoria;

/// <summary>
/// <b>Los registros de auditoría y la traza de Soporte TALVEG son de solo
/// inserción para <c>cae_app_runtime</c></b> (migraciones
/// <c>AuditoriaSoloInsercionParaRuntime</c>,
/// <c>HabilitarRlsRegistrosAccesoDocumentoSensible</c> y
/// <c>ActividadSoporteSoloInsercionParaRuntime</c>).
///
/// <para>
/// Conecta como <c>cae_app_runtime</c> real (login, no <c>SET ROLE</c> desde el
/// propietario) sobre el arnés de arranque con los interceptores de producción:
/// la fila de auditoría la escribe <c>AuditoriaInterceptor</c> bajo ese mismo
/// rol, así que el test demuestra a la vez que el INSERT sigue funcionando y que
/// UPDATE/DELETE se rechazan. El rechazo esperado es <c>42501</c>
/// (<i>insufficient_privilege</i>): lo produce el REVOKE, no RLS — una política
/// que no autorizara el verbo dejaría el UPDATE en cero filas sin error.
/// </para>
/// </summary>
public class AuditoriaSoloInsercionBajoRuntimeTests
{
    [Fact]
    public async Task Runtime_inserta_auditoria_pero_no_puede_actualizarla_ni_borrarla()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false);

        await EscribirEmpresaAsync(arnes);

        await using var conexionRuntime = new NpgsqlConnection(
            BaseDatosPostgresDePruebas.CadenaComoRuntime(arnes.CadenaPropietario));
        await conexionRuntime.OpenAsync();
        await using (var comandoSetTenant = conexionRuntime.CreateCommand())
        {
            comandoSetTenant.CommandText = "SELECT set_config('app.tenant_id', $1, false);";
            comandoSetTenant.Parameters.AddWithValue(TenantSeedData.IdPorDefecto.ToString());
            await comandoSetTenant.ExecuteNonQueryAsync();
        }

        // Control positivo del instrumento, ANTES de los rechazos: la fila la
        // escribió el interceptor como runtime y el mismo rol la ve. Sin esto,
        // un 42501 abajo podría ser una conexión sin SELECT, no el REVOKE.
        await using (var comandoSelect = conexionRuntime.CreateCommand())
        {
            comandoSelect.CommandText = """SELECT COUNT(*) FROM "RegistrosAuditoria" WHERE "EntidadTipo" = 'Empresa';""";
            var total = (long)(await comandoSelect.ExecuteScalarAsync())!;
            total.Should().BeGreaterThan(0, "el INSERT de auditoría como runtime tiene que seguir funcionando");
        }

        await using var comandoUpdate = conexionRuntime.CreateCommand();
        comandoUpdate.CommandText = """UPDATE "RegistrosAuditoria" SET "EntidadTipo" = 'Reescrito';""";
        var intentoUpdate = () => comandoUpdate.ExecuteNonQueryAsync();
        (await intentoUpdate.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501");

        await using var comandoDelete = conexionRuntime.CreateCommand();
        comandoDelete.CommandText = """DELETE FROM "RegistrosAuditoria";""";
        var intentoDelete = () => comandoDelete.ExecuteNonQueryAsync();
        (await intentoDelete.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501");
    }

    /// <summary>
    /// La traza de Soporte TALVEG (vía heredada y Sesiones Privilegiadas): el
    /// runtime la escribe por el camino real —EF con los interceptores de
    /// producción, como <c>AbrirAccesoSoporteCommand</c>— y no puede reescribirla
    /// ni borrarla.
    /// </summary>
    [Fact]
    public async Task Runtime_inserta_actividad_de_soporte_pero_no_puede_actualizarla_ni_borrarla()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false);

        using (var ambito = arnes.Servicios.CreateScope())
        {
            var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
            using var ambitoTenant = AmbitoTenantExplicito.Establecer(TenantSeedData.IdPorDefecto);
            contexto.RegistrosActividadSoporte.Add(RegistroActividadSoporte.PorViaHeredada(
                Guid.NewGuid(), Guid.NewGuid(), TipoActividadSoporte.AccesoConcedido, "Motivo de la apertura"));
            await contexto.SaveChangesAsync();
        }

        await using var conexionRuntime = new NpgsqlConnection(
            BaseDatosPostgresDePruebas.CadenaComoRuntime(arnes.CadenaPropietario));
        await conexionRuntime.OpenAsync();
        await using (var comandoSetTenant = conexionRuntime.CreateCommand())
        {
            comandoSetTenant.CommandText = "SELECT set_config('app.tenant_id', $1, false);";
            comandoSetTenant.Parameters.AddWithValue(TenantSeedData.IdPorDefecto.ToString());
            await comandoSetTenant.ExecuteNonQueryAsync();
        }

        // Control positivo: la fila existe y el runtime la ve; sin esto, un 42501
        // abajo podría ser una conexión sin SELECT, no el REVOKE.
        await using (var comandoSelect = conexionRuntime.CreateCommand())
        {
            comandoSelect.CommandText = """SELECT COUNT(*) FROM "RegistrosActividadSoporte";""";
            ((long)(await comandoSelect.ExecuteScalarAsync())!).Should().Be(1,
                "el INSERT de la traza de soporte como runtime tiene que seguir funcionando");
        }

        await using var comandoUpdate = conexionRuntime.CreateCommand();
        comandoUpdate.CommandText = """UPDATE "RegistrosActividadSoporte" SET "Detalle" = 'Reescrito';""";
        var intentoUpdate = () => comandoUpdate.ExecuteNonQueryAsync();
        (await intentoUpdate.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501");

        await using var comandoDelete = conexionRuntime.CreateCommand();
        comandoDelete.CommandText = """DELETE FROM "RegistrosActividadSoporte";""";
        var intentoDelete = () => comandoDelete.ExecuteNonQueryAsync();
        (await intentoDelete.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501");
    }

    /// <summary>
    /// El contrato de privilegios, leído del catálogo para las dos tablas de
    /// auditoría: INSERT y SELECT sí, UPDATE y DELETE no. Trinquete frente a un
    /// GRANT posterior sobre todas las tablas que devolviera los dos verbos sin
    /// que nadie ejercitara un UPDATE.
    /// </summary>
    [Theory]
    [InlineData("RegistrosAuditoria")]
    [InlineData("RegistrosAccesoDocumentoSensible")]
    [InlineData("RegistrosActividadSoporte")]
    public async Task Runtime_solo_tiene_insert_y_select_sobre_la_auditoria(string tabla)
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false);

        await using var conexion = new NpgsqlConnection(arnes.CadenaPropietario);
        await conexion.OpenAsync();

        foreach (var (privilegio, esperado) in new[] { ("INSERT", true), ("SELECT", true), ("UPDATE", false), ("DELETE", false) })
        {
            await using var orden = new NpgsqlCommand(
                "SELECT has_table_privilege('cae_app_runtime', quote_ident(@tabla), @privilegio);", conexion);
            orden.Parameters.AddWithValue("tabla", tabla);
            orden.Parameters.AddWithValue("privilegio", privilegio);

            var tiene = (bool)(await orden.ExecuteScalarAsync())!;

            tiene.Should().Be(esperado, $"cae_app_runtime {(esperado ? "necesita" : "no debe tener")} {privilegio} sobre {tabla}");
        }
    }

    private static async Task EscribirEmpresaAsync(ArnesDeArranqueRuntime arnes)
    {
        using var ambito = arnes.Servicios.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();

        using var ambitoTenant = AmbitoTenantExplicito.Establecer(TenantSeedData.IdPorDefecto);

        contexto.Empresas.Add(Empresa.CrearComoCliente(
            "Empresa auditada", "B12345674", esCritico: false, notas: null, ejecutivoUsuarioId: null));

        await contexto.SaveChangesAsync();
    }
}
