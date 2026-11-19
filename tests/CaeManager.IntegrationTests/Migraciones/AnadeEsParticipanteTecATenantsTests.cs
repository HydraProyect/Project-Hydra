using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Migraciones;

/// <summary>
/// <c>Tenants.EsParticipanteTec</c> (D3) contra PostgreSQL real. La columna nace NOT NULL y con DEFAULT false, sin
/// backfill: es lo que deja a la ranura anterior de un relevo azul/verde insertar Tenants sin conocerla, y lo que
/// garantiza que ningún Tenant existente queda marcado como participante TEC al migrar.
///
/// <para>
/// El trigger <c>proteger_es_participante_tec</c> es la única barrera frente a <c>cae_app_runtime</c>, que tiene
/// <c>INSERT</c> y <c>UPDATE</c> de tabla entera sobre <c>Tenants</c>: sin él, los tests de rechazo bajo runtime
/// pasarían a ver el <c>UPDATE</c> o el <c>INSERT</c> aplicado y fallarían. El mensaje que comprueban es el del
/// trigger, no el de un <c>GRANT</c>, porque los dos comparten el SQLSTATE <c>42501</c>. Cada rol se adopta con
/// <c>SET ROLE</c> desde la conexión propietaria, como en <c>EscrituraAprovisionamientoEnLaCapaDeDatosTests</c>;
/// la función es <c>SECURITY INVOKER</c>, así que <c>current_user</c> es el rol adoptado.
/// </para>
/// </summary>
public class AnadeEsParticipanteTecATenantsTests : IAsyncLifetime
{
    private const string MarcaDelTrigger = "EsParticipanteTec";

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly TenantActualAmbiental _tenantActual = new() { TenantId = Guid.NewGuid() };

    public async Task InitializeAsync()
    {
        await using var dbContext = CrearContexto();
        await dbContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync() =>
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    private CaeManagerDbContext CrearContexto()
    {
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(_tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), _tenantActual);
    }

    [Fact]
    public async Task La_columna_nace_no_nula_y_en_falso_por_defecto()
    {
        await using var contexto = CrearContexto();

        var columna = await contexto.Database
            .SqlQueryRaw<string>(
                """
                SELECT is_nullable || '|' || COALESCE(column_default, '') AS "Value" FROM information_schema.columns
                WHERE table_schema = 'public' AND table_name = 'Tenants' AND column_name = 'EsParticipanteTec'
                """)
            .SingleAsync();

        columna.Should().Be("NO|false");
    }

    // ── cae_app_runtime: lo lee, no lo pone ni lo cambia ──────────────────────

    [Fact]
    public async Task Bajo_runtime_poner_el_marcador_lo_rechaza_el_trigger()
    {
        var tenantId = await CrearTenantAsync();
        await using var runtime = await AbrirComoAsync("cae_app_runtime");

        var poner = () => EjecutarAsync(runtime, "UPDATE public.\"Tenants\" SET \"EsParticipanteTec\" = true WHERE \"Id\" = @id;", tenantId);

        await DebeRechazarloElTriggerAsync(poner);
        (await LeerMarcadorAsync(tenantId)).Should().BeFalse("el UPDATE rechazado no deja nada escrito");
    }

    [Fact]
    public async Task Bajo_runtime_quitar_el_marcador_tambien_lo_rechaza_el_trigger()
    {
        var tenantId = await CrearTenantAsync();
        await using (var propietario = await AbrirComoPropietarioAsync())
            await EjecutarAsync(propietario, "UPDATE public.\"Tenants\" SET \"EsParticipanteTec\" = true WHERE \"Id\" = @id;", tenantId);
        await using var runtime = await AbrirComoAsync("cae_app_runtime");

        var quitar = () => EjecutarAsync(runtime, "UPDATE public.\"Tenants\" SET \"EsParticipanteTec\" = false WHERE \"Id\" = @id;", tenantId);

        await DebeRechazarloElTriggerAsync(quitar);
        (await LeerMarcadorAsync(tenantId)).Should().BeTrue("cualquier cambio de valor es del escritor autorizado");
    }

    [Fact]
    public async Task Bajo_runtime_un_alta_con_el_marcador_la_rechaza_el_trigger()
    {
        var plantilla = await CrearTenantAsync();
        var nuevo = Guid.NewGuid();
        await using var runtime = await AbrirComoAsync("cae_app_runtime");

        var alta = () => InsertarCopiaAsync(runtime, plantilla, nuevo, marcador: true);

        await DebeRechazarloElTriggerAsync(alta);
        (await ContarTenantAsync(nuevo)).Should().Be(0, "el INSERT rechazado no deja la fila");
    }

    [Fact]
    public async Task Bajo_runtime_un_upsert_que_pone_el_marcador_lo_rechaza_el_trigger()
    {
        var tenantId = await CrearTenantAsync();
        await using var runtime = await AbrirComoAsync("cae_app_runtime");

        // La fila propuesta llega en falso y pasa el BEFORE INSERT; el conflicto la lleva al DO UPDATE, que
        // menciona la columna y dispara el BEFORE UPDATE OF.
        var upsert = () => EjecutarAsync(
            runtime,
            """
            INSERT INTO public."Tenants"
            SELECT (jsonb_populate_record(NULL::public."Tenants", to_jsonb(t))).* FROM public."Tenants" t WHERE t."Id" = @id
            ON CONFLICT ("Id") DO UPDATE SET "EsParticipanteTec" = true;
            """,
            tenantId);

        await DebeRechazarloElTriggerAsync(upsert);
        (await LeerMarcadorAsync(tenantId)).Should().BeFalse();
    }

    [Fact]
    public async Task Bajo_runtime_el_alta_en_falso_y_los_demas_cambios_de_un_Tenant_siguen_funcionando()
    {
        var plantilla = await CrearTenantAsync();
        var nuevo = Guid.NewGuid();
        await using var runtime = await AbrirComoAsync("cae_app_runtime");

        (await InsertarCopiaAsync(runtime, plantilla, nuevo, marcador: false))
            .Should().Be(1, "control positivo: el alta sin marcar no la toca el trigger");
        (await EjecutarAsync(runtime, "UPDATE public.\"Tenants\" SET \"Nombre\" = 'Renombrado bajo runtime' WHERE \"Id\" = @id;", nuevo))
            .Should().Be(1, "control positivo: el trigger solo mira la columna del marcador");
        (await EjecutarAsync(runtime, "UPDATE public.\"Tenants\" SET \"EsParticipanteTec\" = false WHERE \"Id\" = @id;", nuevo))
            .Should().Be(1, "reescribir el mismo valor (EF con la entidad entera marcada) no es un cambio");
        (await LeerMarcadorAsync(nuevo)).Should().BeFalse();
    }

    // ── Los escritores admitidos ─────────────────────────────────────────────

    [Fact]
    public async Task Bajo_aprovisionamiento_el_marcador_se_pone_y_se_quita()
    {
        var tenantId = await CrearTenantAsync();
        await using var aprovisionamiento = await AbrirComoAsync("cae_app_aprovisionamiento");

        (await EjecutarAsync(aprovisionamiento, "UPDATE public.\"Tenants\" SET \"EsParticipanteTec\" = true WHERE \"Id\" = @id;", tenantId))
            .Should().Be(1);
        (await LeerMarcadorAsync(tenantId)).Should().BeTrue();

        (await EjecutarAsync(aprovisionamiento, "UPDATE public.\"Tenants\" SET \"EsParticipanteTec\" = false WHERE \"Id\" = @id;", tenantId))
            .Should().Be(1);
        (await LeerMarcadorAsync(tenantId)).Should().BeFalse();
    }

    [Fact]
    public async Task El_propietario_que_migra_pone_el_marcador()
    {
        var tenantId = await CrearTenantAsync();
        await using var propietario = await AbrirComoPropietarioAsync();

        (await EjecutarAsync(propietario, "UPDATE public.\"Tenants\" SET \"EsParticipanteTec\" = true WHERE \"Id\" = @id;", tenantId))
            .Should().Be(1);
        (await LeerMarcadorAsync(tenantId)).Should().BeTrue();
    }

    [Fact]
    public async Task Bajo_soporte_el_marcador_no_se_escribe()
    {
        var tenantId = await CrearTenantAsync();
        await using var soporte = await AbrirComoAsync("cae_app_soporte");

        // Soporte solo tiene SELECT sobre Tenants: lo para el GRANT antes que el trigger.
        var poner = () => EjecutarAsync(soporte, "UPDATE public.\"Tenants\" SET \"EsParticipanteTec\" = true WHERE \"Id\" = @id;", tenantId);

        (await poner.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        (await LeerMarcadorAsync(tenantId)).Should().BeFalse();
    }

    private static async Task DebeRechazarloElTriggerAsync(Func<Task<int>> accion)
    {
        var error = (await accion.Should().ThrowAsync<PostgresException>()).Which;
        error.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        error.MessageText.Should().Contain(MarcaDelTrigger, "lo rechaza el trigger, no un GRANT");
        error.MessageText.Should().Contain("cae_app_runtime", "el mensaje nombra el rol que intentó escribir");
    }

    private async Task<Guid> CrearTenantAsync()
    {
        await using var contexto = CrearContexto();
        var tenant = new Tenant("Tenant del marcador participante TEC");
        contexto.Tenants.Add(tenant);
        await contexto.SaveChangesAsync();
        return tenant.Id;
    }

    private async Task<NpgsqlConnection> AbrirComoPropietarioAsync()
    {
        var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        return conexion;
    }

    private async Task<NpgsqlConnection> AbrirComoAsync(string rol)
    {
        var conexion = await AbrirComoPropietarioAsync();
        await using var setRol = conexion.CreateCommand();
        setRol.CommandText = $"SET ROLE {rol};";
        await setRol.ExecuteNonQueryAsync();
        return conexion;
    }

    private static async Task<int> EjecutarAsync(NpgsqlConnection conexion, string sql, Guid id)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        comando.Parameters.AddWithValue("id", id);
        return await comando.ExecuteNonQueryAsync();
    }

    /// <summary>Copia una fila existente con otro Id: no depende de qué columnas tenga hoy <c>Tenants</c>.</summary>
    private static async Task<int> InsertarCopiaAsync(NpgsqlConnection conexion, Guid plantilla, Guid nuevo, bool marcador)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = """
            INSERT INTO public."Tenants"
            SELECT (jsonb_populate_record(NULL::public."Tenants",
                    to_jsonb(t) || jsonb_build_object('Id', @nuevo, 'EsParticipanteTec', @marcador))).*
            FROM public."Tenants" t WHERE t."Id" = @plantilla;
            """;
        comando.Parameters.AddWithValue("plantilla", plantilla);
        comando.Parameters.AddWithValue("nuevo", nuevo);
        comando.Parameters.AddWithValue("marcador", marcador);
        return await comando.ExecuteNonQueryAsync();
    }

    private async Task<bool> LeerMarcadorAsync(Guid tenantId)
    {
        await using var conexion = await AbrirComoPropietarioAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = "SELECT \"EsParticipanteTec\" FROM public.\"Tenants\" WHERE \"Id\" = @id;";
        comando.Parameters.AddWithValue("id", tenantId);
        return (bool)(await comando.ExecuteScalarAsync())!;
    }

    private async Task<long> ContarTenantAsync(Guid tenantId)
    {
        await using var conexion = await AbrirComoPropietarioAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = "SELECT count(*) FROM public.\"Tenants\" WHERE \"Id\" = @id;";
        comando.Parameters.AddWithValue("id", tenantId);
        return (long)(await comando.ExecuteScalarAsync())!;
    }
}
