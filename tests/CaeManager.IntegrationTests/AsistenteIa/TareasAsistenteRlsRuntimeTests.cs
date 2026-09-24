using CaeManager.Application.Common;
using CaeManager.Domain.AsistenteIa;
using CaeManager.Domain.Auditoria;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.IntegrationTests.Arranque;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.AsistenteIa;

/// <summary>
/// RLS de las tareas del asistente de flujos con el cableado de producción
/// (<see cref="ArnesDeArranqueRuntime"/>: conexión como <c>cae_app_runtime</c> y
/// los cuatro interceptores), no con <c>SET ROLE</c> a mano: lo que se mide es
/// que las coordenadas que el interceptor fija —<c>app.tenant_id</c> y
/// <c>app.usuario_id</c>— bastan para que la base, sin ayuda de la aplicación,
/// enseñe a cada persona solo sus tareas.
///
/// <para>
/// Escenario: una Gestora CAE cuyo tenant de origen es el de su Operador CAE
/// externo trabaja en el Workspace operativo derivado del Tenant beneficiario.
/// La tarea queda sellada con el Tenant beneficiario; ni otra persona de ese
/// Tenant, ni la misma Gestora desde el tenant de su Operador CAE, la ven.
/// Las lecturas usan <c>IgnoreQueryFilters()</c> para que el filtro global de
/// tenant de EF no preste evidencia a RLS.
/// </para>
/// </summary>
public class TareasAsistenteRlsRuntimeTests
{
    private const string PermisoDenegado = "42501";

    private static readonly DateTime Ahora = new(2026, 9, 24, 10, 0, 0, DateTimeKind.Utc);

    private readonly Guid _tenantOperador = Guid.NewGuid();
    private readonly Guid _tenantBeneficiario = Guid.NewGuid();
    private readonly Guid _gestora = Guid.NewGuid();
    private readonly Guid _otraPersona = Guid.NewGuid();
    private readonly Guid _asignacionOperacion = Guid.NewGuid();

    private readonly TenantActualAmbiental _tenant = new();
    private readonly UsuarioConmutable _usuario = new();

    [Fact]
    public async Task Solo_la_persona_que_la_creo_ve_su_tarea_y_solo_en_el_Tenant_beneficiario()
    {
        await using var arnes = await CrearArnesAsync();
        var tareaId = await CrearTareaDeLaGestoraAsync(arnes);

        await using (var propietario = new NpgsqlConnection(arnes.CadenaPropietario))
        {
            await propietario.OpenAsync();
            await using var consulta = propietario.CreateCommand();
            consulta.CommandText = """SELECT "TenantId" FROM "TareasAsistente" WHERE "Id" = @id;""";
            consulta.Parameters.AddWithValue("id", tareaId);
            ((Guid)(await consulta.ExecuteScalarAsync())!).Should().Be(_tenantBeneficiario,
                "la tarea pertenece al Tenant beneficiario en el que se trabaja, no al tenant de origen de la Gestora");
        }

        ComoPersona(_gestora, _tenantBeneficiario);
        (await ContarAsync(arnes)).Should().Be((1, 1, 1), "control positivo: la Gestora ve su tarea, su turno y su paso");
        (await IdentidadEfectivaAsync(arnes)).Should().Be("cae_app_runtime");

        ComoPersona(_otraPersona, _tenantBeneficiario);
        (await ContarAsync(arnes)).Should().Be((0, 0, 0), "otra persona del mismo Tenant no ve la tarea ni sus hijas");

        ComoPersona(_gestora, _tenantOperador);
        (await ContarAsync(arnes)).Should().Be((0, 0, 0), "desde el tenant de su Operador CAE la Gestora no ve la tarea del Tenant beneficiario");

        ComoPersona(null, _tenantBeneficiario);
        (await ContarAsync(arnes)).Should().Be((0, 0, 0), "sin persona en la sesión la política por persona oculta todo");
    }

    [Fact]
    public async Task Nadie_escribe_una_tarea_a_nombre_de_otra_persona()
    {
        await using var arnes = await CrearArnesAsync();

        ComoPersona(_otraPersona, _tenantBeneficiario);
        await using var scope = arnes.Servicios.CreateAsyncScope();
        var contexto = scope.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        var ajena = new TareaAsistente(_gestora, null, _tenantOperador, TipoViaAccesoAuditoria.OperacionDelegada, _asignacionOperacion, Ahora);
        ajena.AgregarTurnoDePersona("Alta del Centro Norte", null, Ahora);
        contexto.TareasAsistente.Add(ajena);

        var excepcion = await Record.ExceptionAsync(() => contexto.SaveChangesAsync());

        excepcion.Should().BeOfType<DbUpdateException>()
            .Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be(PermisoDenegado,
                "WITH CHECK de solo_su_persona rechaza una tarea cuyo ActorRealUsuarioId no es la persona de la sesión");
    }

    [Fact]
    public async Task Nadie_anade_turnos_a_la_tarea_de_otra_persona_del_mismo_Tenant()
    {
        await using var arnes = await CrearArnesAsync();
        var tareaId = await CrearTareaDeLaGestoraAsync(arnes);

        await using var conexion = await AbrirComoRuntimeAsync(arnes, _tenantBeneficiario, _otraPersona);
        await using var insercion = conexion.CreateCommand();
        insercion.CommandText = """
            INSERT INTO "TurnosTareaAsistente" ("Id", "TareaAsistenteId", "Numero", "Autor", "TextoOriginal", "FechaUtc", "TenantId")
            VALUES (@id, @tarea, 99, 'Persona', 'Turno colado', now(), @tenant);
            """;
        insercion.Parameters.AddWithValue("id", Guid.NewGuid());
        insercion.Parameters.AddWithValue("tarea", tareaId);
        insercion.Parameters.AddWithValue("tenant", _tenantBeneficiario);

        var excepcion = await Record.ExceptionAsync(() => insercion.ExecuteNonQueryAsync());

        excepcion.Should().BeOfType<PostgresException>().Which.SqlState.Should().Be(PermisoDenegado,
            "la hija solo se escribe si la persona de la sesión ve la tarea raíz");

        await using var controlPositivo = await AbrirComoRuntimeAsync(arnes, _tenantBeneficiario, _gestora);
        await using var insercionPropia = controlPositivo.CreateCommand();
        insercionPropia.CommandText = insercion.CommandText;
        insercionPropia.Parameters.AddWithValue("id", Guid.NewGuid());
        insercionPropia.Parameters.AddWithValue("tarea", tareaId);
        insercionPropia.Parameters.AddWithValue("tenant", _tenantBeneficiario);
        (await insercionPropia.ExecuteNonQueryAsync()).Should().Be(1,
            "control positivo: la misma sentencia pasa para la persona propietaria, así que el rechazo es de la política");
    }

    [Fact]
    public async Task La_base_no_admite_un_paso_ejecutado_sin_plan_confirmado_aunque_se_salte_el_dominio()
    {
        await using var arnes = await CrearArnesAsync();
        var tareaId = await CrearTareaDeLaGestoraAsync(arnes);

        await using var conexion = await AbrirComoRuntimeAsync(arnes, _tenantBeneficiario, _gestora);
        await using var atajo = conexion.CreateCommand();
        atajo.CommandText = """
            UPDATE "PasosTareaAsistente"
            SET "Estado" = 'Ejecutado', "ConfirmadoEnUtc" = now(), "EjecutadoEnUtc" = now()
            WHERE "TareaAsistenteId" = @tarea;
            """;
        atajo.Parameters.AddWithValue("tarea", tareaId);

        var excepcion = await Record.ExceptionAsync(() => atajo.ExecuteNonQueryAsync());

        // El mensaje distingue el trigger de los CHECK del paso, que esta fila
        // cumple: tiene ConfirmadoEnUtc y EjecutadoEnUtc.
        excepcion.Should().BeOfType<PostgresException>()
            .Which.MessageText.Should().Contain("sin un plan confirmado");
    }

    [Fact]
    public async Task El_flujo_del_dominio_confirma_ejecuta_y_la_confirmacion_ya_no_se_deshace()
    {
        await using var arnes = await CrearArnesAsync();
        var tareaId = await CrearTareaDeLaGestoraAsync(arnes);

        // Control positivo del trigger diferido: ConfirmarPlan guarda raíz y pasos
        // en la misma transacción, en el orden que elija EF.
        ComoPersona(_gestora, _tenantBeneficiario);
        await using (var scope = arnes.Servicios.CreateAsyncScope())
        {
            var contexto = scope.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
            var tarea = await contexto.TareasAsistente.Include(t => t.Pasos).SingleAsync(t => t.Id == tareaId);
            tarea.ConfirmarPlan(_gestora, null, Ahora);
            await contexto.SaveChangesAsync();
            tarea.RegistrarPasoEjecutado(tarea.Pasos.Single().Id, Guid.NewGuid(), Ahora);
            await contexto.SaveChangesAsync();
        }

        await using var conexion = await AbrirComoRuntimeAsync(arnes, _tenantBeneficiario, _gestora);
        await using (var estado = conexion.CreateCommand())
        {
            estado.CommandText = """SELECT "Estado" FROM "TareasAsistente" WHERE "Id" = @id;""";
            estado.Parameters.AddWithValue("id", tareaId);
            ((string)(await estado.ExecuteScalarAsync())!).Should().Be(nameof(EstadoTareaAsistente.Terminada));
        }

        await using var deshacer = conexion.CreateCommand();
        deshacer.CommandText = """
            UPDATE "TareasAsistente"
            SET "Estado" = 'PlanListo', "PlanConfirmadoEnUtc" = NULL, "PlanConfirmadoPorActorRealUsuarioId" = NULL
            WHERE "Id" = @id;
            """;
        deshacer.Parameters.AddWithValue("id", tareaId);

        var excepcion = await Record.ExceptionAsync(() => deshacer.ExecuteNonQueryAsync());

        excepcion.Should().BeOfType<PostgresException>()
            .Which.MessageText.Should().Contain("no se modifica",
                "sin la confirmación en la raíz, sus pasos ejecutados colgarían de un plan sin confirmar");
    }

    [Theory]
    [InlineData("TareasAsistente")]
    [InlineData("TurnosTareaAsistente")]
    [InlineData("PasosTareaAsistente")]
    public async Task Soporte_TALVEG_no_lee_las_tablas_ni_dentro_del_Tenant_objetivo(string tabla)
    {
        await using var arnes = await CrearArnesAsync();
        await CrearTareaDeLaGestoraAsync(arnes);

        await using var conexion = new NpgsqlConnection(arnes.CadenaPropietario);
        await conexion.OpenAsync();
        await EjecutarAsync(conexion, "SET ROLE cae_app_soporte;");
        await FijarAsync(conexion, _tenantBeneficiario, _gestora);

        var excepcion = await Record.ExceptionAsync(() => ContarTablaAsync(conexion, tabla));

        excepcion.Should().BeOfType<PostgresException>().Which.SqlState.Should().Be(PermisoDenegado,
            "la migración revoca todo privilegio a cae_app_soporte: una Sesión Privilegiada no lee conversaciones ajenas");
    }

    private Task<ArnesDeArranqueRuntime> CrearArnesAsync() =>
        ArnesDeArranqueRuntime.CrearAsync(
            datosDePruebaActivos: false,
            tenantActualPersonalizado: _tenant,
            currentUserServicePersonalizado: _usuario);

    private void ComoPersona(Guid? usuarioId, Guid tenantId)
    {
        _usuario.UsuarioId = usuarioId;
        _usuario.TenantOrigenId = usuarioId == _gestora ? _tenantOperador : tenantId;
        _tenant.TenantId = tenantId;
    }

    private async Task<Guid> CrearTareaDeLaGestoraAsync(ArnesDeArranqueRuntime arnes)
    {
        ComoPersona(_gestora, _tenantBeneficiario);
        await using var scope = arnes.Servicios.CreateAsyncScope();
        var contexto = scope.ServiceProvider.GetRequiredService<CaeManagerDbContext>();

        var tarea = new TareaAsistente(_gestora, null, _tenantOperador, TipoViaAccesoAuditoria.OperacionDelegada, _asignacionOperacion, Ahora);
        tarea.AgregarTurnoDePersona("Alta del Centro Norte para el Cliente empresarial Demo", null, Ahora);
        tarea.GuardarPlan([new("alta_centro", """{"nombre":"Centro Norte"}""", "Alta del Centro Norte", [], [])], true, Ahora);
        contexto.TareasAsistente.Add(tarea);
        await contexto.SaveChangesAsync();
        return tarea.Id;
    }

    private static async Task<(int Tareas, int Turnos, int Pasos)> ContarAsync(ArnesDeArranqueRuntime arnes)
    {
        await using var scope = arnes.Servicios.CreateAsyncScope();
        var contexto = scope.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        return (
            await contexto.TareasAsistente.IgnoreQueryFilters().CountAsync(),
            await contexto.TurnosTareaAsistente.IgnoreQueryFilters().CountAsync(),
            await contexto.PasosTareaAsistente.IgnoreQueryFilters().CountAsync());
    }

    private static async Task<string?> IdentidadEfectivaAsync(ArnesDeArranqueRuntime arnes)
    {
        await using var scope = arnes.Servicios.CreateAsyncScope();
        var contexto = scope.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        return await contexto.Database.SqlQueryRaw<string>("SELECT current_user::text AS \"Value\"").SingleAsync();
    }

    private static async Task<NpgsqlConnection> AbrirComoRuntimeAsync(ArnesDeArranqueRuntime arnes, Guid tenantId, Guid usuarioId)
    {
        var conexion = new NpgsqlConnection(BaseDatosPostgresDePruebas.CadenaComoRuntime(arnes.CadenaPropietario));
        await conexion.OpenAsync();
        await FijarAsync(conexion, tenantId, usuarioId);
        return conexion;
    }

    private static async Task FijarAsync(NpgsqlConnection conexion, Guid tenantId, Guid usuarioId)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = "SELECT set_config('app.tenant_id', @tenant, false), set_config('app.usuario_id', @usuario, false);";
        comando.Parameters.AddWithValue("tenant", tenantId.ToString());
        comando.Parameters.AddWithValue("usuario", usuarioId.ToString());
        await comando.ExecuteNonQueryAsync();
    }

    private static async Task EjecutarAsync(NpgsqlConnection conexion, string sql)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        await comando.ExecuteNonQueryAsync();
    }

    private static async Task<long> ContarTablaAsync(NpgsqlConnection conexion, string tabla)
    {
        await using var consulta = conexion.CreateCommand();
        consulta.CommandText = $"SELECT count(*) FROM \"{tabla}\";";
        return (long)(await consulta.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// La persona de la sesión, cambiable entre pasos del test. El interceptor
    /// la lee en cada apertura de conexión, así que cada ámbito nuevo la toma.
    /// </summary>
    private sealed class UsuarioConmutable : ICurrentUserService
    {
        public Guid? UsuarioId { get; set; }
        public Guid? TenantOrigenId { get; set; }

        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult(UsuarioId);
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>("GestorCae");
        public Task<string?> ObtenerRolOrigenAsync() => Task.FromResult<string?>("GestorCae");
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult(TenantOrigenId);
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }
}
