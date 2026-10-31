using CaeManager.Application.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Seed;
using CaeManager.Infrastructure.Retencion;
using CaeManager.IntegrationTests.Arranque;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Auditoria;

/// <summary>
/// Conservación decidida el 2026-10-31, bajo la identidad real de la aplicación
/// (<c>cae_app_runtime</c>): la purga de auditoría cruza Tenants solo por su función, a 5 años
/// exactos y apagada por defecto; la evidencia de aceptación no se edita ni se borra; y la baja de
/// una cuenta persiste la fecha de la que cuenta su plazo.
/// </summary>
public class PurgaRegistrosAuditoriaBajoRuntimeTests
{
    private static readonly string[] Tablas =
    [
        "RegistrosAuditoria", "RegistrosAccesoDocumentoSensible", "RegistrosActividadSoporte", "AuditoriasExtraccionIa"
    ];

    [Fact]
    public async Task Apagada_cuenta_lo_caducado_de_todos_los_Tenants_sin_borrar_nada()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false);
        var (tenantA, tenantB) = (Guid.NewGuid(), Guid.NewGuid());
        await SembrarAsync(arnes, tenantA, "61 months");
        await SembrarAsync(arnes, tenantB, "61 months");

        (await EjecutarAsync(arnes, activa: false)).Should().Be(8, "cuatro tablas en dos Tenants, con la purga apagada");

        foreach (var tabla in Tablas)
            (await ContarComoPropietarioAsync(arnes, tabla, tenantA, tenantB)).Should().Be(2, $"apagada no borra de {tabla}");
    }

    [Fact]
    public async Task Encendida_borra_lo_que_pasa_de_5_anos_en_las_cuatro_tablas_y_de_cada_Tenant()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false);
        var (tenantA, tenantB) = (Guid.NewGuid(), Guid.NewGuid());
        await SembrarAsync(arnes, tenantA, "61 months");
        await SembrarAsync(arnes, tenantB, "61 months");
        var recientes = Guid.NewGuid();
        await SembrarAsync(arnes, recientes, "59 months");

        (await EjecutarAsync(arnes, activa: true)).Should().Be(8);

        foreach (var tabla in Tablas)
        {
            (await ContarComoPropietarioAsync(arnes, tabla, tenantA, tenantB)).Should().Be(0, $"{tabla} ya pasó el plazo");
            (await ContarComoPropietarioAsync(arnes, tabla, recientes)).Should().Be(1, $"{tabla} no ha cumplido el plazo");
        }
    }

    [Fact]
    public async Task Un_argumento_nulo_cuenta_y_no_borra()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false);
        var tenant = Guid.NewGuid();
        await SembrarAsync(arnes, tenant, "61 months");
        await using var runtime = await ConexionRuntimeAsync(arnes, tenant);

        await using var purgar = new NpgsqlCommand("SELECT public.app_purgar_registros_auditoria(NULL);", runtime);
        ((int)(await purgar.ExecuteScalarAsync())!).Should().Be(4);

        foreach (var tabla in Tablas)
            (await ContarComoPropietarioAsync(arnes, tabla, tenant)).Should().Be(1, $"NULL no debe borrar de {tabla}");
    }

    [Fact]
    public async Task Una_revision_de_IA_sin_resolver_conserva_su_auditoria_hasta_resolverse()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false);
        var tenant = Guid.NewGuid();
        await SembrarAsync(arnes, tenant, "61 months");
        await using var propietario = new NpgsqlConnection(arnes.CadenaPropietario);
        await propietario.OpenAsync();
        await using (var revision = new NpgsqlCommand(
            """
            INSERT INTO "RevisionesIaDocumento"
                ("Id", "DocumentoId", "AuditoriaExtraccionIaId", "ConfianzaGeneral", "Motivo", "Resuelta", "CreadaEnUtc", "TenantId")
            SELECT gen_random_uuid(), gen_random_uuid(), "Id", 50, 'test', false, now(), @tenant
              FROM "AuditoriasExtraccionIa" WHERE "TenantId" = @tenant;
            """, propietario))
        {
            revision.Parameters.AddWithValue("tenant", tenant);
            (await revision.ExecuteNonQueryAsync()).Should().Be(1);
        }

        (await EjecutarAsync(arnes, activa: false)).Should().Be(3, "la pendiente tampoco cuenta como purgable");
        await EjecutarAsync(arnes, activa: true);
        (await ContarComoPropietarioAsync(arnes, "AuditoriasExtraccionIa", tenant)).Should().Be(1, "aún debe recibir la decisión humana");

        await using (var resolver = new NpgsqlCommand("""UPDATE "RevisionesIaDocumento" SET "Resuelta" = true WHERE "TenantId" = @tenant;""", propietario))
        {
            resolver.Parameters.AddWithValue("tenant", tenant);
            await resolver.ExecuteNonQueryAsync();
        }

        await EjecutarAsync(arnes, activa: true);
        (await ContarComoPropietarioAsync(arnes, "AuditoriasExtraccionIa", tenant)).Should().Be(0, "resuelta, ya solo cuenta el plazo");
    }

    [Fact]
    public async Task El_runtime_no_borra_filas_sueltas_ni_otros_roles_piden_la_purga()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false);
        var tenant = Guid.NewGuid();
        await using var runtime = await ConexionRuntimeAsync(arnes, tenant);

        foreach (var tabla in Tablas)
        {
            await using var borrar = new NpgsqlCommand($"""DELETE FROM "{tabla}";""", runtime);
            var intento = () => borrar.ExecuteNonQueryAsync();
            (await intento.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501", $"{tabla} solo se purga por la función");
        }

        await using var privilegios = new NpgsqlCommand(
            """
            SELECT has_function_privilege('cae_app_soporte', 'public.app_purgar_registros_auditoria(boolean)', 'EXECUTE'),
                   has_function_privilege('cae_app_aprovisionamiento', 'public.app_purgar_registros_auditoria(boolean)', 'EXECUTE'),
                   has_function_privilege('cae_app_runtime', 'public.app_purgar_registros_auditoria(boolean)', 'EXECUTE');
            """, runtime);
        await using var lector = await privilegios.ExecuteReaderAsync();
        await lector.ReadAsync();
        lector.GetBoolean(0).Should().BeFalse("Soporte TALVEG no purga");
        lector.GetBoolean(1).Should().BeFalse("el aprovisionamiento no purga");
        lector.GetBoolean(2).Should().BeTrue("control positivo: el servicio de fondo corre como runtime");
    }

    [Fact]
    public async Task La_evidencia_de_aceptacion_no_se_edita_ni_se_borra_bajo_runtime()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false);
        await using var runtime = await ConexionRuntimeAsync(arnes, TenantSeedData.IdPorDefecto);
        var id = Guid.CreateVersion7();

        await using (var insertar = new NpgsqlCommand(
            """
            INSERT INTO "AceptacionesTerminos" ("Id", "UsuarioId", "VersionDocumento", "FechaAceptacionUtc")
            VALUES (@id, @usuario, 'v-test', now());
            """, runtime))
        {
            insertar.Parameters.AddWithValue("id", id);
            insertar.Parameters.AddWithValue("usuario", Guid.NewGuid());
            (await insertar.ExecuteNonQueryAsync()).Should().Be(1, "control positivo: aceptar sigue funcionando");
        }

        await using var actualizar = new NpgsqlCommand("""UPDATE "AceptacionesTerminos" SET "VersionDocumento" = 'otra';""", runtime);
        var intentoActualizar = () => actualizar.ExecuteNonQueryAsync();
        (await intentoActualizar.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501");

        await using var borrar = new NpgsqlCommand("""DELETE FROM "AceptacionesTerminos";""", runtime);
        var intentoBorrar = () => borrar.ExecuteNonQueryAsync();
        (await intentoBorrar.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501");
    }

    [Fact]
    public async Task La_baja_de_una_cuenta_persiste_su_fecha_y_reactivarla_la_anula()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false);
        var correo = $"baja-{Guid.NewGuid():N}@test.local";

        using (var ambito = arnes.Servicios.CreateScope())
        {
            var usuarios = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var cuenta = new ApplicationUser { UserName = correo, Email = correo, TenantId = TenantSeedData.IdPorDefecto };
            (await usuarios.CreateAsync(cuenta)).Succeeded.Should().BeTrue();

            cuenta.Desactivar();
            (await usuarios.UpdateAsync(cuenta)).Succeeded.Should().BeTrue();
            (await LeerFechaBajaAsync(arnes, correo)).Should().NotBeNull("de esta fecha cuentan los 12 meses");

            cuenta.Reactivar();
            (await usuarios.UpdateAsync(cuenta)).Succeeded.Should().BeTrue();
            (await LeerFechaBajaAsync(arnes, correo)).Should().BeNull("una cuenta reactivada no está de baja");
        }
    }

    private static async Task<int> EjecutarAsync(ArnesDeArranqueRuntime arnes, bool activa)
    {
        using var ambito = arnes.Servicios.CreateScope();
        var purga = new PurgaRegistrosCaducados(
            ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>(),
            Options.Create(new RetencionDatosOptions { PurgaRegistrosActiva = activa }),
            NullLogger<PurgaRegistrosCaducados>.Instance);
        return await purga.EjecutarAsync();
    }

    // Siembra como propietario: el runtime no puede fechar una fila en el pasado ni escribir en dos
    // Tenants. Estas tablas no tienen clave foránea hacia Tenants ni hacia Documentos.
    private static async Task SembrarAsync(ArnesDeArranqueRuntime arnes, Guid tenant, string antiguedad)
    {
        await using var propietario = new NpgsqlConnection(arnes.CadenaPropietario);
        await propietario.OpenAsync();
        await using var sembrar = new NpgsqlCommand(
            $"""
            INSERT INTO "RegistrosAuditoria" ("Id", "FechaUtc", "EntidadId", "TenantId", "Accion", "EntidadTipo", "TipoActor")
            VALUES (gen_random_uuid(), now() - interval '{antiguedad}', gen_random_uuid(), @tenant, 'Creado', 'Empresa', 'Sistema');
            INSERT INTO "RegistrosAccesoDocumentoSensible"
                ("Id", "OcurridoEnUtc", "DocumentoId", "TenantId", "Sensibilidad", "TipoAcceso", "TipoActor", "ViaAcceso")
            VALUES (gen_random_uuid(), now() - interval '{antiguedad}', gen_random_uuid(), @tenant, 'Alta', 'Descarga', 'Sistema', 'Web');
            INSERT INTO "RegistrosActividadSoporte" ("Id", "OcurridaEnUtc", "TenantId", "UsuarioSoporteId", "Tipo", "SesionPrivilegiadaId")
            VALUES (gen_random_uuid(), now() - interval '{antiguedad}', @tenant, gen_random_uuid(), 'Lectura', gen_random_uuid());
            INSERT INTO "AuditoriasExtraccionIa"
                ("Id", "ConfianzaGeneral", "CreadaEnUtc", "NumeroPaginas", "TiempoProcesamientoMs", "TenantId", "HashSha256", "ProveedorCodigo", "TipoEsperado", "VersionPipeline")
            VALUES (gen_random_uuid(), 90, now() - interval '{antiguedad}', 1, 0, @tenant, 'hash', 'proveedor', 'tipo', 'v1');
            """, propietario);
        sembrar.Parameters.AddWithValue("tenant", tenant);
        await sembrar.ExecuteNonQueryAsync();
    }

    private static async Task<long> ContarComoPropietarioAsync(ArnesDeArranqueRuntime arnes, string tabla, params Guid[] tenants)
    {
        await using var propietario = new NpgsqlConnection(arnes.CadenaPropietario);
        await propietario.OpenAsync();
        await using var contar = new NpgsqlCommand($"""SELECT COUNT(*) FROM "{tabla}" WHERE "TenantId" = ANY(@tenants);""", propietario);
        contar.Parameters.AddWithValue("tenants", tenants);
        return (long)(await contar.ExecuteScalarAsync())!;
    }

    private static async Task<DateTime?> LeerFechaBajaAsync(ArnesDeArranqueRuntime arnes, string correo)
    {
        await using var propietario = new NpgsqlConnection(arnes.CadenaPropietario);
        await propietario.OpenAsync();
        await using var leer = new NpgsqlCommand("""SELECT "FechaBajaUtc" FROM "AspNetUsers" WHERE "Email" = @correo;""", propietario);
        leer.Parameters.AddWithValue("correo", correo);
        return await leer.ExecuteScalarAsync() is DateTime fecha ? fecha : null;
    }

    private static async Task<NpgsqlConnection> ConexionRuntimeAsync(ArnesDeArranqueRuntime arnes, Guid tenantId)
    {
        var conexion = new NpgsqlConnection(BaseDatosPostgresDePruebas.CadenaComoRuntime(arnes.CadenaPropietario));
        await conexion.OpenAsync();
        await ContextoRlsDePruebas.FijarAsync(conexion, tenantId: tenantId);
        return conexion;
    }
}
