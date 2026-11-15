using CaeManager.Application.Operaciones;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Operaciones;

/// <summary>
/// <c>app_operacion_amparada_por_su_origen</c> (migración <c>AnadeProcedenciaDeOperacionExterna</c>), la versión SQL de
/// <see cref="AmparoDeOrigen"/> que usa <c>EncargoAdministracionRepository</c> porque el comando por Aprovisionamiento lee
/// como <c>cae_app_aprovisionamiento</c>, sin <c>SELECT</c> sobre <c>DelegacionesTenant</c> (Codex r2 en la PR #2237,
/// media 8). Decisión provisional de la coordinadora, pendiente de Chris.
/// <list type="bullet">
/// <item>Las dos versiones del predicado dan lo mismo sobre los mismos casos: amparada, revocada y reactivada, revocada,
/// caducada y sin origen.</item>
/// <item>Su forma: SECURITY DEFINER con el propietario de las tablas, <c>search_path</c> fijo, un único argumento (el Id
/// de la operación: ni el contador ni la fecha, que la convertían en un oráculo, ronda 3 de Codex media 4), solo un
/// booleano y <c>EXECUTE</c> solo para los tres roles de la aplicación; ningún rol gana <c>SELECT</c> sobre la tabla.</item>
/// <item>Bajo el login real de <c>cae_app_runtime</c> (sin la exención del propietario) solo responde sobre una
/// delegación de la que el contexto firmado es extremo, y como <c>cae_app_aprovisionamiento</c> responde sin poder leer
/// la tabla.</item>
/// </list>
/// </summary>
public class FuncionAmparoDeOrigenBajoRuntimeTests : IAsyncLifetime
{
    private const string Funcion = "public.app_operacion_amparada_por_su_origen(uuid)";

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Tenant _operador = new("Operador CAE externo de prueba");
    private readonly Tenant _ajeno = new("Tenant ajeno al par");
    private readonly Dictionary<string, (Guid Propietario, Guid Operacion)> _casos = [];

    public async Task InitializeAsync()
    {
        await BaseDatosPostgresDePruebas.MigrarAsync(_cadenaConexion);
        await using var contexto = ContextoPropietarioDeLaBase();
        contexto.Tenants.AddRange(_operador, _ajeno);

        var ahora = DateTime.UtcNow;
        foreach (var caso in new[] { "amparada", "revocada y reactivada", "revocada", "caducada", "sin origen" })
        {
            // Un Tenant propietario por caso: solo cabe una operación del Tenant entero vigente por propietario y servicio.
            var propietario = new Tenant($"Tenant propietario de prueba ({caso})");
            contexto.Tenants.Add(propietario);
            var operacion = AsignacionOperacion.Externa(
                propietario.Id, _operador.Id, ServicioCae.Outbound, AmbitoAsignacion.Universal,
                vigenciaDesde: ahora.AddDays(-1), vigenciaHasta: null, ahora);
            if (caso == "sin origen")
                contexto.AsignacionesOperacion.Add(operacion); // sin delegación en el par: el trigger lo deja a NULL
            else
                OperacionExternaAmparada.Agregar(contexto, operacion);
            _casos[caso] = (propietario.Id, operacion.Id);
        }

        await contexto.SaveChangesAsync();

        // Como la ranura anterior: solo escribe Activa. El trigger cuenta cada revocación.
        var reactivada = await OrigenDeAsync(contexto, "revocada y reactivada");
        await reactivada.ExecuteUpdateAsync(s => s.SetProperty(d => d.Activa, false));
        await reactivada.ExecuteUpdateAsync(s => s.SetProperty(d => d.Activa, true));
        await (await OrigenDeAsync(contexto, "revocada")).ExecuteUpdateAsync(s => s.SetProperty(d => d.Activa, false));
        var caducidad = ahora.AddMinutes(-5);
        await (await OrigenDeAsync(contexto, "caducada")).ExecuteUpdateAsync(s => s.SetProperty(d => d.ExpiraEnUtc, caducidad));
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task La_funcion_y_AmparoDeOrigen_dan_lo_mismo_en_cada_caso()
    {
        await using var contexto = ContextoPropietarioDeLaBase();
        var ids = _casos.Values.Select(c => c.Operacion).ToList();
        var ahora = DateTime.UtcNow;

        var porLinq = await contexto.AsignacionesOperacion.AsNoTracking()
            .AmparadasPorSuOrigen(contexto.DelegacionesTenant, ahora)
            .Where(o => ids.Contains(o.Id)).Select(o => o.Id).ToListAsync();
        var porFuncion = await contexto.AsignacionesOperacion.AsNoTracking()
            .AmparadasPorSuOrigenEnBase()
            .Where(o => ids.Contains(o.Id)).Select(o => o.Id).ToListAsync();

        porLinq.Should().BeEquivalentTo([_casos["amparada"].Operacion],
            "control: solo la operación cuyo origen sigue activo, sin caducar y sin revocar desde que nació");
        porFuncion.Should().BeEquivalentTo(porLinq, "las dos versiones del predicado no se pueden separar");
    }

    [Fact]
    public async Task Es_SECURITY_DEFINER_con_search_path_fijo_solo_devuelve_un_booleano_y_no_concede_la_tabla()
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = new NpgsqlCommand($"""
            SELECT p.prosecdef, p.prorettype::regtype::text, p.proretset, p.proconfig, p.provolatile::text,
                   p.proowner = (SELECT c.relowner FROM pg_class c WHERE c.oid = 'public."DelegacionesTenant"'::regclass),
                   COALESCE((SELECT bool_or(a.grantee = 0) FROM aclexplode(p.proacl) a), false),
                   has_function_privilege('cae_app_runtime', p.oid, 'EXECUTE'),
                   has_function_privilege('cae_app_soporte', p.oid, 'EXECUTE'),
                   has_function_privilege('cae_app_aprovisionamiento', p.oid, 'EXECUTE'),
                   has_table_privilege('cae_app_aprovisionamiento', 'public."DelegacionesTenant"', 'SELECT'),
                   p.pronargs
            FROM pg_proc p WHERE p.oid = '{Funcion}'::regprocedure;
            """, conexion);

        await using var lector = await comando.ExecuteReaderAsync();
        (await lector.ReadAsync()).Should().BeTrue();
        lector.GetBoolean(0).Should().BeTrue("sin SECURITY DEFINER no leería la tabla que el rol de Aprovisionamiento no tiene");
        lector.GetString(1).Should().Be("boolean");
        lector.GetBoolean(2).Should().BeFalse("no devuelve filas");
        lector.GetFieldValue<string[]>(3).Should().ContainSingle(c => c.StartsWith("search_path=", StringComparison.Ordinal));
        lector.GetString(4).Should().Be("s");
        lector.GetBoolean(5).Should().BeTrue("la función lee con el propietario de las tablas");
        lector.GetBoolean(6).Should().BeFalse("PUBLIC no la ejecuta");
        lector.GetBoolean(7).Should().BeTrue();
        lector.GetBoolean(8).Should().BeTrue();
        lector.GetBoolean(9).Should().BeTrue();
        lector.GetBoolean(10).Should().BeFalse("la función no se acompaña de un SELECT sobre la tabla");
        lector.GetInt16(11).Should().Be(1,
            "solo recibe el Id de la operación: con el contador o la fecha como argumento serviría de oráculo");
    }

    [Fact]
    public async Task Bajo_runtime_solo_responde_a_un_extremo_de_la_delegacion_y_bajo_aprovisionamiento_sin_leer_la_tabla()
    {
        var (propietario, operacion) = _casos["amparada"];
        var llamada = $"SELECT public.app_operacion_amparada_por_su_origen('{operacion}');";

        (await ComoRuntimeAsync(propietario, rol: null, llamada)).Should().Be(true, "control: el Tenant propietario es extremo");
        (await ComoRuntimeAsync(_operador.Id, rol: null, llamada)).Should().Be(true, "control: el Operador CAE externo es extremo");
        (await ComoRuntimeAsync(_ajeno.Id, rol: null, llamada)).Should().Be(false,
            "un Tenant que no es extremo de la delegación no la vería bajo la RLS, así que la función tampoco le responde");

        (await ComoRuntimeAsync(propietario, rol: "cae_app_aprovisionamiento", llamada)).Should().Be(true,
            "el comando elevado confirma el amparo");
        var lectura = async () => await ComoRuntimeAsync(
            propietario, rol: "cae_app_aprovisionamiento", """SELECT EXISTS (SELECT 1 FROM public."DelegacionesTenant");""");
        (await lectura.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege,
            "control: el mismo rol no lee la tabla; lo que responde es la función");
    }

    /// <summary>
    /// Ejecuta <paramref name="sql"/> con el login real de <c>cae_app_runtime</c> (su <c>session_user</c> no evita la
    /// RLS), el contexto firmado con <paramref name="tenant"/> y, si se pide, el rol adoptado como lo adopta el
    /// interceptor. Devuelve el booleano de la primera columna.
    /// </summary>
    private async Task<bool> ComoRuntimeAsync(Guid tenant, string? rol, string sql)
    {
        await using var conexion = new NpgsqlConnection(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion));
        await conexion.OpenAsync();
        await using var transaccion = await conexion.BeginTransactionAsync();
        await ContextoRlsDePruebas.FijarAsync(conexion, tenantId: tenant, local: true, transaccion: transaccion);
        if (rol is not null)
        {
            await using var adoptar = new NpgsqlCommand($"SET LOCAL ROLE {rol};", conexion, transaccion);
            await adoptar.ExecuteNonQueryAsync();
        }

        await using (var identidad = new NpgsqlCommand("SELECT session_user::text || '/' || current_user::text;", conexion, transaccion))
            ((string)(await identidad.ExecuteScalarAsync())!).Should().Be($"cae_app_runtime/{rol ?? "cae_app_runtime"}");

        await using var comando = new NpgsqlCommand(sql, conexion, transaccion);
        return (bool)(await comando.ExecuteScalarAsync())!;
    }

    private async Task<IQueryable<DelegacionTenant>> OrigenDeAsync(CaeManagerDbContext contexto, string caso)
    {
        var operacion = _casos[caso].Operacion;
        var origen = (await contexto.AsignacionesOperacion.AsNoTracking().SingleAsync(o => o.Id == operacion)).OrigenId!.Value;
        return contexto.DelegacionesTenant.Where(d => d.Id == origen);
    }

    private CaeManagerDbContext ContextoPropietarioDeLaBase()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = null };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;
        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
