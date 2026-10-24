using CaeManager.Application.Common;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Auditing;
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

namespace CaeManager.IntegrationTests.EstadoMaterializado;

/// <summary>
/// Las cinco tablas del estado materializado (PR 2 del diseño de 2026-10-24) bajo <c>cae_app_runtime</c> con contexto
/// firmado. Lo que prueba la base: RLS forzada con la política de aislamiento en las cinco; el runtime y el
/// aprovisionamiento insertan, borran y actualizan solo las columnas que el recálculo rehace (nunca Id, TenantId ni la
/// clave natural), y Soporte TALVEG solo lee; ningún rol de la aplicación salta RLS; la fila de un Tenant es invisible
/// desde otro y el <c>WITH CHECK</c> rechaza escribirla con otro TenantId; y el índice único deja un solo par.
/// </summary>
public class EstadoMaterializadoBajoRuntimeTests : IAsyncLifetime
{
    private static readonly string[] Tablas =
        ["EstadosRequisito", "EstadosPlataforma", "EstadosEntidad", "VersionesEstadoTenant", "RecalculosPendientes"];

    private static readonly Dictionary<string, string[]> ColumnasActualizables = new()
    {
        ["EstadosRequisito"] =
        [
            "BloqueaAcceso", "CalculadoEnUtc", "CuentaParaCumplimiento", "DocumentoEfectivoId", "EstadoPlataforma",
            "DiaCalculo", "EstadoTalveg", "ProximoCambioEl", "VencimientoEfectivo", "VersionCalculo",
        ],
        ["EstadosPlataforma"] =
        [
            "CalculadoEnUtc", "DiaCalculo", "Estado", "EstadoVigencia", "FechaVencimientoEnPlataforma", "ProximoCambioEl", "VersionCalculo",
        ],
        ["EstadosEntidad"] =
        [
            "AlDia", "CalculadoEnUtc", "DiaCalculo", "Estado", "EstadoPlataforma", "Faltan", "PendientesEnPlataforma", "Proximos",
            "ProximoCambioEl", "Requeridos", "SinConfirmar", "Urgentes", "Vencidos", "VencidosSinBloqueo", "VersionCalculo",
        ],
        ["VersionesEstadoTenant"] = ["ActualizadoEnUtc", "UltimoDiaProcesado", "Version"],
        ["RecalculosPendientes"] = ["IntentoEnUtc", "Intentos"],
    };

    private readonly string _cadena = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Tenant _propietario = new("Tenant propietario del estado");
    private readonly Tenant _ajeno = new("Tenant propietario ajeno");
    private readonly Guid _usuario = Guid.NewGuid();
    private readonly Guid _usuarioAjeno = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var contexto = Contexto(_propietario.Id);
        await contexto.Database.MigrateAsync();
        contexto.Tenants.AddRange(_propietario, _ajeno);
        var roles = await contexto.Roles.ToDictionaryAsync(r => r.Name!, r => r.Id);
        Cuenta(contexto, _usuario, _propietario.Id, roles[Roles.Administrador]);
        Cuenta(contexto, _usuarioAjeno, _ajeno.Id, roles[Roles.Administrador]);
        await contexto.SaveChangesAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadena);

    [Fact]
    public async Task Las_cinco_tablas_tienen_RLS_forzada_con_la_politica_de_aislamiento_por_Tenant()
    {
        await using var propietario = new NpgsqlConnection(_cadena);
        await propietario.OpenAsync();

        foreach (var tabla in Tablas)
        {
            await using var orden = new NpgsqlCommand(
                """
                SELECT c.relrowsecurity, c.relforcerowsecurity,
                       (SELECT array_agg(p.polname::text ORDER BY p.polname) FROM pg_policy p WHERE p.polrelid = c.oid),
                       (SELECT pg_get_expr(p.polqual, p.polrelid) FROM pg_policy p WHERE p.polrelid = c.oid),
                       (SELECT pg_get_expr(p.polwithcheck, p.polrelid) FROM pg_policy p WHERE p.polrelid = c.oid)
                FROM pg_class c WHERE c.oid = format('public.%I', @tabla)::regclass;
                """, propietario);
            orden.Parameters.AddWithValue("tabla", tabla);
            await using var lector = await orden.ExecuteReaderAsync();
            (await lector.ReadAsync()).Should().BeTrue(tabla);

            lector.GetBoolean(0).Should().BeTrue($"{tabla}: ENABLE ROW LEVEL SECURITY");
            lector.GetBoolean(1).Should().BeTrue($"{tabla}: FORCE ROW LEVEL SECURITY");
            lector.GetFieldValue<string[]>(2).Should().Equal(["aislamiento_tenant"], tabla);
            foreach (var expresion in new[] { lector.GetString(3), lector.GetString(4) })
                expresion.Should().Contain("\"TenantId\"").And.Contain("app.tenant_id", $"{tabla}: USING y WITH CHECK por el Tenant activo");
        }
    }

    [Fact]
    public async Task Runtime_y_aprovisionamiento_escriben_solo_las_columnas_del_recalculo_y_Soporte_solo_lee()
    {
        await using var propietario = new NpgsqlConnection(_cadena);
        await propietario.OpenAsync();

        async Task<bool> Tabla(string rol, string tabla, string privilegio)
        {
            await using var orden = new NpgsqlCommand(
                "SELECT has_table_privilege(@rol, format('public.%I', @tabla), @privilegio)", propietario);
            orden.Parameters.AddWithValue("rol", rol);
            orden.Parameters.AddWithValue("tabla", tabla);
            orden.Parameters.AddWithValue("privilegio", privilegio);
            return (bool)(await orden.ExecuteScalarAsync())!;
        }

        async Task<List<string>> Actualizables(string rol, string tabla)
        {
            await using var orden = new NpgsqlCommand(
                """
                SELECT column_name::text FROM information_schema.columns
                WHERE table_schema = 'public' AND table_name = @tabla
                  AND has_column_privilege(@rol, format('public.%I', @tabla), column_name, 'UPDATE')
                ORDER BY 1
                """, propietario);
            orden.Parameters.AddWithValue("rol", rol);
            orden.Parameters.AddWithValue("tabla", tabla);
            var columnas = new List<string>();
            await using var lector = await orden.ExecuteReaderAsync();
            while (await lector.ReadAsync())
                columnas.Add(lector.GetString(0));
            return columnas;
        }

        foreach (var tabla in Tablas)
        {
            foreach (var rol in new[] { "cae_app_runtime", "cae_app_aprovisionamiento" })
            {
                foreach (var privilegio in new[] { "SELECT", "INSERT", "DELETE" })
                    (await Tabla(rol, tabla, privilegio)).Should().BeTrue($"{rol} {privilegio} en {tabla}");
                foreach (var privilegio in new[] { "UPDATE", "TRUNCATE", "REFERENCES", "TRIGGER" })
                    (await Tabla(rol, tabla, privilegio)).Should().BeFalse($"{rol} {privilegio} en {tabla}: UPDATE solo por columna");
                (await Actualizables(rol, tabla)).Should().Equal(
                    ColumnasActualizables[tabla].Order(StringComparer.Ordinal),
                    $"{rol} en {tabla}: nunca Id, TenantId ni la clave natural de la fila");
            }

            (await Tabla("cae_app_soporte", tabla, "SELECT")).Should().BeTrue($"Soporte lee {tabla}");
            foreach (var privilegio in new[] { "INSERT", "UPDATE", "DELETE", "TRUNCATE" })
                (await Tabla("cae_app_soporte", tabla, privilegio)).Should().BeFalse($"Soporte {privilegio} en {tabla}");
            (await Actualizables("cae_app_soporte", tabla)).Should().BeEmpty($"Soporte no actualiza ninguna columna de {tabla}");
        }
    }

    [Fact]
    public async Task Ningun_rol_de_la_aplicacion_salta_RLS()
    {
        await using var propietario = new NpgsqlConnection(_cadena);
        await propietario.OpenAsync();
        await using var orden = new NpgsqlCommand(
            """
            SELECT rolname::text, rolbypassrls, rolsuper FROM pg_roles
            WHERE rolname IN ('cae_app_runtime', 'cae_app_aprovisionamiento', 'cae_app_soporte') ORDER BY 1
            """, propietario);
        var roles = new List<(string Rol, bool Bypass, bool Super)>();
        await using var lector = await orden.ExecuteReaderAsync();
        while (await lector.ReadAsync())
            roles.Add((lector.GetString(0), lector.GetBoolean(1), lector.GetBoolean(2)));

        roles.Select(r => r.Rol).Should().Equal(["cae_app_aprovisionamiento", "cae_app_runtime", "cae_app_soporte"],
            "control: los tres roles existen, así que la comprobación de abajo no mira una lista vacía");
        roles.Should().OnlyContain(r => !r.Bypass && !r.Super, "con BYPASSRLS o superusuario, la RLS forzada no protegería nada");
    }

    [Fact]
    public async Task La_fila_de_un_Tenant_es_invisible_desde_otro_y_no_se_puede_escribir_con_otro_TenantId()
    {
        await using (var contexto = Contexto(_propietario.Id, _usuario))
        {
            (await contexto.Database.SqlQueryRaw<string>("SELECT current_user AS \"Value\"").SingleAsync()).Should().Be("cae_app_runtime");
            await InsertarVersionAsync(contexto, _propietario.Id);
            (await ContarVersionesAsync(contexto)).Should().Be(1, "control positivo: el propio Tenant ve su fila");
            (await contexto.Database.ExecuteSqlRawAsync(
                "UPDATE \"VersionesEstadoTenant\" SET \"Version\" = \"Version\" + 1, \"ActualizadoEnUtc\" = now()"))
                .Should().Be(1, "control positivo: el runtime sube la versión");
        }

        await using (var ajeno = Contexto(_ajeno.Id, _usuarioAjeno))
        {
            (await ContarVersionesAsync(ajeno)).Should().Be(0, "RLS oculta la fila del otro Tenant sin ayuda del filtro de EF");
            (await ajeno.Database.ExecuteSqlRawAsync("DELETE FROM \"VersionesEstadoTenant\""))
                .Should().Be(0, "y no la alcanza para borrarla");

            var conOtroTenant = () => InsertarVersionAsync(ajeno, _propietario.Id);
            (await conOtroTenant.Should().ThrowAsync<PostgresException>())
                .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege, "el WITH CHECK rechaza una fila de otro Tenant");
        }

        await using (var contexto = Contexto(_propietario.Id, _usuario))
        {
            var cambiarDeTenant = () => contexto.Database.ExecuteSqlRawAsync(
                "UPDATE \"VersionesEstadoTenant\" SET \"TenantId\" = {0}", _ajeno.Id);
            // 42501 también lo daría el WITH CHECK; el mensaje distingue que falta el permiso de columna.
            var error = (await cambiarDeTenant.Should().ThrowAsync<PostgresException>()).Which;
            error.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege, "TenantId no es actualizable");
            error.MessageText.Should().Contain("permission denied", "lo rechaza el permiso por columna, antes que la política");
            (await ContarVersionesAsync(contexto)).Should().Be(1, "la fila sigue en su Tenant");
        }
    }

    // Una fila con Id nuevo y la misma clave natural: solo el índice único puede rechazarla.
    public static TheoryData<string, string> InsercionesConLaMismaClave => new()
    {
        {
            "un par Centro × titular × Tipo",
            """
            INSERT INTO "EstadosRequisito" ("Id", "TenantId", "CentroId", "AmbitoTitular", "TitularId", "TipoDocumentoId",
                "EstadoTalveg", "BloqueaAcceso", "CuentaParaCumplimiento", "DiaCalculo", "CalculadoEnUtc", "VersionCalculo")
            VALUES (gen_random_uuid(), {0}, {1}, 0, {2}, {3}, 1, true, true, {4}, now(), 1)
            """
        },
        {
            "una acreditación Centro × canal × titular × Tipo",
            """
            INSERT INTO "EstadosPlataforma" ("Id", "TenantId", "CentroId", "CanalGestionDocumentalId", "AmbitoTitular",
                "TitularId", "TipoDocumentoId", "Estado", "EstadoVigencia", "DiaCalculo", "CalculadoEnUtc", "VersionCalculo")
            VALUES (gen_random_uuid(), {0}, {1}, {1}, 0, {2}, {3}, 2, 1, {4}, now(), 1)
            """
        },
        {
            "una entidad",
            """
            INSERT INTO "EstadosEntidad" ("Id", "TenantId", "Ambito", "EntidadId", "Estado", "Vencidos", "VencidosSinBloqueo",
                "Urgentes", "Proximos", "Faltan", "SinConfirmar", "PendientesEnPlataforma", "Requeridos", "AlDia",
                "DiaCalculo", "CalculadoEnUtc", "VersionCalculo")
            VALUES (gen_random_uuid(), {0}, 1, {2}, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, {4}, now(), 1)
            """
        },
        {
            "un Tenant",
            """
            INSERT INTO "VersionesEstadoTenant" ("Id", "TenantId", "Version", "ActualizadoEnUtc")
            SELECT gen_random_uuid(), {0}, 1, now() WHERE {1}::uuid IS NOT NULL AND {2}::uuid IS NOT NULL AND {3}::uuid IS NOT NULL AND {4}::date IS NOT NULL
            """
        },
    };

    [Theory]
    [MemberData(nameof(InsercionesConLaMismaClave))]
    public async Task El_indice_unico_deja_una_sola_fila_por_clave_natural(string clave, string insercion)
    {
        await using var contexto = Contexto(_propietario.Id, _usuario);
        var (centro, titular, tipo) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        Task<int> Insertar() => contexto.Database.ExecuteSqlRawAsync(insercion, _propietario.Id, centro, titular, tipo, DiaDeNegocio.Hoy());

        (await Insertar()).Should().Be(1, $"control positivo: el runtime inserta {clave}");
        var duplicado = Insertar;
        (await duplicado.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation, $"{clave} es una sola fila");
    }

    [Fact]
    public async Task Las_filas_del_estado_no_se_auditan_y_la_accion_si()
    {
        await using (var contexto = ContextoAuditado())
        {
            var version = (CaeManager.Domain.EstadoMaterializado.VersionEstadoTenant)Activator.CreateInstance(
                typeof(CaeManager.Domain.EstadoMaterializado.VersionEstadoTenant), nonPublic: true)!;
            contexto.Add(version);
            contexto.Entry(version).Property(v => v.Version).CurrentValue = 1;
            contexto.Entry(version).Property(v => v.ActualizadoEnUtc).CurrentValue = DateTime.UtcNow;
            contexto.Empresas.Add(new Empresa("Empresa de control", "B10380194"));
            await contexto.SaveChangesAsync();
        }

        await using var lectura = Contexto(_propietario.Id);
        var tipos = await lectura.RegistrosAuditoria.IgnoreQueryFilters()
            .Where(r => r.Accion == "Creado").Select(r => r.EntidadTipo).ToListAsync();
        tipos.Should().Contain(nameof(Empresa), "control positivo: el mismo guardado audita la acción");
        tipos.Should().NotContain(nameof(CaeManager.Domain.EstadoMaterializado.VersionEstadoTenant),
            "el estado materializado es derivado: auditar cada fila recalculada inundaría la auditoría");
    }

    private static Task<int> InsertarVersionAsync(CaeManagerDbContext contexto, Guid tenantId) =>
        contexto.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"VersionesEstadoTenant\" (\"Id\", \"TenantId\", \"Version\", \"ActualizadoEnUtc\") VALUES (gen_random_uuid(), {0}, 1, now())",
            tenantId);

    private static Task<int> ContarVersionesAsync(CaeManagerDbContext contexto) =>
        contexto.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM \"VersionesEstadoTenant\"").SingleAsync();

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
            NombreCompleto = "Administrador",
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString(),
            ConcurrencyStamp = Guid.NewGuid().ToString()
        });
        contexto.UserRoles.Add(new IdentityUserRole<Guid> { UserId = id, RoleId = rolId });
    }

    // Sin usuario: identidad propietaria (migración y siembra). Con usuario: cae_app_runtime con contexto firmado.
    private CaeManagerDbContext Contexto(Guid tenantId, Guid? usuario = null)
    {
        var tenant = new TenantActualAmbiental { TenantId = tenantId };
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>().UseNpgsql(
            usuario is null ? _cadena : BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadena),
            n => n.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"));
        opciones.AddInterceptors(new TenantSelladoInterceptor(tenant));
        if (usuario is not null)
            opciones.AddInterceptors(new TenantRlsConnectionInterceptor(tenant, new SinTenantSeleccionado(),
                new CurrentUserServiceFalso(usuario.Value, Roles.Administrador, tenantOrigenId: tenantId), BaseDatosPostgresDePruebas.FirmanteContextoRls),
                new ConcurrenciaOptimistaInterceptor());
        return new(opciones.Options, new EphemeralDataProtectionProvider(), tenant);
    }

    // Identidad propietaria con la auditoría delante del sellado, en el orden de producción.
    private CaeManagerDbContext ContextoAuditado()
    {
        var tenant = new TenantActualAmbiental { TenantId = _propietario.Id };
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadena, n => n.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new AuditoriaInterceptor(new ActorAuditoriaFalso(ActorAuditoria.Normal(_usuario))), new TenantSelladoInterceptor(tenant))
            .Options;
        return new(opciones, new EphemeralDataProtectionProvider(), tenant);
    }

    private sealed class ActorAuditoriaFalso(ActorAuditoria actor) : IActorAuditoria
    {
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(actor);

        public ActorAuditoria? ObtenerSiYaEstaResuelto() => actor;
    }

    private sealed class SinTenantSeleccionado : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }
}
