using CaeManager.Application.Common;
using CaeManager.Application.Plataforma.Commands.AbrirSesionPrivilegiada;
using CaeManager.Application.Plataforma.Commands.CerrarSesionPrivilegiada;
using CaeManager.Domain.Plataforma;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Infrastructure.Plataforma;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Plataforma;

/// <summary>
/// Migración <c>PartePoliticasHijasDeConcesionPorOperacion</c> (OWASP ASVS 4.0.3 V4.1.3 y V4.2.1),
/// contra PostgreSQL real y como <c>cae_app_runtime</c>: como superusuario las políticas no se
/// evaluarían.
///
/// <para>
/// El caso que la motiva: un técnico de Soporte TALVEG con una concesión de
/// <c>RestablecimientoSegundoFactor</c> acotada al Tenant A no puede ampliarla al Tenant B por debajo
/// del dominio, ni abrir una Sesión Privilegiada sobre B, ni reescribir o borrar una sesión. La mitad
/// de control positivo recorre las escrituras legítimas de hoy con el mismo código que la aplicación:
/// la concesión a otra persona, con su fila de alcance, y la apertura y el cierre de la sesión. Sin
/// ella, los rechazos pasarían igual con una política que lo prohibiera todo.
/// </para>
/// </summary>
public class TablasHijasDeConcesionPorOperacionBajoRuntimeTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenantPlataforma = Guid.NewGuid();
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();
    private readonly Guid _raiz = Guid.NewGuid();
    private readonly Guid _admin = Guid.NewGuid();
    private readonly Guid _adminSoloDeA = Guid.NewGuid();
    private readonly Guid _tecnico = Guid.NewGuid();

    private Guid _concesionDelTecnico;

    public async Task InitializeAsync()
    {
        // Se siembra como propietario, que es lo que hacen el migrador y el arranque.
        await BaseDatosPostgresDePruebas.MigrarAsync(_cadenaConexion);

        await using var contexto = CrearContexto(_admin, cadena: _cadenaConexion);
        contexto.Tenants.Add(CrearTenant(_tenantPlataforma, "Plataforma", esPlataforma: true));
        contexto.Tenants.Add(CrearTenant(_tenantA, "Tenant A S.L."));
        contexto.Tenants.Add(CrearTenant(_tenantB, "Tenant B S.L."));

        var ahora = DateTime.UtcNow;
        contexto.ConcesionesPrivilegio.Add(ConcesionPrivilegio.Global(
            _admin, ahora.AddMinutes(-10), ahora.AddDays(30), concedidaPorUsuarioId: _raiz));
        contexto.ConcesionesPrivilegio.Add(ConcesionPrivilegio.SobreTenants(
            _adminSoloDeA, CapacidadPrivilegio.AdminPlataforma, [_tenantA],
            ahora.AddMinutes(-10), ahora.AddDays(30), concedidaPorUsuarioId: _raiz));

        // La concesión que da pie al hallazgo: acotada al Tenant A y concedida por otra persona.
        var concesionDelTecnico = ConcesionPrivilegio.SobreTenants(
            _tecnico, CapacidadPrivilegio.RestablecimientoSegundoFactor, [_tenantA],
            ahora.AddMinutes(-10), ahora.AddDays(1), concedidaPorUsuarioId: _admin,
            motivoConcesion: "El Administrador único perdió el móvil");
        contexto.ConcesionesPrivilegio.Add(concesionDelTecnico);
        await contexto.SaveChangesAsync();

        _concesionDelTecnico = concesionDelTecnico.Id;
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    // ── Escrituras legítimas: siguen pasando ───────────────────────────────

    [Fact]
    public async Task Un_AdminPlataforma_concede_a_otra_persona_con_su_fila_de_alcance_bajo_runtime()
    {
        // La misma fábrica y el mismo escritor que ConcederPrivilegioCommand: la concesión y su fila
        // de alcance en el mismo SaveChanges.
        var ahora = DateTime.UtcNow;
        var concesion = ConcesionPrivilegio.SobreTenants(
            _tecnico, CapacidadPrivilegio.RestablecimientoSegundoFactor, [_tenantB],
            ahora, ahora.AddDays(7), concedidaPorUsuarioId: _admin, motivoConcesion: "Restablecer en B");

        await using (var contexto = CrearContexto(_admin))
        {
            new PlataformaWriter(contexto).AnadirConcesion(concesion);
            await contexto.SaveChangesAsync();
        }

        (await ContarAlcanceComoPropietarioAsync(concesion.Id, _tenantB)).Should().Be(1);
    }

    [Fact]
    public async Task Un_AdminPlataforma_acotado_concede_sobre_el_Tenant_que_cubre_bajo_runtime()
    {
        var ahora = DateTime.UtcNow;
        var concesion = ConcesionPrivilegio.SobreTenants(
            _tecnico, CapacidadPrivilegio.Aprovisionamiento, [_tenantA],
            ahora, ahora.AddDays(7), concedidaPorUsuarioId: _adminSoloDeA, motivoConcesion: "Alta de A");

        await using (var contexto = CrearContexto(_adminSoloDeA))
        {
            new PlataformaWriter(contexto).AnadirConcesion(concesion);
            await contexto.SaveChangesAsync();
        }

        (await ContarAlcanceComoPropietarioAsync(concesion.Id, _tenantA)).Should().Be(1);
    }

    [Fact]
    public async Task El_tecnico_abre_y_cierra_una_sesion_sobre_el_Tenant_alcanzado_bajo_runtime()
    {
        var abierta = await AbrirComoTecnicoAsync(_concesionDelTecnico, _tenantA);
        abierta.EsExitoso.Should().BeTrue(abierta.EsFallido ? abierta.Error.Codigo : "");

        var cerrada = await CerrarComoTecnicoAsync(abierta.Valor);

        cerrada.EsExitoso.Should().BeTrue(cerrada.EsFallido ? cerrada.Error.Codigo : "");
        (await CerradaComoPropietarioAsync(abierta.Valor)).Should().BeTrue();
    }

    [Fact]
    public async Task La_sesion_se_cierra_aunque_la_concesion_ya_este_revocada()
    {
        var abierta = await AbrirComoTecnicoAsync(_concesionDelTecnico, _tenantA);
        abierta.EsExitoso.Should().BeTrue(abierta.EsFallido ? abierta.Error.Codigo : "");
        await EjecutarComoPropietarioAsync(
            @"UPDATE ""ConcesionesPrivilegio"" SET ""Estado"" = 'Revocada' WHERE ""Id"" = @id;", _concesionDelTecnico);

        var cerrada = await CerrarComoTecnicoAsync(abierta.Valor);

        cerrada.EsExitoso.Should().BeTrue(cerrada.EsFallido ? cerrada.Error.Codigo : "");
        (await CerradaComoPropietarioAsync(abierta.Valor)).Should().BeTrue(
            "cerrar no exige la concesión vigente: una sesión siempre tiene que poder cerrarse");
    }

    [Fact]
    public async Task Una_SoporteLectura_global_abre_sesion_sobre_cualquier_Tenant_bajo_runtime()
    {
        Guid concesionId;
        await using (var contexto = CrearContexto(_tecnico, cadena: _cadenaConexion))
        {
            var ahora = DateTime.UtcNow;
            var concesion = ConcesionPrivilegio.SoporteLecturaGlobal(
                _tecnico, ahora.AddMinutes(-10), ahora.AddDays(30), concedidaPorUsuarioId: _tecnico);
            contexto.ConcesionesPrivilegio.Add(concesion);
            await contexto.SaveChangesAsync();
            concesionId = concesion.Id;
        }

        var abierta = await AbrirComoTecnicoAsync(concesionId, _tenantB);

        abierta.EsExitoso.Should().BeTrue(abierta.EsFallido ? abierta.Error.Codigo : "");
    }

    // ── TenantsAlcanzadosPorConcesion ──────────────────────────────────────

    [Fact]
    public async Task El_titular_no_amplia_su_concesion_a_otro_Tenant()
    {
        await using var conexion = await AbrirRestringidaComoAsync(_tecnico);

        var accion = async () => await InsertarAlcanceAsync(conexion, _concesionDelTecnico, _tenantB);

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege,
                "la fila de alcance la escribe quien concede: si la escribiera el titular, una concesión " +
                "acotada a un Tenant valdría para cualquier otro");
        (await ContarAlcanceComoPropietarioAsync(_concesionDelTecnico, _tenantB)).Should().Be(0);
    }

    [Fact]
    public async Task Quien_concede_no_amplia_una_concesion_a_un_Tenant_que_no_cubre()
    {
        Guid concesionId;
        var ahora = DateTime.UtcNow;
        var concesion = ConcesionPrivilegio.SobreTenants(
            _tecnico, CapacidadPrivilegio.Aprovisionamiento, [_tenantA],
            ahora, ahora.AddDays(7), concedidaPorUsuarioId: _adminSoloDeA, motivoConcesion: "Alta de A");
        await using (var contexto = CrearContexto(_adminSoloDeA))
        {
            new PlataformaWriter(contexto).AnadirConcesion(concesion);
            await contexto.SaveChangesAsync();
            concesionId = concesion.Id;
        }

        await using var conexion = await AbrirRestringidaComoAsync(_adminSoloDeA);
        var accion = async () => await InsertarAlcanceAsync(conexion, concesionId, _tenantB);

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege,
                "quien concede necesita AdminPlataforma vigente sobre cada Tenant que añade");
    }

    [Fact]
    public async Task Quien_no_concedio_la_concesion_no_le_anade_alcance()
    {
        // _adminSoloDeA cubre A, pero la concesión del técnico la concedió _admin.
        await using var conexion = await AbrirRestringidaComoAsync(_adminSoloDeA);

        var accion = async () => await InsertarAlcanceAsync(conexion, _concesionDelTecnico, _tenantA);

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Quien_se_dio_a_si_mismo_una_concesion_acotada_no_la_amplia()
    {
        // La siembra el propietario: _admin, que es AdminPlataforma de todos los Tenants, es a la vez
        // titular y concedente de una concesión acotada a A. Solo la condición de que quien concede
        // no sea el titular le impide añadir B.
        Guid concesionId;
        await using (var contexto = CrearContexto(_admin, cadena: _cadenaConexion))
        {
            var ahora = DateTime.UtcNow;
            var concesion = ConcesionPrivilegio.SobreTenants(
                _admin, CapacidadPrivilegio.RestablecimientoSegundoFactor, [_tenantA],
                ahora.AddMinutes(-10), ahora.AddDays(1), concedidaPorUsuarioId: _admin,
                motivoConcesion: "Autoconcesión sembrada (test)");
            contexto.ConcesionesPrivilegio.Add(concesion);
            await contexto.SaveChangesAsync();
            concesionId = concesion.Id;
        }
        await using var conexion = await AbrirRestringidaComoAsync(_admin);

        var accion = async () => await InsertarAlcanceAsync(conexion, concesionId, _tenantB);

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege,
                "el titular nunca escribe su propio alcance, aunque figure también como concedente");
    }

    [Fact]
    public async Task Una_concesion_cerrada_no_recibe_alcance_nuevo()
    {
        await EjecutarComoPropietarioAsync(
            @"UPDATE ""ConcesionesPrivilegio"" SET ""Estado"" = 'Revocada' WHERE ""Id"" = @id;", _concesionDelTecnico);
        await using var conexion = await AbrirRestringidaComoAsync(_admin);

        var accion = async () => await InsertarAlcanceAsync(conexion, _concesionDelTecnico, _tenantB);

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task El_titular_no_mueve_ni_borra_su_fila_de_alcance()
    {
        await using var conexion = await AbrirRestringidaComoAsync(_tecnico);

        (await EjecutarAsync(conexion,
                @"UPDATE ""TenantsAlcanzadosPorConcesion"" SET ""TenantId"" = @tenant WHERE ""ConcesionPrivilegioId"" = @id;",
                _concesionDelTecnico, _tenantB))
            .Should().Be(0, "sin política de UPDATE la fila de alcance no cambia de Tenant");
        (await EjecutarAsync(conexion,
                @"DELETE FROM ""TenantsAlcanzadosPorConcesion"" WHERE ""ConcesionPrivilegioId"" = @id;",
                _concesionDelTecnico))
            .Should().Be(0, "sin política de DELETE el alcance no se borra bajo RLS");
        (await ContarAlcanceComoPropietarioAsync(_concesionDelTecnico, _tenantA)).Should().Be(1);
    }

    // ── SesionesPrivilegiadas ──────────────────────────────────────────────

    [Fact]
    public async Task El_titular_no_abre_una_sesion_sobre_un_Tenant_no_alcanzado()
    {
        await using var conexion = await AbrirRestringidaComoAsync(_tecnico);

        var accion = async () => await InsertarSesionAsync(conexion, _concesionDelTecnico, _tenantB);

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege,
                "la sesión solo se abre sobre un Tenant que la concesión alcanza");
    }

    [Fact]
    public async Task El_control_positivo_del_mismo_INSERT_sobre_el_Tenant_alcanzado_pasa()
    {
        await using var conexion = await AbrirRestringidaComoAsync(_tecnico);

        (await InsertarSesionAsync(conexion, _concesionDelTecnico, _tenantA)).Should().Be(1,
            "sin este control, el rechazo anterior pasaría también si la política lo prohibiera todo");
    }

    [Theory]
    [InlineData("now() + interval '5 hours'", null, false)]
    [InlineData("now() - interval '1 minute'", null, false)]
    [InlineData("now() + interval '1 hour'", "now()", false)]
    [InlineData("now() + interval '1 hour'", null, true)]
    public async Task Una_sesion_solo_nace_abierta_finita_y_sin_simular_a_nadie(
        string expira, string? cerrada, bool simulando)
    {
        await using var conexion = await AbrirRestringidaComoAsync(_tecnico);

        var accion = async () => await InsertarSesionAsync(
            conexion, _concesionDelTecnico, _tenantA, expira, cerrada, simulando);

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Una_sesion_de_exactamente_cuatro_horas_se_abre_bajo_runtime()
    {
        await using var conexion = await AbrirRestringidaComoAsync(_tecnico);

        (await InsertarSesionAsync(conexion, _concesionDelTecnico, _tenantA, expira: "now() + interval '4 hours'"))
            .Should().Be(1, "la ventana máxima de SesionPrivilegiada es de 4 horas, con el límite incluido");
    }

    [Fact]
    public async Task Una_sesion_no_nace_con_el_inicio_en_el_futuro()
    {
        await using var conexion = await AbrirRestringidaComoAsync(_tecnico);

        var accion = async () => await InsertarSesionAsync(conexion, _concesionDelTecnico, _tenantA,
            expira: "now() + interval '1 day 1 hour'", inicio: "now() + interval '1 day'");

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege,
                "un inicio adelantado estiraría la ventana de 4 horas más allá de la vigencia que se vio al abrir");
    }

    [Fact]
    public async Task Una_sesion_no_nace_con_el_inicio_en_el_pasado()
    {
        await using var conexion = await AbrirRestringidaComoAsync(_tecnico);

        var accion = async () => await InsertarSesionAsync(conexion, _concesionDelTecnico, _tenantA,
            expira: "now() + interval '30 minutes'", inicio: "now() - interval '3 hours'");

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege,
                "un inicio retrasado haría pasar por más corta una sesión que el Tenant ve en su rastro");
    }

    [Fact]
    public async Task Una_sesion_no_se_cierra_con_una_fecha_de_cierre_adelantada()
    {
        var sesion = await AbrirSesionDelTecnicoAsync();
        await using var conexion = await AbrirRestringidaComoAsync(_tecnico);

        var accion = async () => await EjecutarAsync(conexion,
            @"UPDATE ""SesionesPrivilegiadas"" SET ""CerradaEnUtc"" = now() + interval '1 hour' WHERE ""Id"" = @id;", sesion);

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege,
                "el cierre se fija al cerrar, no se anuncia para más adelante");
        (await CerradaComoPropietarioAsync(sesion)).Should().BeFalse();
    }

    [Fact]
    public async Task Una_sesion_no_se_cierra_con_una_fecha_de_cierre_retrasada()
    {
        // La sesión la siembra el propietario, abierta hace tres horas: bajo runtime no podría nacer así.
        var sesion = Guid.NewGuid();
        await using (var propietario = new NpgsqlConnection(_cadenaConexion))
        {
            await propietario.OpenAsync();
            (await InsertarSesionAsync(propietario, _concesionDelTecnico, _tenantA,
                    expira: "now() + interval '30 minutes'", inicio: "now() - interval '3 hours'", sesionId: sesion))
                .Should().Be(1);
        }
        await using var conexion = await AbrirRestringidaComoAsync(_tecnico);

        var accion = async () => await EjecutarAsync(conexion,
            @"UPDATE ""SesionesPrivilegiadas"" SET ""CerradaEnUtc"" = ""InicioEnUtc"" WHERE ""Id"" = @id;", sesion);

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege,
                "el rastro que ve el Tenant no puede mostrar una ventana más corta que la real");
        (await CerradaComoPropietarioAsync(sesion)).Should().BeFalse();
    }

    [Theory]
    [InlineData(@"UPDATE ""ConcesionesPrivilegio"" SET ""Estado"" = 'Revocada' WHERE ""Id"" = @id;")]
    [InlineData(@"UPDATE ""ConcesionesPrivilegio"" SET ""VigenciaHasta"" = now() - interval '1 minute' WHERE ""Id"" = @id;")]
    [InlineData(@"UPDATE ""ConcesionesPrivilegio"" SET ""VigenciaDesde"" = now() + interval '1 hour' WHERE ""Id"" = @id;")]
    public async Task Una_concesion_no_vigente_no_abre_sesion_por_debajo_del_dominio(string preparacion)
    {
        await EjecutarComoPropietarioAsync(preparacion, _concesionDelTecnico);
        await using var conexion = await AbrirRestringidaComoAsync(_tecnico);

        var accion = async () => await InsertarSesionAsync(conexion, _concesionDelTecnico, _tenantA);

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Nadie_abre_sesion_con_la_concesion_de_otro()
    {
        await using var conexion = await AbrirRestringidaComoAsync(_admin);

        var accion = async () => await InsertarSesionAsync(conexion, _concesionDelTecnico, _tenantA);

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege,
                "ni siquiera quien la concedió: la sesión la abre el titular");
    }

    [Fact]
    public async Task Una_sesion_abierta_no_se_lleva_a_otro_Tenant()
    {
        var sesion = await AbrirSesionDelTecnicoAsync();
        await using var conexion = await AbrirRestringidaComoAsync(_tecnico);

        var accion = async () => await EjecutarAsync(conexion,
            @"UPDATE ""SesionesPrivilegiadas"" SET ""TenantObjetivoId"" = @tenant WHERE ""Id"" = @id;", sesion, _tenantB);

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege,
                "la fila de llegada de un UPDATE tiene que estar cerrada");
        (await TenantObjetivoComoPropietarioAsync(sesion)).Should().Be(_tenantA);
    }

    [Theory]
    [InlineData(@"""TenantObjetivoId"" = @tenant")]
    [InlineData(@"""ExpiraEnUtc"" = ""ExpiraEnUtc"" + interval '1 day'")]
    [InlineData(@"""Motivo"" = 'Otro motivo'")]
    public async Task Al_cerrar_una_sesion_no_se_reescribe_nada_mas(string otroCambio)
    {
        var sesion = await AbrirSesionDelTecnicoAsync();
        await using var conexion = await AbrirRestringidaComoAsync(_tecnico);

        var accion = async () => await EjecutarAsync(conexion,
            $@"UPDATE ""SesionesPrivilegiadas"" SET ""CerradaEnUtc"" = now(), {otroCambio} WHERE ""Id"" = @id;",
            sesion, _tenantB);

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation,
                "el WITH CHECK admite la fila cerrada, pero el trigger sesion_privilegiada_solo_se_cierra exige " +
                "que al cerrar solo cambien CerradaEnUtc y Version");
        (await CerradaComoPropietarioAsync(sesion)).Should().BeFalse();
    }

    [Fact]
    public async Task Una_sesion_cerrada_no_se_reabre()
    {
        var sesion = await AbrirSesionDelTecnicoAsync();
        (await CerrarComoTecnicoAsync(sesion)).EsExitoso.Should().BeTrue();
        await using var conexion = await AbrirRestringidaComoAsync(_tecnico);

        (await EjecutarAsync(conexion,
                @"UPDATE ""SesionesPrivilegiadas"" SET ""CerradaEnUtc"" = NULL WHERE ""Id"" = @id;", sesion))
            .Should().Be(0, "el USING del cierre solo alcanza sesiones abiertas");
        (await CerradaComoPropietarioAsync(sesion)).Should().BeTrue();
    }

    [Fact]
    public async Task Una_sesion_no_se_borra()
    {
        var sesion = await AbrirSesionDelTecnicoAsync();
        await using var conexion = await AbrirRestringidaComoAsync(_tecnico);

        (await EjecutarAsync(conexion, @"DELETE FROM ""SesionesPrivilegiadas"" WHERE ""Id"" = @id;", sesion))
            .Should().Be(0, "sin política de DELETE el rastro de quién entró en qué Tenant no se borra bajo RLS");
        (await TenantObjetivoComoPropietarioAsync(sesion)).Should().Be(_tenantA);
    }

    // ── Andamiaje ──────────────────────────────────────────────────────────

    private async Task<Guid> AbrirSesionDelTecnicoAsync()
    {
        var abierta = await AbrirComoTecnicoAsync(_concesionDelTecnico, _tenantA);
        abierta.EsExitoso.Should().BeTrue(abierta.EsFallido ? abierta.Error.Codigo : "");
        return abierta.Valor;
    }

    private async Task<Domain.Common.Result<Guid>> AbrirComoTecnicoAsync(Guid concesionId, Guid tenantObjetivo)
    {
        await using var contexto = CrearContexto(_tecnico);
        var handler = new AbrirSesionPrivilegiadaCommandHandler(
            contexto, contexto, new PlataformaWriter(contexto), Usuario(_tecnico), contexto);

        return await handler.Handle(
            new AbrirSesionPrivilegiadaCommand(concesionId, tenantObjetivo, "El Administrador perdió el móvil", HorasDeVentana: 1),
            CancellationToken.None);
    }

    private async Task<Domain.Common.Result> CerrarComoTecnicoAsync(Guid sesionId)
    {
        await using var contexto = CrearContexto(_tecnico);
        var handler = new CerrarSesionPrivilegiadaCommandHandler(contexto, contexto, Usuario(_tecnico));
        return await handler.Handle(new CerrarSesionPrivilegiadaCommand(sesionId), CancellationToken.None);
    }

    private async Task<NpgsqlConnection> AbrirRestringidaComoAsync(Guid usuarioId)
    {
        var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();

        await ContextoRlsDePruebas.FijarAsync(conexion, usuarioId: usuarioId);

        // Sin SET ROLE seguiríamos siendo superusuario y las políticas no se evaluarían.
        await using var setRol = conexion.CreateCommand();
        setRol.CommandText = "SET ROLE cae_app_runtime;";
        await setRol.ExecuteNonQueryAsync();

        return conexion;
    }

    private static async Task<int> InsertarAlcanceAsync(NpgsqlConnection conexion, Guid concesionId, Guid tenantId)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = @"
INSERT INTO public.""TenantsAlcanzadosPorConcesion"" (""Id"", ""ConcesionPrivilegioId"", ""TenantId"")
VALUES (gen_random_uuid(), @concesion, @tenant);";
        comando.Parameters.AddWithValue("concesion", concesionId);
        comando.Parameters.AddWithValue("tenant", tenantId);
        return await comando.ExecuteNonQueryAsync();
    }

    private static async Task<int> InsertarSesionAsync(
        NpgsqlConnection conexion, Guid concesionId, Guid tenantObjetivo,
        string expira = "now() + interval '1 hour'", string? cerrada = null, bool simulando = false,
        string inicio = "now()", Guid? sesionId = null)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = $@"
INSERT INTO public.""SesionesPrivilegiadas""
    (""Id"", ""ConcesionPrivilegioId"", ""TenantObjetivoId"", ""UsuarioSimuladoId"", ""Motivo"", ""Ticket"",
     ""InicioEnUtc"", ""ExpiraEnUtc"", ""CerradaEnUtc"", ""Version"", ""Capacidad"")
VALUES (@sesion, @concesion, @tenant, {(simulando ? "gen_random_uuid()" : "NULL")},
        'Escritura por debajo del dominio (test)', NULL, {inicio}, {expira}, {cerrada ?? "NULL"},
        gen_random_uuid(), 'RestablecimientoSegundoFactor');";
        comando.Parameters.AddWithValue("sesion", sesionId ?? Guid.NewGuid());
        comando.Parameters.AddWithValue("concesion", concesionId);
        comando.Parameters.AddWithValue("tenant", tenantObjetivo);
        return await comando.ExecuteNonQueryAsync();
    }

    private static async Task<int> EjecutarAsync(NpgsqlConnection conexion, string sql, Guid id, Guid? tenant = null)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        comando.Parameters.AddWithValue("id", id);
        if (tenant is not null)
            comando.Parameters.AddWithValue("tenant", tenant.Value);
        return await comando.ExecuteNonQueryAsync();
    }

    private async Task EjecutarComoPropietarioAsync(string sql, Guid id)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        (await EjecutarAsync(conexion, sql, id)).Should().Be(1, "la preparación tiene que tocar la concesión");
    }

    private async Task<long> ContarAlcanceComoPropietarioAsync(Guid concesionId, Guid tenantId)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = @"SELECT count(*) FROM ""TenantsAlcanzadosPorConcesion""
                                 WHERE ""ConcesionPrivilegioId"" = @id AND ""TenantId"" = @tenant;";
        comando.Parameters.AddWithValue("id", concesionId);
        comando.Parameters.AddWithValue("tenant", tenantId);
        return (long)(await comando.ExecuteScalarAsync())!;
    }

    private async Task<bool> CerradaComoPropietarioAsync(Guid sesionId)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = @"SELECT ""CerradaEnUtc"" IS NOT NULL FROM ""SesionesPrivilegiadas"" WHERE ""Id"" = @id;";
        comando.Parameters.AddWithValue("id", sesionId);
        return (bool)(await comando.ExecuteScalarAsync())!;
    }

    private async Task<Guid> TenantObjetivoComoPropietarioAsync(Guid sesionId)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = @"SELECT ""TenantObjetivoId"" FROM ""SesionesPrivilegiadas"" WHERE ""Id"" = @id;";
        comando.Parameters.AddWithValue("id", sesionId);
        return (Guid)(await comando.ExecuteScalarAsync())!;
    }

    private CurrentUserServiceFalso Usuario(Guid usuario) =>
        new(usuario, rol: null, tenantOrigenId: _tenantPlataforma, tieneDobleFactorActivo: true);

    /// <summary>
    /// Por defecto conecta como <c>cae_app_runtime</c>, con el contexto RLS firmado del usuario. Solo
    /// la siembra pasa la cadena del propietario.
    /// </summary>
    private CaeManagerDbContext CrearContexto(Guid usuario, string? cadena = null)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenantPlataforma };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(
                cadena ?? BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion),
                npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(
                new TenantSelladoInterceptor(tenantActual),
                new TenantRlsConnectionInterceptor(
                    tenantActual, new SinClienteSeleccionado(), Usuario(usuario),
                    BaseDatosPostgresDePruebas.FirmanteContextoRls))
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

    private sealed class SinClienteSeleccionado : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;

        public Guid? AsignacionOperacionIdSeleccionada => null;

        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }
}
