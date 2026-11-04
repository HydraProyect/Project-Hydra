using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Plataforma.Commands.AbrirSesionPrivilegiada;
using CaeManager.Domain.Plataforma;
using CaeManager.Domain.Soporte;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;
using PlataformaWriter = CaeManager.Infrastructure.Plataforma.PlataformaWriter;
using RegistroLecturaSesionPrivilegiada = CaeManager.Infrastructure.Plataforma.RegistroLecturaSesionPrivilegiada;

namespace CaeManager.IntegrationTests.Plataforma;

/// <summary>
/// Issue #2001: toda lectura que el Importador hace bajo una Sesión Privilegiada deja una fila en
/// <c>RegistrosActividadSoporte</c> con el Actor real, la Sesión Privilegiada y el Tenant objetivo,
/// contra PostgreSQL real y con la identidad de conexión de producción, <c>cae_app_runtime</c>, que
/// con la sesión adopta <c>cae_app_soporte</c>. El propietario solo siembra y lee lo que quedó escrito.
///
/// <para>
/// Lo que se ataca: la conexión de la sesión sigue sin <c>INSERT</c> sobre la tabla y la RLS no se
/// ensancha. La única escritura es la función <c>app_registrar_actividad_soporte_por_sesion</c>,
/// que vuelve a comprobar la sesión contra el contexto RLS firmado; cada precondición tiene aquí
/// un caso que la incumple, no deja fila y hace que el servicio devuelva false (fallo cerrado).
/// Mismo andamiaje que <see cref="RegistroAccesoDocumentoSensiblePorSoporteBajoRuntimeTests"/>.
/// </para>
/// </summary>
public class RegistroLecturaSesionPrivilegiadaBajoRuntimeTests : IAsyncLifetime
{
    private const string Funcion = "app_registrar_actividad_soporte_por_sesion";
    private const string Firma = Funcion + "(uuid, uuid, text, text)";

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenantPlataforma = Guid.NewGuid();
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();
    private readonly Guid _tecnico = Guid.NewGuid();
    private readonly Guid _otroTecnico = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await BaseDatosPostgresDePruebas.MigrarAsync(_cadenaConexion);

        // Siembra como propietario: el rol de runtime no crea Tenants ni cuentas.
        await using var contexto = CrearContexto(_tenantPlataforma, cadena: _cadenaConexion);
        contexto.Tenants.Add(CrearTenant(_tenantPlataforma, "Plataforma", esPlataforma: true));
        contexto.Tenants.Add(CrearTenant(_tenantA, "Tenant A S.L."));
        contexto.Tenants.Add(CrearTenant(_tenantB, "Tenant B S.L."));
        SembrarCuenta(contexto, "tecnico", _tecnico);
        SembrarCuenta(contexto, "otro-tecnico", _otroTecnico);
        await contexto.SaveChangesAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    // ── Camino positivo ──────────────────────────────────────────────────────

    [Fact]
    public async Task En_una_Sesion_Privilegiada_vigente_la_lectura_queda_registrada_con_actor_sesion_y_Tenant_objetivo()
    {
        var sesion = await AbrirSesionDeAprovisionamientoAsync([_tenantA]);

        (await RegistrarComoSoporteAsync(_tenantA, sesion, "EjecutarImportacionCommand")).Should().BeTrue();

        var fila = (await LeerFilasAsync(sesion)).Should().ContainSingle().Subject;
        fila.TenantId.Should().Be(_tenantA, "el Tenant de la fila es el Tenant objetivo, sacado del contexto firmado");
        fila.UsuarioSoporteId.Should().Be(_tecnico, "el Actor real es el técnico de Soporte TALVEG");
        fila.SesionPrivilegiadaId.Should().Be(sesion);
        fila.DelegacionTenantId.Should().BeNull("exactamente un agrupador: la Sesión Privilegiada");
        fila.Tipo.Should().Be(nameof(TipoActividadSoporte.Lectura));
        fila.Detalle.Should().Be("EjecutarImportacionCommand");
    }

    [Fact]
    public async Task Si_la_sesion_simula_a_un_usuario_la_fila_nombra_al_actor_real()
    {
        var sesion = await AbrirSesionDeSoporteLecturaAsync();
        await EjecutarComoPropietarioAsync(
            $@"UPDATE ""SesionesPrivilegiadas"" SET ""UsuarioSimuladoId"" = '{Guid.NewGuid()}' WHERE ""Id"" = @u;", sesion);

        (await RegistrarComoSoporteAsync(_tenantA, sesion, "AnalizarPlantillaCombinadaQuery")).Should().BeTrue();

        (await LeerFilasAsync(sesion)).Should().ContainSingle()
            .Which.UsuarioSoporteId.Should().Be(_tecnico, "en impersonación, la fila es del Actor real, nunca del Usuario simulado");
    }

    /// <summary>
    /// El behavior del pipeline con la pieza real: la lectura (el <c>next</c>) solo corre después de
    /// que la fila quede escrita, y con la misma conexión de la sesión, que lleva <c>cae_app_soporte</c>.
    /// </summary>
    [Fact]
    public async Task El_behavior_registra_antes_de_leer_y_la_lectura_corre_despues()
    {
        var sesion = await AbrirSesionDeAprovisionamientoAsync([_tenantA]);
        await using var contexto = CrearContexto(_tenantA, sesionPrivilegiadaId: sesion);
        var behavior = new RegistroLecturaSesionPrivilegiadaBehavior<LecturaMarcada, int>(
            new SesionFija(sesion, _tenantA),
            new RegistroLecturaSesionPrivilegiada(contexto, NullLogger<RegistroLecturaSesionPrivilegiada>.Instance));

        var filasVistasPorLaLectura = -1;
        var resultado = await behavior.Handle(new LecturaMarcada(), async ct =>
        {
            filasVistasPorLaLectura = (await LeerFilasAsync(sesion)).Count;
            return 7;
        }, CancellationToken.None);

        resultado.Should().Be(7);
        filasVistasPorLaLectura.Should().Be(1, "la fila existe antes de que la lectura corra");
        (await LeerFilasAsync(sesion)).Should().ContainSingle().Which.Detalle.Should().Be(nameof(LecturaMarcada));
    }

    [Fact]
    public async Task Con_una_sesion_cerrada_el_behavior_no_deja_leer_ni_escribe_fila()
    {
        var sesion = await AbrirSesionDeAprovisionamientoAsync([_tenantA]);
        await EjecutarComoPropietarioAsync(
            @"UPDATE ""SesionesPrivilegiadas"" SET ""CerradaEnUtc"" = now() WHERE ""Id"" = @u;", sesion);
        await using var contexto = CrearContexto(_tenantA, sesionPrivilegiadaId: sesion);
        // La memo de la aplicación todavía nombra la sesión: la función es la que la rechaza.
        var behavior = new RegistroLecturaSesionPrivilegiadaBehavior<LecturaMarcada, int>(
            new SesionFija(sesion, _tenantA),
            new RegistroLecturaSesionPrivilegiada(contexto, NullLogger<RegistroLecturaSesionPrivilegiada>.Instance));

        var leyo = false;
        var ejecutar = () => behavior.Handle(new LecturaMarcada(), _ =>
        {
            leyo = true;
            return Task.FromResult(7);
        }, CancellationToken.None);

        await ejecutar.Should().ThrowAsync<LecturaPrivilegiadaNoRegistradaException>();
        leyo.Should().BeFalse("lo que no deja rastro no se sirve");
        (await LeerFilasAsync(sesion)).Should().BeEmpty();
    }

    // ── Cada precondición de la función, incumplida: no hay fila y el servicio da false ─

    [Fact]
    public async Task Una_sesion_cerrada_no_registra()
    {
        var sesion = await AbrirSesionDeSoporteLecturaAsync();
        await EjecutarComoPropietarioAsync(
            @"UPDATE ""SesionesPrivilegiadas"" SET ""CerradaEnUtc"" = now() WHERE ""Id"" = @u;", sesion);

        await NoRegistraAsync(_tenantA, sesion);
    }

    [Fact]
    public async Task Una_sesion_caducada_no_registra()
    {
        var sesion = await AbrirSesionDeSoporteLecturaAsync();
        await EjecutarComoPropietarioAsync(
            @"UPDATE ""SesionesPrivilegiadas"" SET ""ExpiraEnUtc"" = now() - interval '1 minute' WHERE ""Id"" = @u;", sesion);

        await NoRegistraAsync(_tenantA, sesion);
    }

    [Fact]
    public async Task Una_sesion_que_aun_no_empieza_no_registra()
    {
        var sesion = await AbrirSesionDeSoporteLecturaAsync();
        await EjecutarComoPropietarioAsync(
            @"UPDATE ""SesionesPrivilegiadas"" SET ""InicioEnUtc"" = now() + interval '5 minutes' WHERE ""Id"" = @u;", sesion);

        await NoRegistraAsync(_tenantA, sesion);
    }

    [Fact]
    public async Task Una_sesion_sobre_A_no_registra_con_el_contexto_de_B_aunque_la_concesion_alcance_los_dos()
    {
        var sesion = await AbrirSesionDeAprovisionamientoAsync([_tenantA, _tenantB]);

        await NoRegistraAsync(_tenantB, sesion);
    }

    [Fact]
    public async Task Otro_actor_no_registra_con_la_sesion_del_tecnico_aunque_conozca_su_Id()
    {
        var sesion = await AbrirSesionDeSoporteLecturaAsync();

        await NoRegistraAsync(_tenantA, sesion, actor: _otroTecnico);
    }

    [Fact]
    public async Task Una_concesion_revocada_no_registra_aunque_la_sesion_siga_abierta()
    {
        var sesion = await AbrirSesionDeSoporteLecturaAsync();
        await EjecutarComoPropietarioAsync(@"
UPDATE ""ConcesionesPrivilegio"" SET ""Estado"" = 'Revocada'
WHERE ""Id"" = (SELECT ""ConcesionPrivilegioId"" FROM ""SesionesPrivilegiadas"" WHERE ""Id"" = @u);", sesion);

        await NoRegistraAsync(_tenantA, sesion);
    }

    [Fact]
    public async Task Una_concesion_caducada_no_registra_aunque_la_sesion_siga_abierta()
    {
        var sesion = await AbrirSesionDeSoporteLecturaAsync();
        await EjecutarComoPropietarioAsync(@"
UPDATE ""ConcesionesPrivilegio"" SET ""VigenciaHasta"" = now() - interval '1 minute'
WHERE ""Id"" = (SELECT ""ConcesionPrivilegioId"" FROM ""SesionesPrivilegiadas"" WHERE ""Id"" = @u);", sesion);

        await NoRegistraAsync(_tenantA, sesion);
    }

    [Fact]
    public async Task Una_concesion_por_Tenant_que_ya_no_alcanza_el_Tenant_no_registra()
    {
        var sesion = await AbrirSesionDeAprovisionamientoAsync([_tenantA]);
        await EjecutarComoPropietarioAsync(@"
UPDATE ""TenantsAlcanzadosPorConcesion"" SET ""TenantId"" = gen_random_uuid()
WHERE ""ConcesionPrivilegioId"" = (SELECT ""ConcesionPrivilegioId"" FROM ""SesionesPrivilegiadas"" WHERE ""Id"" = @u);", sesion);

        await NoRegistraAsync(_tenantA, sesion);
    }

    [Fact]
    public async Task La_funcion_solo_acepta_el_tipo_Lectura()
    {
        var sesion = await AbrirSesionDeSoporteLecturaAsync();
        await using (var contexto = CrearContexto(_tenantA, sesionPrivilegiadaId: sesion))
        {
            foreach (var tipo in Enum.GetValues<TipoActividadSoporte>().Where(t => t != TipoActividadSoporte.Lectura))
            {
                (await LlamarFuncionAsync(contexto, sesion, tipo.ToString(), "x"))
                    .Should().Be("valor_no_valido", $"{tipo} no es una lectura y la función no lo escribe");
            }
        }

        (await LeerFilasAsync(sesion)).Should().BeEmpty();
    }

    [Fact]
    public async Task Un_contexto_firmado_invalidado_no_registra()
    {
        var sesion = await AbrirSesionDeSoporteLecturaAsync();
        await using var contexto = CrearContexto(_tenantA, sesionPrivilegiadaId: sesion);
        await contexto.Database.OpenConnectionAsync();

        // Control positivo: la misma conexión y la misma sesión registran antes de invalidar el contexto.
        (await LlamarFuncionAsync(contexto, sesion, nameof(TipoActividadSoporte.Lectura), "control")).Should().Be("registrado");
        await contexto.Database.ExecuteSqlRawAsync("SELECT set_config('app.contexto', '', false);");

        (await LlamarFuncionAsync(contexto, sesion, nameof(TipoActividadSoporte.Lectura), "tras invalidar"))
            .Should().Be("contexto_no_valido");
        (await new RegistroLecturaSesionPrivilegiada(contexto, NullLogger<RegistroLecturaSesionPrivilegiada>.Instance)
                .RegistrarAsync(sesion, "tras invalidar"))
            .Should().BeFalse();

        (await LeerFilasAsync(sesion)).Should().ContainSingle().Which.Detalle.Should().Be("control");
    }

    // ── Quién puede ejecutarla, y la tabla sin INSERT para la sesión ─────────

    [Fact]
    public async Task Sin_Sesion_Privilegiada_el_rol_de_runtime_no_puede_ejecutarla()
    {
        var sesion = await AbrirSesionDeSoporteLecturaAsync();

        // Mismo técnico, mismo Tenant, pero sin la sesión en la conexión: el rol es
        // cae_app_runtime, que no tiene EXECUTE.
        await using (var contexto = CrearContexto(_tenantA))
        {
            var ejecutar = async () => await LlamarFuncionAsync(contexto, sesion, nameof(TipoActividadSoporte.Lectura), "x");
            (await ejecutar.Should().ThrowAsync<PostgresException>())
                .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);

            (await new RegistroLecturaSesionPrivilegiada(contexto, NullLogger<RegistroLecturaSesionPrivilegiada>.Instance)
                    .RegistrarAsync(sesion, "x"))
                .Should().BeFalse("el 42501 se traduce a false, que el behavior convierte en fallo cerrado");
        }

        (await LeerFilasAsync(sesion)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("cae_app_runtime", false)]
    [InlineData("cae_app_soporte", true)]
    [InlineData("cae_app_aprovisionamiento", false)]
    public async Task Solo_cae_app_soporte_tiene_EXECUTE(string rol, bool esperado)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = $"SELECT has_function_privilege(@rol, '{Firma}', 'EXECUTE');";
        comando.Parameters.AddWithValue("rol", rol);

        ((bool)(await comando.ExecuteScalarAsync())!).Should().Be(esperado);
    }

    [Fact]
    public async Task La_funcion_es_SECURITY_DEFINER_con_search_path_fijo_y_soporte_sigue_sin_INSERT()
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = $"""
            SELECT p.prosecdef,
                   array_to_string(p.proconfig, ';'),
                   has_table_privilege('cae_app_soporte', '"RegistrosActividadSoporte"', 'INSERT'),
                   p.proacl IS NULL OR EXISTS (SELECT 1 FROM aclexplode(p.proacl) a WHERE a.grantee = 0)
              FROM pg_proc p
             WHERE p.proname = '{Funcion}' AND p.pronamespace = 'public'::regnamespace;
            """;
        await using var lector = await comando.ExecuteReaderAsync();
        (await lector.ReadAsync()).Should().BeTrue("la función existe tras migrar");

        lector.GetBoolean(0).Should().BeTrue("SECURITY DEFINER");
        lector.GetString(1).Should().Be("search_path=pg_catalog, pg_temp",
            "sin search_path fijo, una tabla temporal podría suplantar a las de la sesión");
        lector.GetBoolean(2).Should().BeFalse("la función es la única puerta: la conexión de soporte no gana INSERT");
        lector.GetBoolean(3).Should().BeFalse("PUBLIC no conserva EXECUTE");
    }

    [Fact]
    public async Task La_conexion_de_la_sesion_no_puede_insertar_directamente_en_la_tabla()
    {
        var sesion = await AbrirSesionDeSoporteLecturaAsync();
        await using var contexto = CrearContexto(_tenantA, sesionPrivilegiadaId: sesion);
        contexto.RegistrosActividadSoporte.Add(
            RegistroActividadSoporte.PorSesionPrivilegiada(_tecnico, sesion, TipoActividadSoporte.Lectura, "directo"));

        var guardar = () => contexto.SaveChangesAsync();

        (await guardar.Should().ThrowAsync<DbUpdateException>())
            .WithInnerException<PostgresException>()
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        (await LeerFilasAsync(sesion)).Should().BeEmpty();
    }

    /// <summary>
    /// Por qué el behavior va ANTES de la elevación: con la conexión ya en
    /// <c>cae_app_aprovisionamiento</c>, la función no se puede ejecutar y el registro falla en cerrado.
    /// </summary>
    [Fact]
    public async Task Con_la_conexion_elevada_a_aprovisionamiento_el_registro_falla_en_cerrado()
    {
        var sesion = await AbrirSesionDeAprovisionamientoAsync([_tenantA]);
        await using var contexto = CrearContexto(_tenantA, sesionPrivilegiadaId: sesion);
        await contexto.Database.OpenConnectionAsync();
        await contexto.Database.ExecuteSqlRawAsync("SET ROLE cae_app_aprovisionamiento;");

        (await new RegistroLecturaSesionPrivilegiada(contexto, NullLogger<RegistroLecturaSesionPrivilegiada>.Instance)
                .RegistrarAsync(sesion, "EjecutarImportacionCommand"))
            .Should().BeFalse();

        await contexto.Database.ExecuteSqlRawAsync("SET ROLE cae_app_soporte;");
        (await LeerFilasAsync(sesion)).Should().BeEmpty();
    }

    // ── Andamiaje ────────────────────────────────────────────────────────────

    private sealed record LecturaMarcada : IRequest<int>, ILecturaRegistradaEnSesionPrivilegiada;

    private sealed class SesionFija(Guid sesionId, Guid tenantObjetivo) : ISesionPrivilegiadaActual
    {
        private readonly SesionPrivilegiadaActiva _sesion =
            new(sesionId, Guid.NewGuid(), tenantObjetivo, CapacidadPrivilegio.Aprovisionamiento, null);

        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(_sesion);

        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(_sesion);
    }

    private async Task NoRegistraAsync(Guid tenantDelContexto, Guid sesion, Guid? actor = null)
    {
        await using (var contexto = CrearContexto(tenantDelContexto, sesionPrivilegiadaId: sesion, actor: actor))
        {
            (await LlamarFuncionAsync(contexto, sesion, nameof(TipoActividadSoporte.Lectura), "x"))
                .Should().Be("sesion_no_autorizada");
        }

        (await RegistrarComoSoporteAsync(tenantDelContexto, sesion, "EjecutarImportacionCommand", actor))
            .Should().BeFalse("el servicio traduce el rechazo a false, y el behavior no sirve los datos");

        (await LeerFilasAsync(sesion)).Should().BeEmpty("sin sesión válida la función no escribe");
    }

    private async Task<bool> RegistrarComoSoporteAsync(Guid tenantDelContexto, Guid sesion, string operacion, Guid? actor = null)
    {
        await using var contexto = CrearContexto(tenantDelContexto, sesionPrivilegiadaId: sesion, actor: actor);
        return await new RegistroLecturaSesionPrivilegiada(contexto, NullLogger<RegistroLecturaSesionPrivilegiada>.Instance)
            .RegistrarAsync(sesion, operacion);
    }

    private static Task<string> LlamarFuncionAsync(CaeManagerDbContext contexto, Guid sesion, string tipo, string detalle) =>
        contexto.Database
            .SqlQuery<string>(
                $"SELECT app_registrar_actividad_soporte_por_sesion({sesion}, {Guid.NewGuid()}, {tipo}, {detalle}) AS \"Value\"")
            .SingleAsync();

    private void SembrarCuenta(CaeManagerDbContext contexto, string nombre, Guid id)
    {
        // El técnico de Soporte TALVEG es una cuenta del Tenant de plataforma.
        var correo = $"{nombre}@registro-lectura-sesion.test";
        contexto.Users.Add(new ApplicationUser
        {
            Id = id,
            UserName = correo,
            NormalizedUserName = correo.ToUpperInvariant(),
            Email = correo,
            NormalizedEmail = correo.ToUpperInvariant(),
            NombreCompleto = nombre,
            TenantId = _tenantPlataforma,
            SecurityStamp = Guid.NewGuid().ToString("N").ToUpperInvariant(),
        });
    }

    private async Task<Guid> AbrirSesionDeSoporteLecturaAsync()
    {
        var ahora = DateTime.UtcNow;
        var concesion = ConcesionPrivilegio.SoporteLecturaGlobal(
            _tecnico, ahora.AddMinutes(-10), ahora.AddDays(30), concedidaPorUsuarioId: _tecnico);

        // Sembrada como propietario: bajo runtime la auto-concesión exige AdminPlataforma vigente.
        await using (var contexto = CrearContexto(_tenantPlataforma, cadena: _cadenaConexion))
        {
            contexto.ConcesionesPrivilegio.Add(concesion);
            await contexto.SaveChangesAsync();
        }

        return await AbrirComoRuntimeAsync(concesion.Id, _tenantA);
    }

    /// <summary>Aprovisionamiento por Tenant: la capacidad con la que el Importador lee bajo la sesión.</summary>
    private async Task<Guid> AbrirSesionDeAprovisionamientoAsync(Guid[] tenantsAlcanzados)
    {
        var ahora = DateTime.UtcNow;
        var concesion = ConcesionPrivilegio.SobreTenants(
            _tecnico, CapacidadPrivilegio.Aprovisionamiento, tenantsAlcanzados,
            ahora.AddMinutes(-10), ahora.AddDays(1), concedidaPorUsuarioId: _otroTecnico);
        await using (var contexto = CrearContexto(_tenantPlataforma, cadena: _cadenaConexion))
        {
            contexto.ConcesionesPrivilegio.Add(concesion);
            await contexto.SaveChangesAsync();
        }

        return await AbrirComoRuntimeAsync(concesion.Id, _tenantA);
    }

    private async Task<Guid> AbrirComoRuntimeAsync(Guid concesionId, Guid tenantObjetivo)
    {
        await using var contexto = CrearContexto(_tenantPlataforma);
        var handler = new AbrirSesionPrivilegiadaCommandHandler(
            contexto, contexto, new PlataformaWriter(contexto), UsuarioTecnico(), contexto);

        var resultado = await handler.Handle(
            new AbrirSesionPrivilegiadaCommand(concesionId, tenantObjetivo, "Carga inicial del catálogo", HorasDeVentana: 1),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Codigo : "");
        return resultado.Valor;
    }

    private sealed record FilaRegistro(
        Guid TenantId, Guid UsuarioSoporteId, string Tipo, Guid? DelegacionTenantId, Guid? SesionPrivilegiadaId, string? Detalle);

    private async Task<List<FilaRegistro>> LeerFilasAsync(Guid sesion)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = """
            SELECT "TenantId", "UsuarioSoporteId", "Tipo", "DelegacionTenantId", "SesionPrivilegiadaId", "Detalle"
              FROM "RegistrosActividadSoporte"
             WHERE "SesionPrivilegiadaId" = @s
             ORDER BY "OcurridaEnUtc";
            """;
        comando.Parameters.AddWithValue("s", sesion);
        await using var lector = await comando.ExecuteReaderAsync();
        var filas = new List<FilaRegistro>();
        while (await lector.ReadAsync())
        {
            filas.Add(new FilaRegistro(
                lector.GetGuid(0),
                lector.GetGuid(1),
                lector.GetString(2),
                lector.IsDBNull(3) ? null : lector.GetGuid(3),
                lector.IsDBNull(4) ? null : lector.GetGuid(4),
                lector.IsDBNull(5) ? null : lector.GetString(5)));
        }

        return filas;
    }

    private async Task EjecutarComoPropietarioAsync(string sql, Guid parametro)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        comando.Parameters.AddWithValue("u", parametro);
        (await comando.ExecuteNonQueryAsync()).Should().BeGreaterThan(0, "la preparación tiene que tocar alguna fila");
    }

    private CurrentUserServiceFalso UsuarioTecnico(Guid? actor = null) =>
        new(actor ?? _tecnico, rol: null, tenantOrigenId: _tenantPlataforma, tieneDobleFactorActivo: true);

    /// <summary>
    /// Por defecto conecta como <c>cae_app_runtime</c>: la identidad de producción.
    /// Solo la siembra pasa la cadena del propietario.
    /// </summary>
    private CaeManagerDbContext CrearContexto(
        Guid tenantId, Guid? sesionPrivilegiadaId = null, string? cadena = null, Guid? actor = null)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var seleccion = new ClienteActivoSeleccionadoFalso(
            sesionPrivilegiadaId is null ? null : tenantId, sesionPrivilegiadaId);
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(
                cadena ?? BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion),
                npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(
                new TenantSelladoInterceptor(tenantActual),
                new TenantRlsConnectionInterceptor(
                    tenantActual, seleccion, UsuarioTecnico(actor), BaseDatosPostgresDePruebas.FirmanteContextoRls))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private static Domain.Tenants.Tenant CrearTenant(Guid id, string nombre, bool esPlataforma = false)
    {
        var tenant = new Domain.Tenants.Tenant(nombre);
        typeof(Domain.Common.Entity).GetProperty(nameof(Domain.Common.Entity.Id))!.SetValue(tenant, id);
        if (esPlataforma) tenant.MarcarComoPlataforma();
        return tenant;
    }

    private sealed class ClienteActivoSeleccionadoFalso(Guid? tenantId, Guid? sesionPrivilegiadaId)
        : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => tenantId;

        public Guid? AsignacionOperacionIdSeleccionada => null;

        public Guid? SesionPrivilegiadaIdSeleccionada => sesionPrivilegiadaId;
    }
}
