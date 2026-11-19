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
/// <c>INSERT</c>, <c>UPDATE</c> y <c>DELETE</c> de tabla entera sobre <c>Tenants</c>: sin él, los tests de rechazo
/// bajo runtime pasarían a ver el <c>UPDATE</c>, el <c>INSERT</c> o el <c>DELETE</c> aplicado y fallarían. El
/// mensaje que comprueban es el del trigger, no el de un <c>GRANT</c>, porque los dos comparten el SQLSTATE
/// <c>42501</c>. Cada rol se adopta con <c>SET ROLE</c> desde la conexión de pruebas, que es superusuaria, como
/// en <c>EscrituraAprovisionamientoEnLaCapaDeDatosTests</c>; la función es <c>SECURITY INVOKER</c>, así que
/// <c>current_user</c> es el rol adoptado.
/// </para>
///
/// <para>
/// Mutaciones razonadas (la evidencia de mutación real se declara como hueco en la PR): quitar <c>"Id"</c> de
/// <c>UPDATE OF</c> deja pasar el cambio de clave de una fila marcada y hace fallar
/// <see cref="Bajo_runtime_cambiar_el_Id_de_una_fila_marcada_lo_rechaza_el_trigger"/>; quitar <c>OR DELETE</c>
/// deja pasar el borrado y hace fallar
/// <see cref="Bajo_runtime_borrar_una_fila_marcada_lo_rechaza_el_trigger_y_una_sin_marcar_se_borra"/>. Las filas
/// de esos dos tests son copias insertadas por SQL, sin filas hijas, para que bajo la mutación la operación se
/// aplique de verdad y no la pare una clave ajena.
/// </para>
///
/// <para>
/// <c>cae_app_aprovisionamiento</c> no tiene <c>DELETE</c> sobre <c>Tenants</c> ni <c>UPDATE</c> de
/// <c>"Id"</c>: esas vías las para el <c>GRANT</c>, no el trigger, y lo que se comprueba es el catálogo
/// (<see cref="Aprovisionamiento_no_borra_Tenants_ni_cambia_su_Id_porque_no_tiene_el_GRANT"/>).
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
        (await LeerMarcadorAsync(tenantId)).Should().BeTrue("cambiar el valor de la columna, también a falso, es del escritor autorizado");
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
            .Should().Be(1, "control positivo: un UPDATE que no nombra el marcador ni la clave no dispara el trigger");
        (await EjecutarAsync(runtime, "UPDATE public.\"Tenants\" SET \"EsParticipanteTec\" = false WHERE \"Id\" = @id;", nuevo))
            .Should().Be(1, "reescribir el mismo valor (EF con la entidad entera marcada) no es un cambio");
        (await LeerMarcadorAsync(nuevo)).Should().BeFalse();
    }

    [Fact]
    public async Task Bajo_runtime_cambiar_el_Id_de_una_fila_marcada_lo_rechaza_el_trigger()
    {
        var plantilla = await CrearTenantAsync();
        var marcada = await CrearCopiaAsync(plantilla, marcador: true);
        var sinMarcar = await CrearCopiaAsync(plantilla, marcador: false);
        var otroId = Guid.NewGuid();
        var otroIdSinMarcar = Guid.NewGuid();
        await using var runtime = await AbrirComoAsync("cae_app_runtime");

        // Intercambiar la clave con una copia en falso quitaría el marcador sin nombrar la columna.
        var cambiar = () => CambiarIdAsync(runtime, marcada, otroId);

        await DebeRechazarloElTriggerAsync(cambiar, "UPDATE");
        (await ContarTenantAsync(marcada)).Should().Be(1, "el UPDATE rechazado deja la fila con su clave");
        (await ContarTenantAsync(otroId)).Should().Be(0);
        (await LeerMarcadorAsync(marcada)).Should().BeTrue();

        (await CambiarIdAsync(runtime, sinMarcar, otroIdSinMarcar))
            .Should().Be(1, "control positivo: la clave de una fila sin marcar sigue cambiando, y nada más la para");
        (await ContarTenantAsync(otroIdSinMarcar)).Should().Be(1);
    }

    [Fact]
    public async Task Bajo_runtime_borrar_una_fila_marcada_lo_rechaza_el_trigger_y_una_sin_marcar_se_borra()
    {
        var plantilla = await CrearTenantAsync();
        var marcada = await CrearCopiaAsync(plantilla, marcador: true);
        var sinMarcar = await CrearCopiaAsync(plantilla, marcador: false);
        await using var runtime = await AbrirComoAsync("cae_app_runtime");

        // Borrar y reinsertar en falso quitaría el marcador sin nombrar la columna.
        var borrar = () => EjecutarAsync(runtime, "DELETE FROM public.\"Tenants\" WHERE \"Id\" = @id;", marcada);

        await DebeRechazarloElTriggerAsync(borrar, "DELETE");
        (await ContarTenantAsync(marcada)).Should().Be(1, "el DELETE rechazado no borra la fila");
        (await LeerMarcadorAsync(marcada)).Should().BeTrue();

        (await EjecutarAsync(runtime, "DELETE FROM public.\"Tenants\" WHERE \"Id\" = @id;", sinMarcar))
            .Should().Be(1, "control positivo: una fila sin marcar se sigue borrando");
        (await ContarTenantAsync(sinMarcar)).Should().Be(0);
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
    public async Task El_superusuario_que_migra_pone_el_marcador()
    {
        var tenantId = await CrearTenantAsync();
        await using var superusuario = await AbrirComoPropietarioAsync();

        (await EsSuperusuarioAsync(superusuario))
            .Should().BeTrue("la lista blanca admite a un superusuario, no al propietario de la tabla por serlo");
        (await EjecutarAsync(superusuario, "UPDATE public.\"Tenants\" SET \"EsParticipanteTec\" = true WHERE \"Id\" = @id;", tenantId))
            .Should().Be(1);
        (await LeerMarcadorAsync(tenantId)).Should().BeTrue();
    }

    // ── Los que no llegan al trigger: los para el GRANT ─────────────────────

    [Fact]
    public async Task Bajo_soporte_el_marcador_no_se_escribe_porque_lo_para_el_GRANT_y_no_el_trigger()
    {
        var tenantId = await CrearTenantAsync();
        await using var soporte = await AbrirComoAsync("cae_app_soporte");

        // Soporte solo tiene SELECT sobre Tenants: el GRANT lo rechaza antes de que el trigger se dispare.
        var poner = () => EjecutarAsync(soporte, "UPDATE public.\"Tenants\" SET \"EsParticipanteTec\" = true WHERE \"Id\" = @id;", tenantId);

        var error = (await poner.Should().ThrowAsync<PostgresException>()).Which;
        error.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        error.MessageText.Should().NotContain(MarcaDelTrigger, "el mensaje es el del GRANT de tabla, no el del trigger");
        (await LeerMarcadorAsync(tenantId)).Should().BeFalse();
        (await TienePrivilegioDeColumnaAsync("cae_app_soporte", "EsParticipanteTec", "UPDATE"))
            .Should().BeFalse("soporte no tiene UPDATE sobre la columna, ni de tabla ni de columna");
    }

    [Fact]
    public async Task Aprovisionamiento_no_borra_Tenants_ni_cambia_su_Id_porque_no_tiene_el_GRANT()
    {
        (await TienePrivilegioDeTablaAsync("cae_app_aprovisionamiento", "DELETE"))
            .Should().BeFalse("el borrado de una fila marcada bajo aprovisionamiento lo para el GRANT, no el trigger");
        (await TienePrivilegioDeColumnaAsync("cae_app_aprovisionamiento", "Id", "UPDATE"))
            .Should().BeFalse("el cambio de clave bajo aprovisionamiento lo para el GRANT, no el trigger");
        (await TienePrivilegioDeColumnaAsync("cae_app_aprovisionamiento", "EsParticipanteTec", "UPDATE"))
            .Should().BeTrue("control positivo: la consulta de catálogo ve el GRANT de columna que sí tiene");
    }

    private static async Task DebeRechazarloElTriggerAsync(Func<Task<int>> accion, string? operacion = null)
    {
        var error = (await accion.Should().ThrowAsync<PostgresException>()).Which;
        error.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        error.MessageText.Should().Contain(MarcaDelTrigger, "lo rechaza el trigger, no un GRANT");
        error.MessageText.Should().Contain("cae_app_runtime", "el mensaje nombra el rol que intentó escribir");
        if (operacion is not null)
            error.MessageText.Should().Contain($"({operacion})", "el mensaje nombra la operación rechazada");
    }

    private async Task<Guid> CrearCopiaAsync(Guid plantilla, bool marcador)
    {
        var nuevo = Guid.NewGuid();
        await using var superusuario = await AbrirComoPropietarioAsync();
        (await InsertarCopiaAsync(superusuario, plantilla, nuevo, marcador)).Should().Be(1);
        return nuevo;
    }

    private static async Task<int> CambiarIdAsync(NpgsqlConnection conexion, Guid id, Guid nuevo)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = "UPDATE public.\"Tenants\" SET \"Id\" = @nuevo WHERE \"Id\" = @id;";
        comando.Parameters.AddWithValue("id", id);
        comando.Parameters.AddWithValue("nuevo", nuevo);
        return await comando.ExecuteNonQueryAsync();
    }

    private static async Task<bool> EsSuperusuarioAsync(NpgsqlConnection conexion)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = "SELECT rolsuper FROM pg_catalog.pg_roles WHERE rolname = current_user;";
        return (bool)(await comando.ExecuteScalarAsync())!;
    }

    private async Task<bool> TienePrivilegioDeTablaAsync(string rol, string privilegio)
    {
        await using var conexion = await AbrirComoPropietarioAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = "SELECT has_table_privilege(@rol::name, 'public.\"Tenants\"', @privilegio);";
        comando.Parameters.AddWithValue("rol", rol);
        comando.Parameters.AddWithValue("privilegio", privilegio);
        return (bool)(await comando.ExecuteScalarAsync())!;
    }

    private async Task<bool> TienePrivilegioDeColumnaAsync(string rol, string columna, string privilegio)
    {
        await using var conexion = await AbrirComoPropietarioAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = "SELECT has_column_privilege(@rol::name, 'public.\"Tenants\"', @columna, @privilegio);";
        comando.Parameters.AddWithValue("rol", rol);
        comando.Parameters.AddWithValue("columna", columna);
        comando.Parameters.AddWithValue("privilegio", privilegio);
        return (bool)(await comando.ExecuteScalarAsync())!;
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
