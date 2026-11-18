using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Inbound.AccesoFisico;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Inbound.AccesoFisico;

/// <summary>
/// Credenciales de acceso físico (AF-02) bajo <c>cae_app_runtime</c> con contexto firmado. X e Y son dos Tenants TCE que
/// acreditan al mismo Trabajador del Tenant TEC T (mismo TenantOrigenId y TrabajadorOrigenId): cada uno emite la suya y
/// ninguno ve la del otro. Lo que prueba la base: CredencialesAcceso tiene RLS forzada con la política de aislamiento; el
/// runtime inserta y cambia solo estado, serie, instantánea y trazas de cambio, nunca borra ni reescribe la procedencia, y
/// Soporte y aprovisionamiento no tocan la tabla; el índice único parcial deja una sola Emitida por Trabajador y Tenant TCE (y no limita el histórico);
/// los CHECK impiden una credencial con origen en el propio Tenant, una revocación sin motivo cerrado y una serie menor
/// que uno.
/// </summary>
public class CredencialesAccesoBajoRuntimeTests : IAsyncLifetime
{
    private readonly string _cadena = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Tenant _tceX = new("Tenant TCE X");
    private readonly Tenant _tceY = new("Tenant TCE Y");
    private readonly Tenant _tecT = new("Tenant TEC T");
    private readonly Guid _usuarioX = Guid.NewGuid();
    private readonly Guid _usuarioY = Guid.NewGuid();
    private readonly Guid _usuarioT = Guid.NewGuid();
    private readonly Guid _trabajador = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var contexto = Contexto(_tceX.Id);
        await contexto.Database.MigrateAsync();
        contexto.Tenants.AddRange(_tceX, _tceY, _tecT);
        var roles = await contexto.Roles.ToDictionaryAsync(r => r.Name!, r => r.Id);
        Cuenta(contexto, _usuarioX, _tceX.Id, roles[Roles.GestorCae]);
        Cuenta(contexto, _usuarioY, _tceY.Id, roles[Roles.GestorCae]);
        Cuenta(contexto, _usuarioT, _tecT.Id, roles[Roles.Administrador]);
        await contexto.SaveChangesAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadena);

    [Fact]
    public async Task CredencialesAcceso_tiene_RLS_forzada_con_la_politica_de_aislamiento()
    {
        await using var propietario = new NpgsqlConnection(_cadena);
        await propietario.OpenAsync();
        await using var orden = new NpgsqlCommand(
            """
            SELECT c.relrowsecurity, c.relforcerowsecurity,
                   (SELECT array_agg(p.polname::text ORDER BY p.polname) FROM pg_policy p WHERE p.polrelid = c.oid)
            FROM pg_class c WHERE c.oid = 'public."CredencialesAcceso"'::regclass;
            """, propietario);
        await using var lector = await orden.ExecuteReaderAsync();
        (await lector.ReadAsync()).Should().BeTrue();

        lector.GetBoolean(0).Should().BeTrue("ENABLE ROW LEVEL SECURITY");
        lector.GetBoolean(1).Should().BeTrue("FORCE ROW LEVEL SECURITY");
        lector.GetFieldValue<string[]>(2).Should().Equal("aislamiento_tenant");
    }

    [Fact]
    public async Task El_runtime_inserta_y_cambia_solo_el_estado_nunca_borra_y_Soporte_no_la_toca()
    {
        await using var propietario = new NpgsqlConnection(_cadena);
        await propietario.OpenAsync();

        async Task<bool> Tabla(string rol, string privilegio)
        {
            await using var orden = new NpgsqlCommand(
                "SELECT has_table_privilege(@rol, 'public.\"CredencialesAcceso\"', @privilegio)", propietario);
            orden.Parameters.AddWithValue("rol", rol);
            orden.Parameters.AddWithValue("privilegio", privilegio);
            return (bool)(await orden.ExecuteScalarAsync())!;
        }

        async Task<bool> Columna(string rol, string columna)
        {
            await using var orden = new NpgsqlCommand(
                "SELECT has_column_privilege(@rol, 'public.\"CredencialesAcceso\"', @columna, 'UPDATE')", propietario);
            orden.Parameters.AddWithValue("rol", rol);
            orden.Parameters.AddWithValue("columna", columna);
            return (bool)(await orden.ExecuteScalarAsync())!;
        }

        (await Tabla("cae_app_runtime", "SELECT")).Should().BeTrue();
        (await Tabla("cae_app_runtime", "INSERT")).Should().BeTrue();
        (await Tabla("cae_app_runtime", "DELETE")).Should().BeFalse("una credencial no se borra: se revoca");
        (await Tabla("cae_app_runtime", "TRUNCATE")).Should().BeFalse();
        foreach (var columna in new[]
                 {
                     "Serie", "Estado", "SujetoNombre", "SujetoDocumentoIdentidad", "RegeneradaEnUtc", "RegeneradaPorUsuarioId",
                     "MotivoRevocacion", "RevocadaEnUtc", "RevocadaPorUsuarioId", "CaducadaEnUtc", "Version",
                 })
            (await Columna("cae_app_runtime", columna)).Should().BeTrue(columna);
        foreach (var columna in new[]
                 {
                     "TenantId", "TenantOrigenId", "TrabajadorOrigenId", "EmitidaPorUsuarioId", "EmitidaEnUtc", "CreadoEnUtc",
                     "EstaEliminado", "EliminadoEnUtc",
                 })
            (await Columna("cae_app_runtime", columna)).Should().BeFalse($"{columna}: la procedencia y la emisión no se reescriben");

        foreach (var rol in new[] { "cae_app_soporte", "cae_app_aprovisionamiento" })
            foreach (var privilegio in new[] { "SELECT", "INSERT", "UPDATE", "DELETE" })
                (await Tabla(rol, privilegio)).Should().BeFalse($"{rol}: {privilegio}; nombre y documento de Trabajadores de otro Tenant");

        // Y de verdad bajo runtime con contexto firmado: DELETE y reescribir la procedencia responden 42501.
        await EmitirAsync(_tceX, _usuarioX);
        await using var contexto = Contexto(_tceX.Id, _usuarioX);
        foreach (var sentencia in new[]
                 {
                     "DELETE FROM \"CredencialesAcceso\"",
                     "UPDATE \"CredencialesAcceso\" SET \"TrabajadorOrigenId\" = gen_random_uuid()",
                     "UPDATE \"CredencialesAcceso\" SET \"TenantOrigenId\" = gen_random_uuid()",
                 })
        {
            (await FluentActions.Awaiting(() => contexto.Database.ExecuteSqlRawAsync(sentencia)).Should().ThrowAsync<PostgresException>())
                .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege, sentencia);
        }
    }

    [Fact]
    public async Task Dos_Tenants_TCE_emiten_credencial_al_mismo_Trabajador_y_ninguno_ve_la_del_otro()
    {
        var deX = await EmitirAsync(_tceX, _usuarioX);
        var deY = await EmitirAsync(_tceY, _usuarioY);

        deX.Should().NotBe(deY);
        await using (var contexto = Contexto(_tceX.Id, _usuarioX))
        {
            var ids = await contexto.CredencialesAcceso.IgnoreQueryFilters().Select(c => c.Id).ToListAsync();
            ids.Should().Equal([deX], "RLS, sin el filtro de EF: solo la propia");
            (await contexto.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM \"CredencialesAcceso\"").SingleAsync())
                .Should().Be(1);
        }

        await using (var contexto = Contexto(_tceY.Id, _usuarioY))
            (await contexto.CredencialesAcceso.IgnoreQueryFilters().Select(c => c.Id).ToListAsync())
                .Should().Equal([deY], "el otro Tenant TCE ve solo la suya, aunque el Trabajador sea el mismo");

        await using (var contexto = Contexto(_tecT.Id, _usuarioT))
            (await contexto.CredencialesAcceso.IgnoreQueryFilters().CountAsync()).Should().Be(0,
                "ni siquiera el Tenant TEC del Trabajador ve las credenciales que otros emiten sobre él");
    }

    [Fact]
    public async Task Una_segunda_Emitida_del_mismo_Trabajador_en_el_mismo_Tenant_choca_con_el_indice_unico_parcial()
    {
        await EmitirAsync(_tceX, _usuarioX);

        var segunda = async () => await EmitirAsync(_tceX, _usuarioX);

        (await segunda.Should().ThrowAsync<DbUpdateException>()).Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task El_indice_solo_limita_las_Emitidas_tras_revocar_se_emite_otra_y_el_otro_Tenant_no_choca()
    {
        var primera = await EmitirAsync(_tceX, _usuarioX);
        await using (var contexto = Contexto(_tceX.Id, _usuarioX))
        {
            var credencial = await contexto.CredencialesAcceso.SingleAsync(c => c.Id == primera);
            credencial.Revocar(MotivoRevocacionCredencial.PerdidaOExtravio, _usuarioX, DateTime.UtcNow);
            await contexto.SaveChangesAsync();
        }

        var segunda = await EmitirAsync(_tceX, _usuarioX);
        var deY = await EmitirAsync(_tceY, _usuarioY);

        await using var lectura = Contexto(_tceX.Id, _usuarioX);
        var propias = await lectura.CredencialesAcceso.IgnoreQueryFilters().OrderBy(c => c.EmitidaEnUtc).ToListAsync();
        propias.Select(c => (c.Id, c.Estado)).Should().Equal(
            (primera, EstadoCredencialAcceso.Revocada), (segunda, EstadoCredencialAcceso.Emitida));
        deY.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public async Task Regenerar_persiste_la_serie_nueva_con_el_token_de_version()
    {
        var id = await EmitirAsync(_tceX, _usuarioX);

        await using (var contexto = Contexto(_tceX.Id, _usuarioX))
        {
            var credencial = await contexto.CredencialesAcceso.SingleAsync(c => c.Id == id);
            credencial.Regenerar("Ana María Pérez", null, _usuarioX, DateTime.UtcNow);
            await contexto.SaveChangesAsync();
        }

        await using var lectura = Contexto(_tceX.Id, _usuarioX);
        var guardada = await lectura.CredencialesAcceso.SingleAsync(c => c.Id == id);
        (guardada.Serie, guardada.Estado, guardada.SujetoNombre).Should().Be((2, EstadoCredencialAcceso.Emitida, "Ana María Pérez"));
    }

    [Fact]
    public async Task Los_CHECK_impiden_origen_en_el_propio_Tenant_serie_cero_y_revocacion_sin_motivo()
    {
        await using var propietario = new NpgsqlConnection(_cadena);
        await propietario.OpenAsync();

        async Task<string> Insertar(Guid tenantOrigen, int serie, string estado, string? motivo, bool conRevocadaEn)
        {
            await using var orden = new NpgsqlCommand(
                """
                INSERT INTO "CredencialesAcceso"
                    ("Id", "TenantId", "TenantOrigenId", "TrabajadorOrigenId", "SujetoNombre", "Serie", "Estado", "MotivoRevocacion",
                     "EmitidaPorUsuarioId", "EmitidaEnUtc", "RevocadaEnUtc", "RevocadaPorUsuarioId", "CreadoEnUtc", "Version", "EstaEliminado")
                VALUES (gen_random_uuid(), @tenant, @origen, gen_random_uuid(), 'Ana', @serie, @estado, @motivo,
                        gen_random_uuid(), now(), @revocadaEn, @revocadaPor, now(), gen_random_uuid(), false);
                """, propietario);
            orden.Parameters.AddWithValue("tenant", _tceX.Id);
            orden.Parameters.AddWithValue("origen", tenantOrigen);
            orden.Parameters.AddWithValue("serie", serie);
            orden.Parameters.AddWithValue("estado", estado);
            orden.Parameters.AddWithValue("motivo", (object?)motivo ?? DBNull.Value);
            orden.Parameters.AddWithValue("revocadaEn", conRevocadaEn ? DateTime.UtcNow : DBNull.Value);
            orden.Parameters.AddWithValue("revocadaPor", conRevocadaEn ? Guid.NewGuid() : DBNull.Value);
            var fallo = await FluentActions.Awaiting(() => orden.ExecuteNonQueryAsync()).Should().ThrowAsync<PostgresException>();
            fallo.Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
            return fallo.Which.ConstraintName!;
        }

        (await Insertar(_tceX.Id, 1, "Emitida", null, false)).Should().Be("CK_CredencialesAcceso_OrigenEnOtroTenant");
        (await Insertar(_tecT.Id, 0, "Emitida", null, false)).Should().Be("CK_CredencialesAcceso_SerieDesdeUno");
        (await Insertar(_tecT.Id, 1, "Revocada", null, true)).Should().Be("CK_CredencialesAcceso_RevocacionCoherente",
            "revocar exige el motivo cerrado");
        (await Insertar(_tecT.Id, 1, "Emitida", "UsoIndebido", false)).Should().Be("CK_CredencialesAcceso_RevocacionCoherente",
            "una Emitida no lleva motivo de revocación");
        (await Insertar(_tecT.Id, 1, "Revocada", "Inventado", true)).Should().Be("CK_CredencialesAcceso_MotivoRevocacionCerrado",
            "el motivo es una lista cerrada, nunca texto libre");
        (await Insertar(_tecT.Id, 1, "Rota", null, false)).Should().Be("CK_CredencialesAcceso_EstadoValido");
    }

    private async Task<Guid> EmitirAsync(Tenant tce, Guid usuario)
    {
        await using var contexto = Contexto(tce.Id, usuario);
        var credencial = CredencialAcceso.Emitir(
            new CredencialAcceso.Sujeto(_tecT.Id, _trabajador, "Ana Pérez", "12345678Z"), usuario, DateTime.UtcNow);
        contexto.CredencialesAcceso.Add(credencial);
        await contexto.SaveChangesAsync();
        return credencial.Id;
    }

    private static void Cuenta(CaeManagerDbContext contexto, Guid id, Guid tenantId, Guid rolId)
    {
        contexto.Users.Add(new ApplicationUser
        {
            Id = id,
            TenantId = tenantId,
            UserName = $"{id:N}@caemanager.local",
            NormalizedUserName = $"{id:N}@CAEMANAGER.LOCAL",
            Email = $"{id:N}@caemanager.local",
            NormalizedEmail = $"{id:N}@CAEMANAGER.LOCAL",
            NombreCompleto = "Usuario",
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString(),
            ConcurrencyStamp = Guid.NewGuid().ToString()
        });
        contexto.UserRoles.Add(new IdentityUserRole<Guid> { UserId = id, RoleId = rolId });
    }

    // Sin usuario: identidad propietaria (siembra y lectura de control). Con usuario: cae_app_runtime con contexto firmado.
    private CaeManagerDbContext Contexto(Guid tenantId, Guid? usuario = null)
    {
        ICurrentUserService? usuarioActual = usuario is { } id ? new CurrentUserServiceFalso(id, Roles.GestorCae, tenantOrigenId: tenantId) : null;
        var tenant = new TenantActualAmbiental { TenantId = tenantId };
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>().UseNpgsql(
            usuarioActual is null ? _cadena : BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadena),
            n => n.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"));
        opciones.AddInterceptors(new TenantSelladoInterceptor(tenant));
        if (usuarioActual is not null)
            opciones.AddInterceptors(new TenantRlsConnectionInterceptor(tenant, new SinTenantSeleccionado(),
                usuarioActual, BaseDatosPostgresDePruebas.FirmanteContextoRls),
                new ConcurrenciaOptimistaInterceptor());
        return new(opciones.Options, new EphemeralDataProtectionProvider(), tenant);
    }

    private sealed class SinTenantSeleccionado : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }
}
