using System.Data;
using CaeManager.Application.Common;
using CaeManager.Application.Plataforma.Commands.AbrirSesionPrivilegiada;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Plataforma;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;
using AjusteSegundoFactorPorPlataforma = CaeManager.Infrastructure.Plataforma.AjusteSegundoFactorPorPlataforma;
using PlataformaWriter = CaeManager.Infrastructure.Plataforma.PlataformaWriter;

namespace CaeManager.IntegrationTests.Plataforma;

/// <summary>
/// Decisiones del 2026-11-08: un Actor de Plataforma TALVEG cambia la exigencia de segundo factor de
/// un Tenant desde una Sesión Privilegiada con la capacidad <c>AjusteSegundoFactorTenant</c>, contra
/// PostgreSQL real y con la identidad de producción, <c>cae_app_runtime</c>, que con la sesión
/// adopta <c>cae_app_soporte</c>. El propietario solo siembra, lee y hace de escritor concurrente
/// (cierre, revocación, retirada del alcance, cambio del Administrador del Tenant) en los casos de
/// concurrencia (ASVS 4.0.3 V4.1.3, V4.2.1, V7.1.3).
///
/// <para>
/// Lo que se ataca: la conexión de la sesión sigue siendo de solo lectura y la RLS no se ensancha.
/// La única escritura posible es <c>app_cambiar_segundo_factor_tenant_por_plataforma</c>, y cada
/// precondición de sesión, concesión, Tenant, actor y motivo tiene aquí un caso que la incumple y
/// deja los parámetros intactos y sin auditoría. Las reglas de Application están en
/// <c>CambiarSegundoFactorDelTenantPorPlataformaCommandTests</c>.
/// </para>
/// </summary>
public class PlataformaAjustaSegundoFactorTenantBajoRuntimeTests : IAsyncLifetime
{
    private const string Motivo = "Petición del Tenant, ticket SOP-2048";
    private const int MinutosDeA = 45;

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenantPlataforma = Guid.NewGuid();
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();
    private readonly Guid _tecnico = Guid.NewGuid();
    private readonly Guid _adminPlataforma = Guid.NewGuid();
    private readonly Guid _administradorDeA = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await BaseDatosPostgresDePruebas.MigrarAsync(_cadenaConexion);

        await using (var contexto = CrearContexto(_tenantPlataforma, cadena: _cadenaConexion))
        {
            contexto.Tenants.Add(CrearTenant(_tenantPlataforma, "Plataforma", esPlataforma: true));
            contexto.Tenants.Add(CrearTenant(_tenantA, "Tenant A S.L."));
            contexto.Tenants.Add(CrearTenant(_tenantB, "Tenant B S.L."));
            var rolAdministrador = (await contexto.Roles.SingleAsync(r => r.Name == Roles.Administrador)).Id;

            SembrarCuenta(contexto, "tecnico", _tenantPlataforma, _tecnico);
            SembrarCuenta(contexto, "admin-plataforma", _tenantPlataforma, _adminPlataforma);
            SembrarCuenta(contexto, "admin-a", _tenantA, _administradorDeA);
            contexto.Set<Microsoft.AspNetCore.Identity.IdentityUserRole<Guid>>().Add(
                new Microsoft.AspNetCore.Identity.IdentityUserRole<Guid> { UserId = _administradorDeA, RoleId = rolAdministrador });
            await contexto.SaveChangesAsync();
        }

        // Una fila de ParametrosSistema por Tenant, con un plazo distinto del de por defecto para
        // poder ver que esta vía no lo toca.
        foreach (var tenant in new[] { _tenantA, _tenantB })
        {
            await using var contexto = CrearContexto(tenant, cadena: _cadenaConexion);
            var parametros = new ParametroSistema(30, 15);
            parametros.ActualizarSeguridadAcceso(MinutosDeA, exigeSegundoFactorTodosLosPerfiles: false);
            contexto.ParametrosSistema.Add(parametros);
            await contexto.SaveChangesAsync();
        }
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    // ── Camino positivo ──────────────────────────────────────────────────────

    [Fact]
    public async Task Con_la_concesion_cambia_solo_la_exigencia_del_Tenant_objetivo_y_audita_al_Actor_real()
    {
        var sesion = await AbrirSesionConCapacidadAsync();
        var parametrosDeB = await ParametrosAsync(_tenantB);

        await using (var contexto = CrearContexto(_tenantA, sesionPrivilegiadaId: sesion))
        {
            var resultado = await new AjusteSegundoFactorPorPlataforma(contexto, new PuertaAccesoDatos())
                .CambiarAsync(sesion, true, Motivo, CancellationToken.None);
            resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Mensaje : "");
        }

        (await ExigeAsync(_tenantA)).Should().BeTrue();
        (await MinutosAsync(_tenantA)).Should().Be(MinutosDeA, "el plazo de inactividad no cambia por esta vía");
        (await ParametrosAsync(_tenantB)).Should().Be(parametrosDeB, "solo se toca la fila del Tenant objetivo");

        var fila = (await LeerAuditoriaDeSesionAsync(sesion)).Should().ContainSingle().Subject;
        fila.Should().BeEquivalentTo(new
        {
            TenantId = _tenantA,
            EntidadTipo = "ParametroSistema",
            Accion = "Modificado",
            UsuarioId = (Guid?)_tecnico,
            ActorRealUsuarioId = (Guid?)_tecnico,
            TipoActor = "Persona",
        }, o => o.ExcludingMissingMembers());
        fila.DatosAntes.Should().Contain("\"ExigeSegundoFactorTodosLosPerfiles\" : false");
        fila.DatosDespues.Should().Contain("\"ExigeSegundoFactorTodosLosPerfiles\" : true").And.Contain(Motivo);
        (fila.DatosAntes + fila.DatosDespues).Should().NotContain("MinutosInactividadSesion");
    }

    [Fact]
    public async Task Tambien_la_deja_de_exigir()
    {
        await EjecutarAsync(@"UPDATE ""ParametrosSistema"" SET ""ExigeSegundoFactorTodosLosPerfiles"" = true WHERE ""TenantId"" = @u;", _tenantA);
        var sesion = await AbrirSesionConCapacidadAsync();

        (await CambiarComoPlataformaAsync(_tenantA, sesion, exige: false)).Should().Be("cambiado");

        (await ExigeAsync(_tenantA)).Should().BeFalse();
        (await MinutosAsync(_tenantA)).Should().Be(MinutosDeA);
    }

    [Fact]
    public async Task Pedir_el_valor_que_ya_tiene_no_escribe_ni_audita()
    {
        var sesion = await AbrirSesionConCapacidadAsync();
        var antes = await InstantaneaAsync();

        (await CambiarComoPlataformaAsync(_tenantA, sesion, exige: false)).Should().Be("sin_cambios");
        (await InstantaneaAsync()).Should().Be(antes);
    }

    [Fact]
    public async Task Un_motivo_de_exactamente_500_caracteres_con_espacios_alrededor_si_cambia()
    {
        var sesion = await AbrirSesionConCapacidadAsync();

        (await CambiarComoPlataformaAsync(_tenantA, sesion, motivo: "  " + new string('a', 500) + "  "))
            .Should().Be("cambiado");
    }

    [Fact]
    public async Task Los_blancos_alrededor_del_motivo_se_recortan_como_con_Trim()
    {
        var sesion = await AbrirSesionConCapacidadAsync();

        (await CambiarComoPlataformaAsync(_tenantA, sesion, motivo: "\t\r\n\u00A0" + new string('a', 500) + "\u3000\n"))
            .Should().Be("cambiado", "recortado, el motivo tiene 500 caracteres");
        (await LeerAuditoriaDeSesionAsync(sesion)).Should().ContainSingle()
            .Which.DatosDespues.Should().Contain("\"" + new string('a', 500) + "\"");
    }

    // ── Cada precondición de la función, incumplida ──────────────────────────

    [Fact]
    public async Task Sin_concesion_una_sesion_de_SoporteLectura_no_cambia()
    {
        var sesion = await AbrirSesionDeSoporteLecturaAsync();
        var antes = await InstantaneaAsync();

        (await CambiarComoPlataformaAsync(_tenantA, sesion)).Should().Be("sesion_no_autorizada");
        (await InstantaneaAsync()).Should().Be(antes);
    }

    [Theory]
    [InlineData(CapacidadPrivilegio.RestablecimientoSegundoFactor)]
    [InlineData(CapacidadPrivilegio.ConcesionAdministrador)]
    [InlineData(CapacidadPrivilegio.RevocacionOperadorCaeExterno)]
    public async Task Una_sesion_por_Tenant_de_otra_capacidad_acotada_no_cambia(CapacidadPrivilegio capacidad)
    {
        var sesion = await AbrirSesionConCapacidadAsync(capacidad: capacidad);
        var antes = await InstantaneaAsync();

        (await CambiarComoPlataformaAsync(_tenantA, sesion)).Should().Be("sesion_no_autorizada");
        (await InstantaneaAsync()).Should().Be(antes);
    }

    [Fact]
    public async Task Una_concesion_sobre_otro_Tenant_no_ampara_el_contexto_de_A()
    {
        var sesionSobreB = await AbrirSesionConCapacidadAsync(tenantObjetivo: _tenantB);
        var antes = await InstantaneaAsync();

        (await CambiarComoPlataformaAsync(_tenantA, sesionSobreB)).Should().Be("sesion_no_autorizada");
        (await InstantaneaAsync()).Should().Be(antes);
    }

    [Fact]
    public async Task Una_sesion_abierta_sobre_B_no_ampara_A_aunque_la_concesion_alcance_a_los_dos()
    {
        // Fija el predicado s."TenantObjetivoId" = v_tenant de la función: el alcance de la
        // concesión cubre A, así que solo ese predicado rechaza la sesión abierta sobre B.
        var sesionSobreB = await AbrirSesionConCapacidadAsync(tenantObjetivo: _tenantB, alcance: [_tenantA, _tenantB]);
        var antes = await InstantaneaAsync();

        (await CambiarComoPlataformaAsync(_tenantA, sesionSobreB)).Should().Be("sesion_no_autorizada");
        (await InstantaneaAsync()).Should().Be(antes);

        (await CambiarComoPlataformaAsync(_tenantB, sesionSobreB)).Should().Be("cambiado",
            "control positivo: la misma sesión sí cambia el Tenant sobre el que se abrió");
        (await ExigeAsync(_tenantA)).Should().BeFalse();
    }

    [Theory]
    [InlineData(@"UPDATE ""ConcesionesPrivilegio"" SET ""Estado"" = 'Revocada' WHERE ""Id"" = (SELECT ""ConcesionPrivilegioId"" FROM ""SesionesPrivilegiadas"" WHERE ""Id"" = @u);")]
    [InlineData(@"UPDATE ""ConcesionesPrivilegio"" SET ""VigenciaHasta"" = now() - interval '1 minute' WHERE ""Id"" = (SELECT ""ConcesionPrivilegioId"" FROM ""SesionesPrivilegiadas"" WHERE ""Id"" = @u);")]
    [InlineData(@"UPDATE ""TenantsAlcanzadosPorConcesion"" SET ""TenantId"" = gen_random_uuid() WHERE ""ConcesionPrivilegioId"" = (SELECT ""ConcesionPrivilegioId"" FROM ""SesionesPrivilegiadas"" WHERE ""Id"" = @u);")]
    [InlineData(@"UPDATE ""SesionesPrivilegiadas"" SET ""UsuarioSimuladoId"" = gen_random_uuid() WHERE ""Id"" = @u;")]
    [InlineData(@"UPDATE ""SesionesPrivilegiadas"" SET ""CerradaEnUtc"" = now() WHERE ""Id"" = @u;")]
    [InlineData(@"UPDATE ""SesionesPrivilegiadas"" SET ""ExpiraEnUtc"" = now() - interval '1 minute' WHERE ""Id"" = @u;")]
    public async Task Concesion_no_vigente_sin_Tenant_sesion_cerrada_o_con_simulacion_no_cambia(string preparacion)
    {
        var sesion = await AbrirSesionConCapacidadAsync();
        await EjecutarAsync(preparacion, sesion);
        var antes = await InstantaneaAsync();

        (await CambiarComoPlataformaAsync(_tenantA, sesion)).Should().Be("sesion_no_autorizada");
        (await InstantaneaAsync()).Should().Be(antes);
    }

    [Fact]
    public async Task Otro_actor_no_puede_ejercer_la_sesion()
    {
        var sesion = await AbrirSesionConCapacidadAsync();
        var antes = await InstantaneaAsync();
        await using var contexto = CrearContexto(_tenantA, sesionPrivilegiadaId: sesion, actor: _adminPlataforma);

        (await LlamarFuncionAsync(contexto, sesion, true, Motivo)).Should().Be("sesion_no_autorizada");
        (await InstantaneaAsync()).Should().Be(antes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("\r\n\t \v\f")]
    [InlineData("\u00A0\u2003\u3000\u2028")]
    [InlineData("\u0085\u1680\u2029\u202F\u205F")]
    public async Task Sin_motivo_no_cambia(string motivo)
    {
        var sesion = await AbrirSesionConCapacidadAsync();
        var antes = await InstantaneaAsync();

        (await CambiarComoPlataformaAsync(_tenantA, sesion, motivo: motivo)).Should().Be("motivo_no_valido");
        (await InstantaneaAsync()).Should().Be(antes);
    }

    // U+200B (espacio de anchura cero) no es blanco para char.IsWhiteSpace: Application lo admite
    // como motivo, y la base tampoco lo recorta.
    [Fact]
    public async Task Un_caracter_que_no_es_blanco_para_NET_si_cuenta_como_motivo()
    {
        var sesion = await AbrirSesionConCapacidadAsync();

        (await CambiarComoPlataformaAsync(_tenantA, sesion, motivo: "\u200B")).Should().Be("cambiado");
    }

    [Fact]
    public async Task Un_motivo_de_mas_de_500_caracteres_no_cambia()
    {
        var sesion = await AbrirSesionConCapacidadAsync();
        var antes = await InstantaneaAsync();

        (await CambiarComoPlataformaAsync(_tenantA, sesion, motivo: new string('a', 501))).Should().Be("motivo_no_valido");
        (await InstantaneaAsync()).Should().Be(antes);
    }

    [Fact]
    public async Task Una_concesion_que_se_dio_el_propio_tecnico_no_cambia()
    {
        var sesion = await AbrirSesionConCapacidadAsync(concedidaPor: _tecnico);
        var antes = await InstantaneaAsync();

        (await CambiarComoPlataformaAsync(_tenantA, sesion)).Should().Be("sesion_no_autorizada");
        (await InstantaneaAsync()).Should().Be(antes);
    }

    [Fact]
    public async Task La_base_no_admite_una_concesion_global_de_esta_capacidad()
    {
        var sesion = await AbrirSesionConCapacidadAsync();

        var hacerlaGlobal = () => EjecutarAsync(
            @"UPDATE ""ConcesionesPrivilegio"" SET ""EsAlcanceGlobal"" = true
WHERE ""Id"" = (SELECT ""ConcesionPrivilegioId"" FROM ""SesionesPrivilegiadas"" WHERE ""Id"" = @u);",
            sesion);

        await hacerlaGlobal.Should().ThrowAsync<PostgresException>()
            .WithMessage("*CK_ConcesionesPrivilegio_AlcanceGlobalSoloCapacidadesAdmitidas*");
    }

    [Fact]
    public async Task Un_actor_que_no_es_de_un_Tenant_de_plataforma_no_cambia()
    {
        var sesion = await AbrirSesionConCapacidadAsync();
        await EjecutarAsync(@"UPDATE ""Tenants"" SET ""EsPlataforma"" = false WHERE ""Id"" = @u;", _tenantPlataforma);
        var antes = await InstantaneaAsync();

        (await CambiarComoPlataformaAsync(_tenantA, sesion)).Should().Be("sesion_no_autorizada");
        (await InstantaneaAsync()).Should().Be(antes);
    }

    [Fact]
    public async Task No_cambia_la_exigencia_de_un_Tenant_de_plataforma()
    {
        var sesion = await AbrirSesionConCapacidadAsync();
        await EjecutarAsync(@"UPDATE ""Tenants"" SET ""EsPlataforma"" = true WHERE ""Id"" = @u;", _tenantA);
        var antes = await InstantaneaAsync();

        (await CambiarComoPlataformaAsync(_tenantA, sesion)).Should().Be("sesion_no_autorizada");
        (await InstantaneaAsync()).Should().Be(antes);
    }

    [Fact]
    public async Task Un_usuario_de_negocio_sin_sesion_no_puede_ejecutar_la_funcion()
    {
        // El Administrador del Tenant, con su conexión normal (cae_app_runtime): la función solo
        // tiene EXECUTE para cae_app_soporte. Su vía es ActualizarSeguridadAccesoCommand.
        await using var contexto = CrearContexto(_tenantA, actor: _administradorDeA, tenantOrigen: _tenantA);

        var llamada = () => LlamarFuncionAsync(contexto, Guid.NewGuid(), true, Motivo);

        (await llamada.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        (await ExigeAsync(_tenantA)).Should().BeFalse();
    }

    [Fact]
    public async Task La_conexion_de_la_sesion_sigue_sin_poder_escribir_los_parametros_directamente()
    {
        var sesion = await AbrirSesionConCapacidadAsync();
        await using var contexto = CrearContexto(_tenantA, sesionPrivilegiadaId: sesion);

        var escritura = () => contexto.Database.ExecuteSqlAsync(
            $@"UPDATE ""ParametrosSistema"" SET ""ExigeSegundoFactorTodosLosPerfiles"" = true WHERE ""TenantId"" = {_tenantA}");

        (await escritura.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege,
                "la capacidad abre un acto por la función, no un camino de escritura");
        (await ExigeAsync(_tenantA)).Should().BeFalse();
    }

    // ── Concurrencia con el cierre, la revocación y la retirada del alcance ──

    // Una escritura en curso (sin confirmar) sobre la sesión, la concesión o el alcance: la
    // primera comprobación, sin candados, no la ve y pasa; la función tiene que esperar a su
    // candado y, cuando se confirma, rechazar con lo confirmado. Sin los FOR SHARE la función no
    // espera (la barrera falla) y cambia la exigencia con una sesión que ya no la ampara.
    [Theory]
    [InlineData(@"UPDATE ""ConcesionesPrivilegio"" SET ""Estado"" = 'Revocada' WHERE ""Id"" = (SELECT ""ConcesionPrivilegioId"" FROM ""SesionesPrivilegiadas"" WHERE ""Id"" = @u);")]
    [InlineData(@"UPDATE ""SesionesPrivilegiadas"" SET ""CerradaEnUtc"" = now() WHERE ""Id"" = @u;")]
    [InlineData(@"UPDATE ""TenantsAlcanzadosPorConcesion"" SET ""TenantId"" = gen_random_uuid() WHERE ""ConcesionPrivilegioId"" = (SELECT ""ConcesionPrivilegioId"" FROM ""SesionesPrivilegiadas"" WHERE ""Id"" = @u);")]
    public async Task Un_cierre_una_revocacion_o_una_retirada_del_alcance_en_curso_se_esperan_y_se_respetan(string escrituraEnCurso)
    {
        var sesion = await AbrirSesionConCapacidadAsync();

        await using var retenedor = new NpgsqlConnection(_cadenaConexion);
        await retenedor.OpenAsync();
        await using var transaccion = await retenedor.BeginTransactionAsync();
        await using (var orden = new NpgsqlCommand(escrituraEnCurso, retenedor, transaccion))
        {
            orden.Parameters.AddWithValue("u", sesion);
            (await orden.ExecuteNonQueryAsync()).Should().Be(1, "la preparación tiene que tocar una fila");
        }

        await using var contexto = CrearContexto(_tenantA, sesionPrivilegiadaId: sesion);
        var pid = await PidAsync(contexto);
        var llamada = LlamarFuncionAsync(contexto, sesion, true, Motivo);

        (await QuedaEsperandoUnCandadoAsync(pid, llamada)).Should().BeTrue(
            "control positivo: la función tiene que esperar a la escritura en curso, no decidir sin verla");
        await transaccion.CommitAsync();

        (await llamada).Should().Be("sesion_no_autorizada");
        (await ExigeAsync(_tenantA)).Should().BeFalse();
        (await LeerAuditoriaDeSesionAsync(sesion)).Should().BeEmpty();
    }

    // Mientras la función espera la fila de ParametrosSistema (un cambio del Administrador del
    // Tenant en curso) no retiene la sesión ni la concesión: una revocación que llega entonces se
    // confirma sin esperar, y la comprobación definitiva la ve. Con la sesión y la concesión
    // bloqueadas antes que los parámetros, la revocación quedaría esperando al acto.
    [Fact]
    public async Task Una_revocacion_que_llega_mientras_la_funcion_espera_los_parametros_no_espera_y_se_respeta()
    {
        var sesion = await AbrirSesionConCapacidadAsync();

        await using var retenedor = await RetenerParametrosDeAAsync();
        await using var contexto = CrearContexto(_tenantA, sesionPrivilegiadaId: sesion);
        var pidFuncion = await PidAsync(contexto);
        var llamada = LlamarFuncionAsync(contexto, sesion, true, Motivo);
        (await QuedaEsperandoUnCandadoAsync(pidFuncion, llamada)).Should().BeTrue(
            "control positivo: la función espera la fila de ParametrosSistema");

        await using (var revocador = new NpgsqlConnection(_cadenaConexion))
        {
            await revocador.OpenAsync();
            await using var revocacion = new NpgsqlCommand(
                @"UPDATE ""ConcesionesPrivilegio"" SET ""Estado"" = 'Revocada' WHERE ""Id"" = (SELECT ""ConcesionPrivilegioId"" FROM ""SesionesPrivilegiadas"" WHERE ""Id"" = @u);",
                revocador);
            revocacion.Parameters.AddWithValue("u", sesion);
            (await revocacion.ExecuteNonQueryAsync().WaitAsync(TimeSpan.FromSeconds(10))).Should().Be(1,
                "la revocación no espera a una función que aún no ha bloqueado la concesión");
        }

        await retenedor.Transaccion.CommitAsync();

        (await llamada).Should().Be("sesion_no_autorizada");
        (await ExigeAsync(_tenantA)).Should().BeFalse();
        (await LeerAuditoriaDeSesionAsync(sesion)).Should().BeEmpty();
    }

    // La hora con la que se comprueba la ventana es la de después de los candados, no la de la
    // llamada: una sesión que caduca mientras la función espera ya no ampara el acto.
    [Fact]
    public async Task Una_sesion_que_caduca_mientras_la_funcion_espera_los_parametros_no_cambia()
    {
        var sesion = await AbrirSesionConCapacidadAsync();
        DateTime expira;
        await using (var conexion = new NpgsqlConnection(_cadenaConexion))
        {
            await conexion.OpenAsync();
            await using var orden = new NpgsqlCommand(
                @"UPDATE ""SesionesPrivilegiadas"" SET ""ExpiraEnUtc"" = clock_timestamp() + interval '3 seconds'
WHERE ""Id"" = @u RETURNING ""ExpiraEnUtc"";", conexion);
            orden.Parameters.AddWithValue("u", sesion);
            expira = ((DateTime)(await orden.ExecuteScalarAsync())!).ToUniversalTime();
        }

        await using var retenedor = await RetenerParametrosDeAAsync();
        await using var contexto = CrearContexto(_tenantA, sesionPrivilegiadaId: sesion);
        var pidFuncion = await PidAsync(contexto);
        var llamada = LlamarFuncionAsync(contexto, sesion, true, Motivo);
        (await QuedaEsperandoUnCandadoAsync(pidFuncion, llamada)).Should().BeTrue(
            "control positivo: la primera comprobación pasó y la función espera la fila de ParametrosSistema");

        var restante = expira - DateTime.UtcNow + TimeSpan.FromMilliseconds(500);
        if (restante > TimeSpan.Zero)
            await Task.Delay(restante);
        await retenedor.Transaccion.CommitAsync();

        (await llamada).Should().Be("sesion_no_autorizada");
        (await ExigeAsync(_tenantA)).Should().BeFalse();
        (await LeerAuditoriaDeSesionAsync(sesion)).Should().BeEmpty();
    }

    [Theory]
    [InlineData(IsolationLevel.RepeatableRead)]
    [InlineData(IsolationLevel.Serializable)]
    public async Task Fuera_de_READ_COMMITTED_no_cambia(IsolationLevel aislamiento)
    {
        var sesion = await AbrirSesionConCapacidadAsync();
        await using var contexto = CrearContexto(_tenantA, sesionPrivilegiadaId: sesion);

        await using (var transaccion = await contexto.Database.BeginTransactionAsync(aislamiento))
        {
            (await LlamarFuncionAsync(contexto, sesion, true, Motivo)).Should().Be("aislamiento_no_admitido");
            await transaccion.CommitAsync();
        }

        (await ExigeAsync(_tenantA)).Should().BeFalse();
        (await LeerAuditoriaDeSesionAsync(sesion)).Should().BeEmpty();
    }

    private sealed record Retencion(NpgsqlConnection Conexion, NpgsqlTransaction Transaccion) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Transaccion.DisposeAsync();
            await Conexion.DisposeAsync();
        }
    }

    // Un cambio del Administrador del Tenant en curso: la fila de ParametrosSistema de A tomada en
    // una transacción abierta.
    private async Task<Retencion> RetenerParametrosDeAAsync()
    {
        var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        var transaccion = await conexion.BeginTransactionAsync();
        await using var orden = new NpgsqlCommand(
            @"SELECT 1 FROM ""ParametrosSistema"" WHERE ""TenantId"" = @u FOR UPDATE;", conexion, transaccion);
        orden.Parameters.AddWithValue("u", _tenantA);
        (await orden.ExecuteScalarAsync()).Should().Be(1);
        return new Retencion(conexion, transaccion);
    }

    // Deja la conexión del contexto abierta: si EF la cerrara entre consultas, la llamada podría
    // salir del pool por otra conexión física y el pid no sería el suyo.
    private static async Task<int> PidAsync(CaeManagerDbContext contexto)
    {
        await contexto.Database.OpenConnectionAsync();
        return await contexto.Database.SqlQuery<int>($"SELECT pg_backend_pid() AS \"Value\"").SingleAsync();
    }

    // Barrera: el proceso está esperando un candado (no ha terminado ni fallado todavía).
    private async Task<bool> QuedaEsperandoUnCandadoAsync(int pid, Task tarea)
    {
        await using var observador = new NpgsqlConnection(_cadenaConexion);
        await observador.OpenAsync();
        for (var intento = 0; intento < 100 && !tarea.IsCompleted; intento++)
        {
            await using var consulta = new NpgsqlCommand(
                "SELECT EXISTS (SELECT 1 FROM pg_locks WHERE pid = @pid AND NOT granted);", observador);
            consulta.Parameters.AddWithValue("pid", pid);
            if ((bool)(await consulta.ExecuteScalarAsync())!)
                return true;
            await Task.Delay(100);
        }

        return false;
    }

    // ── Preparación y lectura ────────────────────────────────────────────────

    private async Task<Guid> AbrirSesionConCapacidadAsync(
        Guid? concedidaPor = null,
        CapacidadPrivilegio capacidad = CapacidadPrivilegio.AjusteSegundoFactorTenant,
        Guid? tenantObjetivo = null,
        Guid[]? alcance = null)
    {
        var objetivo = tenantObjetivo ?? _tenantA;
        var ahora = DateTime.UtcNow;
        var concesion = ConcesionPrivilegio.SobreTenants(
            _tecnico, capacidad, alcance ?? [objetivo], ahora.AddMinutes(-10), ahora.AddDays(1),
            concedidaPorUsuarioId: concedidaPor ?? _adminPlataforma);
        await using (var contexto = CrearContexto(_tenantPlataforma, cadena: _cadenaConexion))
        {
            contexto.ConcesionesPrivilegio.Add(concesion);
            await contexto.SaveChangesAsync();
        }

        // Bajo runtime: fija también el ALTER POLICY de sesion_privilegiada_apertura.
        return await AbrirComoRuntimeAsync(concesion.Id, objetivo);
    }

    private async Task<Guid> AbrirSesionDeSoporteLecturaAsync()
    {
        await using var contexto = CrearContexto(_tenantPlataforma, cadena: _cadenaConexion);
        var ahora = DateTime.UtcNow;
        var concesion = ConcesionPrivilegio.SoporteLecturaGlobal(
            _tecnico, ahora.AddMinutes(-10), ahora.AddDays(30), concedidaPorUsuarioId: _tecnico);
        contexto.ConcesionesPrivilegio.Add(concesion);
        await contexto.SaveChangesAsync();
        return await AbrirComoRuntimeAsync(concesion.Id, _tenantA);
    }

    private async Task<Guid> AbrirComoRuntimeAsync(Guid concesionId, Guid tenantObjetivo)
    {
        await using var contexto = CrearContexto(_tenantPlataforma);
        var handler = new AbrirSesionPrivilegiadaCommandHandler(
            contexto, contexto, new PlataformaWriter(contexto), Usuario(), contexto);

        var resultado = await handler.Handle(
            new AbrirSesionPrivilegiadaCommand(concesionId, tenantObjetivo, "Petición del Tenant", HorasDeVentana: 1),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Codigo : "");
        return resultado.Valor;
    }

    private async Task<string> CambiarComoPlataformaAsync(
        Guid tenantDelContexto, Guid sesion, bool exige = true, string motivo = Motivo)
    {
        await using var contexto = CrearContexto(tenantDelContexto, sesionPrivilegiadaId: sesion);
        return await LlamarFuncionAsync(contexto, sesion, exige, motivo);
    }

    // La misma llamada que AjusteSegundoFactorPorPlataforma, para leer el código exacto.
    private static Task<string> LlamarFuncionAsync(CaeManagerDbContext contexto, Guid sesion, bool exige, string motivo) =>
        contexto.Database
            .SqlQuery<string>($"SELECT app_cambiar_segundo_factor_tenant_por_plataforma({sesion}, {exige}, {motivo}) AS \"Value\"")
            .SingleAsync();

    private Task<bool> ExigeAsync(Guid tenant) => EscalarAsync<bool>(
        @"SELECT ""ExigeSegundoFactorTodosLosPerfiles"" FROM ""ParametrosSistema"" WHERE ""TenantId"" = @u;", tenant);

    private Task<int> MinutosAsync(Guid tenant) => EscalarAsync<int>(
        @"SELECT ""MinutosInactividadSesion"" FROM ""ParametrosSistema"" WHERE ""TenantId"" = @u;", tenant);

    private Task<string> ParametrosAsync(Guid tenant) => EscalarAsync<string>(
        @"SELECT row_to_json(p)::text FROM ""ParametrosSistema"" p WHERE p.""TenantId"" = @u;", tenant);

    private Task<string> InstantaneaAsync() => EscalarAsync<string>(@"
SELECT concat_ws('|',
 (SELECT coalesce(json_agg(row_to_json(t) ORDER BY t.""Id"")::text, '[]') FROM ""ParametrosSistema"" t),
 (SELECT coalesce(json_agg(row_to_json(t) ORDER BY t.""Id"")::text, '[]') FROM ""RegistrosAuditoria"" t));", Guid.Empty);

    private async Task<T> EscalarAsync<T>(string sql, Guid parametro)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        comando.Parameters.AddWithValue("u", parametro);
        return (T)(await comando.ExecuteScalarAsync())!;
    }

    private async Task EjecutarAsync(string sql, Guid parametro)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        comando.Parameters.AddWithValue("u", parametro);
        (await comando.ExecuteNonQueryAsync()).Should().Be(1, "la preparación tiene que tocar una fila");
    }

    private sealed record FilaAuditoria(
        Guid TenantId, string EntidadTipo, Guid EntidadId, string Accion, string? DatosAntes, string? DatosDespues,
        Guid? UsuarioId, Guid? ActorRealUsuarioId, string? TipoActor);

    private async Task<List<FilaAuditoria>> LeerAuditoriaDeSesionAsync(Guid sesion)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = @"
SELECT ""TenantId"", ""EntidadTipo"", ""EntidadId"", ""Accion"", ""DatosAntes"", ""DatosDespues"",
       ""UsuarioId"", ""ActorRealUsuarioId"", ""TipoActor""
FROM ""RegistrosAuditoria""
WHERE ""ViaAcceso"" = 'SesionPrivilegiada' AND ""ViaAccesoId"" = @u;";
        comando.Parameters.AddWithValue("u", sesion);
        await using var lector = await comando.ExecuteReaderAsync();
        var filas = new List<FilaAuditoria>();
        while (await lector.ReadAsync())
            filas.Add(new FilaAuditoria(
                lector.GetGuid(0), lector.GetString(1), lector.GetGuid(2), lector.GetString(3),
                lector.IsDBNull(4) ? null : lector.GetString(4), lector.IsDBNull(5) ? null : lector.GetString(5),
                lector.IsDBNull(6) ? null : lector.GetGuid(6), lector.IsDBNull(7) ? null : lector.GetGuid(7),
                lector.IsDBNull(8) ? null : lector.GetString(8)));
        return filas;
    }

    private static void SembrarCuenta(CaeManagerDbContext contexto, string nombre, Guid tenant, Guid id)
    {
        var correo = $"{nombre}@ajuste-segundo-factor.test";
        contexto.Users.Add(new ApplicationUser
        {
            Id = id,
            UserName = correo,
            NormalizedUserName = correo.ToUpperInvariant(),
            Email = correo,
            NormalizedEmail = correo.ToUpperInvariant(),
            NombreCompleto = nombre,
            TenantId = tenant,
            TwoFactorEnabled = true,
            SecurityStamp = Guid.NewGuid().ToString("N").ToUpperInvariant(),
        });
    }

    private CurrentUserServiceFalso Usuario(Guid? actor = null, Guid? tenantOrigen = null) =>
        new(actor ?? _tecnico, rol: null, tenantOrigenId: tenantOrigen ?? _tenantPlataforma, tieneDobleFactorActivo: true);

    /// <summary>
    /// Por defecto conecta como <c>cae_app_runtime</c>: la identidad de producción. Solo la
    /// siembra pasa la cadena del propietario.
    /// </summary>
    private CaeManagerDbContext CrearContexto(
        Guid tenantId, Guid? sesionPrivilegiadaId = null, string? cadena = null, Guid? actor = null, Guid? tenantOrigen = null)
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
                    tenantActual, seleccion, Usuario(actor, tenantOrigen), BaseDatosPostgresDePruebas.FirmanteContextoRls))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private static Tenant CrearTenant(Guid id, string nombre, bool esPlataforma = false)
    {
        var tenant = new Tenant(nombre);
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
