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
/// Migraciones <c>PartePoliticasHijasDeConcesionPorOperacion</c> y
/// <c>AlcanceDeConcesionSoloEnSuCreacion</c> (OWASP ASVS 4.0.3 V4.1.3 y V4.2.1), contra PostgreSQL real
/// y como <c>cae_app_runtime</c>: como superusuario las políticas no se evaluarían.
///
/// <para>
/// El caso que la motiva: un técnico de Soporte TALVEG con una concesión de
/// <c>RestablecimientoSegundoFactor</c> acotada al Tenant A no puede ampliarla al Tenant B por debajo
/// del dominio, ni abrir una Sesión Privilegiada sobre B, ni reescribir o borrar una sesión. Tampoco
/// la amplía quien se la concedió: el alcance solo se fija en la transacción que la crea. La mitad
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
    public async Task Quien_concede_no_amplia_una_concesion_ya_creada_a_otro_Tenant_que_si_cubre()
    {
        // El hallazgo: _admin concedió sobre A y cubre también B. Todas las condiciones de autoridad
        // se cumplen; solo falla que la concesión ya no se esté creando.
        var ahora = DateTime.UtcNow;
        var concesion = ConcesionPrivilegio.SobreTenants(
            _tecnico, CapacidadPrivilegio.RestablecimientoSegundoFactor, [_tenantA],
            ahora, ahora.AddDays(7), concedidaPorUsuarioId: _admin, motivoConcesion: "Restablecer en A");
        await using (var contexto = CrearContexto(_admin))
        {
            new PlataformaWriter(contexto).AnadirConcesion(concesion);
            await contexto.SaveChangesAsync();
        }
        await using var conexion = await AbrirRestringidaComoAsync(_admin);

        var accion = async () => await InsertarAlcanceAsync(conexion, concesion.Id, _tenantB);

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege,
                "el alcance se fija al crear la concesión: añadirlo después la amplía sin nuevo acto ni motivo");
        (await ContarAlcanceComoPropietarioAsync(concesion.Id, _tenantB)).Should().Be(0);
        (await AbrirComoTecnicoAsync(concesion.Id, _tenantB)).EsFallido.Should().BeTrue(
            "sin la fila de alcance, la concesión no abre una Sesión Privilegiada sobre B");
        var sobreA = await AbrirComoTecnicoAsync(concesion.Id, _tenantA);
        sobreA.EsExitoso.Should().BeTrue(sobreA.EsFallido ? sobreA.Error.Codigo : "");
    }

    [Fact]
    public async Task Un_alta_dentro_de_una_transaccion_exterior_guarda_su_alcance()
    {
        // Con una transacción abierta, EF Core guarda dentro de un punto de guardado. La identidad es la
        // de la transacción de nivel superior, así que el alta no depende de quién abra la transacción.
        var ahora = DateTime.UtcNow;
        var concesion = ConcesionPrivilegio.SobreTenants(
            _tecnico, CapacidadPrivilegio.Aprovisionamiento, [_tenantB],
            ahora, ahora.AddDays(7), concedidaPorUsuarioId: _admin, motivoConcesion: "Alta de B");
        await using (var contexto = CrearContexto(_admin))
        {
            await using var exterior = await contexto.Database.BeginTransactionAsync();
            new PlataformaWriter(contexto).AnadirConcesion(concesion);
            await contexto.SaveChangesAsync();
            await exterior.CommitAsync();
        }

        (await ContarAlcanceComoPropietarioAsync(concesion.Id, _tenantB)).Should().Be(1);
    }

    [Fact]
    public async Task El_alcance_entra_en_el_alta_aunque_la_concesion_se_cree_en_un_punto_de_guardado()
    {
        // La subtransacción es la que xmin no reconocía: aquí la concesión nace dentro de ella y la fila
        // de alcance se escribe después de liberarla.
        (await AnadirAlcanceDentroDelAltaAsync(
                usuario: _admin, beneficiario: _tecnico, concedente: _admin, _tenantB, enPuntoDeGuardado: true))
            .Should().Be(1);
    }

    [Fact]
    public async Task Una_concesion_sin_transaccion_de_alta_no_recibe_alcance()
    {
        // Es lo que queda en las filas anteriores a la migración: NULL nunca coincide.
        var accion = async () => await AnadirAlcanceDentroDelAltaAsync(
            usuario: _admin, beneficiario: _tecnico, concedente: _admin, _tenantB,
            trasElAlta: @"UPDATE ""ConcesionesPrivilegio"" SET ""TransaccionDeAlta"" = NULL WHERE ""Id"" = @id;");

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Una_concesion_con_mas_de_cinco_minutos_no_recibe_alcance_aunque_coincida_la_transaccion()
    {
        // La coincidencia que deja una restauración lógica, que reinicia la cuenta de transacciones.
        var accion = async () => await AnadirAlcanceDentroDelAltaAsync(
            usuario: _admin, beneficiario: _tecnico, concedente: _admin, _tenantB,
            vigenciaDesde: "now() - interval '6 minutes'");

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task La_transaccion_de_alta_no_se_elige_en_el_INSERT()
    {
        // Sin el trigger, quien concede anotaría un identificador futuro y ampliaría la concesión al
        // llegar la cuenta a él. Es la falsificación que un DEFAULT no impide.
        var ahora = DateTime.UtcNow;
        var concesion = Guid.NewGuid();
        await using var conexion = await AbrirRestringidaComoAsync(_admin);
        await using var transaccion = await conexion.BeginTransactionAsync();
        await using (var insertar = conexion.CreateCommand())
        {
            insertar.CommandText = @"
INSERT INTO public.""ConcesionesPrivilegio""
    (""Id"", ""UsuarioPlataformaId"", ""Capacidad"", ""EsAlcanceGlobal"", ""VigenciaDesde"", ""VigenciaHasta"",
     ""Estado"", ""ConcedidaPorUsuarioId"", ""MotivoConcesion"", ""CreadoEnUtc"", ""Version"", ""Origen"",
     ""TransaccionDeAlta"")
VALUES (@id, @beneficiario, 'Aprovisionamiento', false, @desde, @hasta, 'Vigente', @concedente,
        'Alta por debajo del dominio (test)', @desde, gen_random_uuid(), 'Ordinaria',
        (pg_current_xact_id()::text::numeric + 1000)::text::xid8);";
            insertar.Parameters.AddWithValue("id", concesion);
            insertar.Parameters.AddWithValue("beneficiario", _tecnico);
            insertar.Parameters.AddWithValue("desde", ahora);
            insertar.Parameters.AddWithValue("hasta", ahora.AddDays(7));
            insertar.Parameters.AddWithValue("concedente", _admin);
            (await insertar.ExecuteNonQueryAsync()).Should().Be(1);
        }

        await using var leer = conexion.CreateCommand();
        leer.CommandText =
            @"SELECT ""TransaccionDeAlta"" = pg_current_xact_id() FROM ""ConcesionesPrivilegio"" WHERE ""Id"" = @id;";
        leer.Parameters.AddWithValue("id", concesion);

        ((bool)(await leer.ExecuteScalarAsync())!).Should().BeTrue(
            "el trigger pisa cualquier valor que venga en el INSERT");
    }

    [Theory]
    [InlineData(@"UPDATE ""ConcesionesPrivilegio"" SET ""TransaccionDeAlta"" = pg_current_xact_id() WHERE ""Id"" = @id;",
        PostgresErrorCodes.InsufficientPrivilege)]
    [InlineData(@"UPDATE ""ConcesionesPrivilegio"" SET ""Estado"" = 'Revocada', ""TransaccionDeAlta"" = pg_current_xact_id() WHERE ""Id"" = @id;",
        PostgresErrorCodes.CheckViolation)]
    public async Task La_transaccion_de_alta_no_se_reescribe_despues(string sql, string sqlState)
    {
        await using var conexion = await AbrirRestringidaComoAsync(_admin);

        var accion = async () => await EjecutarAsync(conexion, sql, _concesionDelTecnico);

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(sqlState,
                "una vigente no se reescribe sin cerrarse, y cerrarla solo cambia su estado y su fin");
    }

    [Fact]
    public async Task Cerrar_la_concesion_en_la_misma_transaccion_no_reabre_su_alcance()
    {
        // La identidad de la transacción coincide: solo Estado = 'Vigente' lo rechaza.
        var accion = async () => await AnadirAlcanceDentroDelAltaAsync(
            usuario: _admin, beneficiario: _tecnico, concedente: _admin, _tenantB,
            trasElAlta: @"UPDATE ""ConcesionesPrivilegio"" SET ""Estado"" = 'Revocada' WHERE ""Id"" = @id;");

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Una_concesion_nace_con_varios_Tenants_en_el_mismo_SaveChanges_bajo_runtime()
    {
        var ahora = DateTime.UtcNow;
        var concesion = ConcesionPrivilegio.SobreTenants(
            _tecnico, CapacidadPrivilegio.Aprovisionamiento, [_tenantA, _tenantB],
            ahora, ahora.AddDays(7), concedidaPorUsuarioId: _admin, motivoConcesion: "Alta de A y B");

        await using (var contexto = CrearContexto(_admin))
        {
            new PlataformaWriter(contexto).AnadirConcesion(concesion);
            await contexto.SaveChangesAsync();
        }

        (await ContarAlcanceComoPropietarioAsync(concesion.Id, _tenantA)).Should().Be(1);
        (await ContarAlcanceComoPropietarioAsync(concesion.Id, _tenantB)).Should().Be(1);
    }

    // Los casos siguientes crean la concesión en la misma transacción que la fila de alcance, para que
    // el rechazo venga de la condición que nombran y no de que la concesión ya exista. Sin este control
    // positivo pasarían también con una política que lo prohibiera todo.

    [Fact]
    public async Task En_el_alta_quien_concede_escribe_el_alcance_de_un_Tenant_que_cubre()
    {
        (await AnadirAlcanceDentroDelAltaAsync(usuario: _admin, beneficiario: _tecnico, concedente: _admin, _tenantB))
            .Should().Be(1);
    }

    [Fact]
    public async Task El_titular_no_amplia_su_concesion_a_otro_Tenant()
    {
        var accion = async () => await AnadirAlcanceDentroDelAltaAsync(
            usuario: _tecnico, beneficiario: _tecnico, concedente: _admin, _tenantB);

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege,
                "la fila de alcance la escribe quien concede: si la escribiera el titular, una concesión " +
                "acotada a un Tenant valdría para cualquier otro");
    }

    [Fact]
    public async Task Quien_concede_no_amplia_una_concesion_a_un_Tenant_que_no_cubre()
    {
        var accion = async () => await AnadirAlcanceDentroDelAltaAsync(
            usuario: _adminSoloDeA, beneficiario: _tecnico, concedente: _adminSoloDeA, _tenantB);

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege,
                "quien concede necesita AdminPlataforma vigente sobre cada Tenant que añade");
    }

    [Fact]
    public async Task Quien_no_concedio_la_concesion_no_le_anade_alcance()
    {
        // _adminSoloDeA cubre A, pero la concesión la concedió _admin.
        var accion = async () => await AnadirAlcanceDentroDelAltaAsync(
            usuario: _adminSoloDeA, beneficiario: _tecnico, concedente: _admin, _tenantA);

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Quien_se_dio_a_si_mismo_una_concesion_acotada_no_la_amplia()
    {
        // _admin, que es AdminPlataforma de todos los Tenants, es a la vez titular y concedente. Solo la
        // condición de que quien concede no sea el titular le impide añadir B.
        var accion = async () => await AnadirAlcanceDentroDelAltaAsync(
            usuario: _admin, beneficiario: _admin, concedente: _admin, _tenantB);

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege,
                "el titular nunca escribe su propio alcance, aunque figure también como concedente");
    }

    [Fact]
    public async Task Una_concesion_cerrada_no_recibe_alcance_nuevo()
    {
        var accion = async () => await AnadirAlcanceDentroDelAltaAsync(
            usuario: _admin, beneficiario: _tecnico, concedente: _admin, _tenantB, estado: "Revocada");

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Una_concesion_global_no_recibe_alcance()
    {
        var accion = async () => await AnadirAlcanceDentroDelAltaAsync(
            usuario: _admin, beneficiario: _tecnico, concedente: _admin, _tenantB, esAlcanceGlobal: true);

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

    /// <summary>
    /// Crea la concesión como propietario, que no pasa por las políticas de alta, y en la misma
    /// transacción baja a <c>cae_app_runtime</c> para escribir la fila de alcance. Así
    /// <c>alcance_por_quien_concede</c> ve la concesión recién creada, con la forma que cada caso pide,
    /// y solo decide el resto de su <c>WITH CHECK</c>.
    /// </summary>
    private async Task<int> AnadirAlcanceDentroDelAltaAsync(
        Guid usuario, Guid beneficiario, Guid concedente, Guid tenantId,
        string estado = "Vigente", bool esAlcanceGlobal = false, string vigenciaDesde = "now()",
        bool enPuntoDeGuardado = false, string? trasElAlta = null)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await ContextoRlsDePruebas.FijarAsync(conexion, usuarioId: usuario);
        await using var alta = await conexion.BeginTransactionAsync();

        if (enPuntoDeGuardado)
            await alta.SaveAsync("alta_de_la_concesion");

        var concesionId = Guid.NewGuid();
        await using (var insertar = conexion.CreateCommand())
        {
            insertar.CommandText = $@"
INSERT INTO public.""ConcesionesPrivilegio""
    (""Id"", ""UsuarioPlataformaId"", ""Capacidad"", ""EsAlcanceGlobal"", ""VigenciaDesde"", ""VigenciaHasta"",
     ""Estado"", ""ConcedidaPorUsuarioId"", ""MotivoConcesion"", ""CreadoEnUtc"", ""Version"", ""Origen"")
VALUES (@id, @beneficiario, @capacidad, @global, {vigenciaDesde}, now() + interval '7 days', @estado, @concedente,
        'Alta por debajo del dominio (test)', now(), gen_random_uuid(), 'Ordinaria');";
            insertar.Parameters.AddWithValue("id", concesionId);
            insertar.Parameters.AddWithValue("beneficiario", beneficiario);
            // CK_ConcesionesPrivilegio_AlcanceGlobalSoloCapacidadesAdmitidas: lo global no es Aprovisionamiento.
            insertar.Parameters.AddWithValue("capacidad", esAlcanceGlobal ? "SoporteLectura" : "Aprovisionamiento");
            insertar.Parameters.AddWithValue("global", esAlcanceGlobal);
            insertar.Parameters.AddWithValue("estado", estado);
            insertar.Parameters.AddWithValue("concedente", concedente);
            await insertar.ExecuteNonQueryAsync();
        }

        if (enPuntoDeGuardado)
            await alta.ReleaseAsync("alta_de_la_concesion");

        if (trasElAlta is not null)
            await EjecutarAsync(conexion, trasElAlta, concesionId);

        await using (var setRol = conexion.CreateCommand())
        {
            setRol.CommandText = "SET LOCAL ROLE cae_app_runtime;";
            await setRol.ExecuteNonQueryAsync();
        }

        return await InsertarAlcanceAsync(conexion, concesionId, tenantId);
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
