using CaeManager.Application.Common;
using CaeManager.Application.Plataforma.Commands.AutoConcederPrivilegio;
using CaeManager.Application.Plataforma.Commands.RevocarConcesionPrivilegio;
using CaeManager.Infrastructure.Auditing;
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
/// Migración <c>PartePoliticaConcesionesPorOperacion</c> (OWASP ASVS 4.0.3 V4.1.1 y V4.1.3), contra
/// PostgreSQL real y como <c>cae_app_runtime</c>, la identidad de producción: como superusuario las
/// políticas no se evaluarían.
///
/// <para>
/// Dos mitades. La de rechazo: por debajo del dominio, una sesión de usuario ya no se escribe una
/// <c>AdminPlataforma</c> global propia sin bootstrap, ni devuelve a <c>Vigente</c> una concesión
/// cerrada, ni borra una concesión, ni reabre el bootstrap. La de control positivo: las tres
/// escrituras legítimas de hoy —el acto fundacional, la auto-concesión de <c>SoporteLectura</c> y la
/// concesión a otra persona— siguen pasando por el mismo código que usa la aplicación. Sin esta
/// mitad, la primera pasaría igual con una política que lo prohibiera todo.
/// </para>
///
/// <para>
/// El acto fundacional es además la medición del orden: la concesión y el consumo van en el mismo
/// <c>SaveChanges</c>, y la política de alta exige el bootstrap sin consumir. Si EF Core ejecutara el
/// <c>UPDATE</c> de <c>EstadoBootstrapPlataforma</c> antes del <c>INSERT</c>, este test se pondría
/// rojo.
/// </para>
///
/// <para>
/// Migración <c>TopeVigenciaConcesionEnPoliticasDeAlta</c> (V4.1.1 y V11.1.3): las dos puertas de alta
/// imponen en la base el tope del dominio y una ventana sobre el inicio, sin alcanzar a la fundacional
/// sin fin ni a las filas anteriores al tope. La mitad de control positivo es en el tope exacto, con la
/// medida del dominio, y dentro de la ventana.
/// </para>
/// </summary>
public class ConcesionesPrivilegioPorOperacionBajoRuntimeTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenantPlataforma = Guid.NewGuid();
    private readonly Guid _tenantObjetivo = Guid.NewGuid();
    private readonly Guid _raiz = Guid.NewGuid();
    private readonly Guid _admin = Guid.NewGuid();
    private readonly Guid _sinAutoridad = Guid.NewGuid();
    private readonly Guid _beneficiario = Guid.NewGuid();

    private static readonly TimeSpan TopeDeVigencia = TimeSpan.FromDays(ConcesionPrivilegio.MaximoDiasDeVigencia);

    private Guid _concesionDelAdmin;

    public async Task InitializeAsync()
    {
        // Se siembra como propietario, que es lo que hacen el migrador y el arranque.
        await BaseDatosPostgresDePruebas.MigrarAsync(_cadenaConexion);

        await using var contexto = CrearContexto(_admin, cadena: _cadenaConexion);
        contexto.Tenants.Add(CrearTenant(_tenantPlataforma, "Plataforma", esPlataforma: true));
        contexto.Tenants.Add(CrearTenant(_tenantObjetivo, "Tenant objetivo S.L."));
        contexto.EstadoBootstrapPlataforma.Add(EstadoBootstrapPlataforma.Designar(_raiz, DateTime.UtcNow));

        var ahora = DateTime.UtcNow;
        var concesionDelAdmin = ConcesionPrivilegio.Global(
            _admin, ahora.AddMinutes(-10), ahora.AddDays(30), concedidaPorUsuarioId: _raiz);
        contexto.ConcesionesPrivilegio.Add(concesionDelAdmin);
        await contexto.SaveChangesAsync();

        _concesionDelAdmin = concesionDelAdmin.Id;
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    // ── Escrituras legítimas: siguen pasando ───────────────────────────────

    [Fact]
    public async Task La_raiz_arranca_la_plataforma_bajo_runtime_y_consume_el_bootstrap_en_el_mismo_SaveChanges()
    {
        var resultado = await AutoConcederAsync(_raiz, CapacidadPrivilegio.AdminPlataforma);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Codigo : "");

        await using var contexto = CrearContexto(_raiz, cadena: _cadenaConexion);
        var fundacional = await contexto.ConcesionesPrivilegio.SingleAsync(c => c.Id == resultado.Valor);
        fundacional.UsuarioPlataformaId.Should().Be(_raiz);
        fundacional.Capacidad.Should().Be(CapacidadPrivilegio.AdminPlataforma);
        fundacional.EsAlcanceGlobal.Should().BeTrue();
        fundacional.Origen.Should().Be(OrigenConcesion.BootstrapPlataforma);

        (await contexto.EstadoBootstrapPlataforma.SingleAsync()).Consumido.Should().BeTrue(
            "el INSERT de la concesión se ejecuta antes que el UPDATE del bootstrap, y por eso lo ve sin consumir");
    }

    [Fact]
    public async Task Un_AdminPlataforma_se_autoconcede_SoporteLectura_global_bajo_runtime()
    {
        var resultado = await AutoConcederAsync(_admin, CapacidadPrivilegio.SoporteLectura);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Codigo : "");
    }

    [Fact]
    public async Task Un_AdminPlataforma_concede_a_otra_persona_una_capacidad_acotada_y_finita_bajo_runtime()
    {
        // La misma fábrica y el mismo escritor que ConcederPrivilegioCommand.
        var ahora = DateTime.UtcNow;
        var concesion = ConcesionPrivilegio.SobreTenants(
            _beneficiario, CapacidadPrivilegio.Aprovisionamiento, [_tenantObjetivo],
            ahora, ahora.AddDays(7), concedidaPorUsuarioId: _admin, motivoConcesion: "Alta del Tenant objetivo");

        await using var contexto = CrearContexto(_admin);
        new PlataformaWriter(contexto).AnadirConcesion(concesion);
        await contexto.SaveChangesAsync();

        (await ContarComoPropietarioAsync(concesion.Id)).Should().Be(1);
    }

    [Fact]
    public async Task Cerrar_una_concesion_vigente_si_se_permite()
    {
        await using var contexto = CrearContexto(_admin);
        var concesion = await contexto.ConcesionesPrivilegio.SingleAsync(c => c.Id == _concesionDelAdmin);

        concesion.Revocar(DateTime.UtcNow);
        await contexto.SaveChangesAsync();

        (await EstadoComoPropietarioAsync(_concesionDelAdmin)).Should().Be("Revocada");
    }

    // ── Alta de fila propia ────────────────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task El_comando_revoca_bajo_runtime_y_audita_al_actor_real(bool beneficiario)
    {
        var concesionId = await SembrarSoporteLecturaAsync();
        var actor = beneficiario ? _beneficiario : _admin;
        await using (var contexto = CrearContexto(actor, auditar: ActorAuditoria.Normal(actor)))
        {
            var handler = new RevocarConcesionPrivilegioCommandHandler(contexto, contexto, Usuario(actor));
            var resultado = await handler.Handle(new(concesionId), CancellationToken.None);
            resultado.EsExitoso.Should().BeTrue();
        }

        await using var comprobacion = CrearContexto(actor, cadena: _cadenaConexion);
        var concesion = await comprobacion.ConcesionesPrivilegio.SingleAsync(c => c.Id == concesionId);
        concesion.Estado.Should().Be(EstadoConcesionPrivilegio.Revocada);
        var auditoria = await comprobacion.RegistrosAuditoria.SingleAsync(a =>
            a.EntidadTipo == nameof(ConcesionPrivilegio) && a.EntidadId == concesionId && a.Accion == "Modificado");
        auditoria.TenantId.Should().Be(_tenantPlataforma);
        auditoria.UsuarioId.Should().Be(actor);
        auditoria.ActorRealUsuarioId.Should().Be(actor);
        using var datos = System.Text.Json.JsonDocument.Parse(auditoria.DatosDespues!);
        datos.RootElement.GetProperty("Estado").GetInt32().Should().Be((int)EstadoConcesionPrivilegio.Revocada);
    }

    [Fact]
    public async Task La_auditoria_de_la_revocacion_separa_al_actor_real_del_usuario_simulado()
    {
        // En la aplicación, AutorizacionEscrituraBehavior rechaza este comando dentro de una Sesión
        // Privilegiada, así que aquí no se prueba un camino alcanzable: se fija bajo cae_app_runtime la
        // fila que dejaría, y que el interceptor no funde las dos identidades.
        var concesionId = await SembrarSoporteLecturaAsync();
        var actorReal = Guid.NewGuid();
        var actor = new ActorAuditoria(actorReal, _beneficiario, TipoViaAcceso.SesionPrivilegiada, Guid.NewGuid());
        await using (var contexto = CrearContexto(_beneficiario, auditar: actor))
        {
            var handler = new RevocarConcesionPrivilegioCommandHandler(contexto, contexto, Usuario(_beneficiario));
            (await handler.Handle(new(concesionId), CancellationToken.None)).EsExitoso.Should().BeTrue();
        }

        await using var comprobacion = CrearContexto(_beneficiario, cadena: _cadenaConexion);
        var auditoria = await comprobacion.RegistrosAuditoria.SingleAsync(a =>
            a.EntidadTipo == nameof(ConcesionPrivilegio) && a.EntidadId == concesionId && a.Accion == "Modificado");
        auditoria.UsuarioId.Should().Be(_beneficiario);
        auditoria.ActorRealUsuarioId.Should().Be(actorReal);
    }

    [Fact]
    public async Task El_comando_no_revoca_una_concesion_invisible_bajo_runtime()
    {
        var concesionId = await SembrarSoporteLecturaAsync();
        await using var contexto = CrearContexto(_sinAutoridad, auditar: ActorAuditoria.Normal(_sinAutoridad));
        var handler = new RevocarConcesionPrivilegioCommandHandler(contexto, contexto, Usuario(_sinAutoridad));
        var resultado = await handler.Handle(new(concesionId), CancellationToken.None);
        resultado.Error.Codigo.Should().Be("ConcesionPrivilegio.RevocacionNoPermitida");
        (await EstadoComoPropietarioAsync(concesionId)).Should().Be("Vigente");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task El_comando_no_revoca_AdminPlataforma_aunque_la_politica_lo_admita(bool beneficiario)
    {
        var actor = beneficiario ? _admin : _raiz;
        await using var contexto = CrearContexto(actor, auditar: ActorAuditoria.Normal(actor));
        var handler = new RevocarConcesionPrivilegioCommandHandler(contexto, contexto, Usuario(actor));
        var resultado = await handler.Handle(new(_concesionDelAdmin), CancellationToken.None);
        resultado.Error.Codigo.Should().Be("ConcesionPrivilegio.AdminPlataformaNoRevocable");
        (await EstadoComoPropietarioAsync(_concesionDelAdmin)).Should().Be("Vigente");
    }

    [Theory]
    [InlineData("BootstrapPlataforma")]
    [InlineData("Ordinaria")]
    public async Task Una_AdminPlataforma_global_propia_sin_bootstrap_se_rechaza(string origen)
    {
        await using var conexion = await AbrirRestringidaComoAsync(_sinAutoridad);

        var accion = async () => await InsertarAsync(
            conexion, beneficiario: _sinAutoridad, concedidaPor: _sinAutoridad, "AdminPlataforma",
            esAlcanceGlobal: true, conFin: false, origen: origen);

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege,
                "la rama de fila propia ya no admite cualquier capacidad: una AdminPlataforma global vigente " +
                "es la que leen app_es_admin_plataforma_global y app_destinatarios_aviso_seguridad");
    }

    [Fact]
    public async Task Quien_no_es_la_raiz_no_se_escribe_la_fundacional_aunque_el_bootstrap_siga_abierto()
    {
        await using var conexion = await AbrirRestringidaComoAsync(_admin);

        var accion = async () => await InsertarAsync(
            conexion, beneficiario: _admin, concedidaPor: _admin, "AdminPlataforma",
            esAlcanceGlobal: true, conFin: false, origen: "BootstrapPlataforma");

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Consumido_el_bootstrap_la_raiz_no_se_escribe_otra_AdminPlataforma()
    {
        (await AutoConcederAsync(_raiz, CapacidadPrivilegio.AdminPlataforma)).EsExitoso.Should().BeTrue();

        await using var conexion = await AbrirRestringidaComoAsync(_raiz);

        var accion = async () => await InsertarAsync(
            conexion, beneficiario: _raiz, concedidaPor: _raiz, "AdminPlataforma",
            esAlcanceGlobal: true, conFin: false, origen: "BootstrapPlataforma");

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege,
                "consumido es consumido: el acto fundacional no se repite por debajo del dominio");
    }

    [Fact]
    public async Task Sin_AdminPlataforma_no_se_autoconcede_SoporteLectura_por_debajo_del_dominio()
    {
        await using var conexion = await AbrirRestringidaComoAsync(_sinAutoridad);

        var accion = async () => await InsertarAsync(
            conexion, beneficiario: _sinAutoridad, concedidaPor: _sinAutoridad, "SoporteLectura",
            esAlcanceGlobal: true, conFin: true, origen: "Ordinaria");

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege,
                "la misma matriz que AutorizacionAutoConcesionPorMatriz: SoporteLectura exige AdminPlataforma vigente");
    }

    [Theory]
    [InlineData("SoporteLectura", false, true, "Ordinaria")]
    [InlineData("Aprovisionamiento", false, true, "Ordinaria")]
    [InlineData("BreakGlass", false, true, "Ordinaria")]
    [InlineData("SoporteLectura", true, true, "BootstrapPlataforma")]
    [InlineData("AdminPlataforma", true, false, "Ordinaria")]
    [InlineData("AdminPlataforma", true, true, "Ordinaria")]
    public async Task La_autoconcesion_de_un_AdminPlataforma_no_admite_otra_forma(
        string capacidad, bool esAlcanceGlobal, bool conFin, string origen)
    {
        await using var conexion = await AbrirRestringidaComoAsync(_admin);

        var accion = async () => await InsertarAsync(
            conexion, beneficiario: _admin, concedidaPor: _admin, capacidad, esAlcanceGlobal, conFin, origen);

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    // ── Alta a otra persona ────────────────────────────────────────────────

    [Theory]
    [InlineData("Aprovisionamiento", false, "Ordinaria")]
    [InlineData("RestablecimientoSegundoFactor", false, "Ordinaria")]
    [InlineData("Aprovisionamiento", true, "BootstrapPlataforma")]
    public async Task A_otra_persona_solo_se_concede_con_fin_y_de_origen_ordinario(
        string capacidad, bool conFin, string origen)
    {
        await using var conexion = await AbrirRestringidaComoAsync(_admin);

        var accion = async () => await InsertarAsync(
            conexion, beneficiario: _beneficiario, concedidaPor: _admin, capacidad,
            esAlcanceGlobal: false, conFin: conFin, origen: origen);

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    // ── Tope de vigencia al crear (TopeVigenciaConcesionEnPoliticasDeAlta) ─

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task En_el_tope_exacto_entra(bool autoconcesion)
    {
        await using var conexion = await AbrirRestringidaComoAsync(_admin);

        await InsertarAltaAsync(conexion, autoconcesion, TimeSpan.Zero, TopeDeVigencia);

        (await ContarConcesionesDeLaPuertaAsync(autoconcesion)).Should().Be(1,
            "90 días exactos es lo que admite el dominio: el tope de la base no puede ser más estrecho");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task La_vigencia_maxima_de_la_aplicacion_pasa_la_politica(bool autoconcesion)
    {
        // La misma fábrica y el mismo escritor que los dos comandos, con DateTime de .NET: el tope se
        // compara en microsegundos y la medida del dominio tiene que caber tal cual.
        var ahora = DateTime.UtcNow;
        var concesion = autoconcesion
            ? ConcesionPrivilegio.SoporteLecturaGlobal(
                _admin, ahora, ahora.AddDays(ConcesionPrivilegio.MaximoDiasDeVigencia), concedidaPorUsuarioId: _admin)
            : ConcesionPrivilegio.SobreTenants(
                _beneficiario, CapacidadPrivilegio.Aprovisionamiento, [_tenantObjetivo],
                ahora, ahora.AddDays(ConcesionPrivilegio.MaximoDiasDeVigencia),
                concedidaPorUsuarioId: _admin, motivoConcesion: "Alta del Tenant objetivo");

        await using var contexto = CrearContexto(_admin);
        new PlataformaWriter(contexto).AnadirConcesion(concesion);
        await contexto.SaveChangesAsync();

        (await ContarComoPropietarioAsync(concesion.Id)).Should().Be(1);
    }

    [Theory]
    [InlineData(true, 86400)]
    [InlineData(false, 86400)]
    [InlineData(true, 1)]
    [InlineData(false, 1)]
    public async Task Por_encima_del_tope_se_rechaza_por_politica(bool autoconcesion, int segundosDeMas)
    {
        await using var conexion = await AbrirRestringidaComoAsync(_admin);

        var accion = async () => await InsertarAltaAsync(
            conexion, autoconcesion, TimeSpan.Zero, TopeDeVigencia + TimeSpan.FromSeconds(segundosDeMas));

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege,
                "el tope de 90 días vive también en la base, con la misma medida que el dominio");
    }

    [Theory]
    [InlineData(true, -1440)]
    [InlineData(false, -1440)]
    [InlineData(true, -6)]
    [InlineData(false, -6)]
    [InlineData(true, 6)]
    [InlineData(false, 6)]
    [InlineData(true, 525600)]
    [InlineData(false, 525600)]
    public async Task Un_inicio_fuera_de_la_ventana_se_rechaza_por_politica(bool autoconcesion, int minutosDesdeAhora)
    {
        await using var conexion = await AbrirRestringidaComoAsync(_admin);

        var accion = async () => await InsertarAltaAsync(
            conexion, autoconcesion, TimeSpan.FromMinutes(minutosDesdeAhora), TimeSpan.FromDays(7));

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege,
                "el tope se mide desde el inicio, y el inicio tiene que estar junto al momento del alta");
    }

    [Theory]
    [InlineData(true, -4)]
    [InlineData(false, -4)]
    [InlineData(true, 4)]
    [InlineData(false, 4)]
    public async Task Un_inicio_dentro_de_la_ventana_entra(bool autoconcesion, int minutosDesdeAhora)
    {
        await using var conexion = await AbrirRestringidaComoAsync(_admin);

        await InsertarAltaAsync(conexion, autoconcesion, TimeSpan.FromMinutes(minutosDesdeAhora), TimeSpan.FromDays(7));

        (await ContarConcesionesDeLaPuertaAsync(autoconcesion)).Should().Be(1,
            "la ventana cubre el desfase de relojes y los reintentos de SaveChanges con el mismo inicio");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-525600)]
    public async Task La_fundacional_sin_fin_sigue_admitida_como_antes(int minutosDesdeAhora)
    {
        await using var conexion = await AbrirRestringidaComoAsync(_raiz);

        await InsertarConVigenciaAsync(
            conexion, _raiz, _raiz, "AdminPlataforma", TimeSpan.FromMinutes(minutosDesdeAhora), duracion: null,
            esAlcanceGlobal: true, origen: "BootstrapPlataforma");

        (await ContarFundacionalesAsync(_raiz)).Should().Be(1,
            "sin fin no hay nada que medir desde el inicio: la condición del tope no aplica a esta fila");
    }

    [Fact]
    public async Task Una_fundacional_con_fin_sobre_el_tope_se_rechaza()
    {
        await using var conexion = await AbrirRestringidaComoAsync(_raiz);

        var accion = async () => await InsertarConVigenciaAsync(
            conexion, _raiz, _raiz, "AdminPlataforma", TimeSpan.Zero, TopeDeVigencia + TimeSpan.FromDays(1),
            esAlcanceGlobal: true, origen: "BootstrapPlataforma");

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege,
                "ConcesionPrivilegio.RaizDeBootstrap aplica el mismo tope cuando se le da un fin");
    }

    [Fact]
    public async Task Una_fila_historica_sobre_el_tope_se_sigue_leyendo_y_cerrando_bajo_runtime()
    {
        var historica = Guid.NewGuid();
        await using (var propietario = new NpgsqlConnection(_cadenaConexion))
        {
            await propietario.OpenAsync();
            await using var sembrar = propietario.CreateCommand();
            sembrar.CommandText = @"
INSERT INTO public.""ConcesionesPrivilegio""
    (""Id"", ""UsuarioPlataformaId"", ""Capacidad"", ""EsAlcanceGlobal"", ""VigenciaDesde"", ""VigenciaHasta"",
     ""Estado"", ""ConcedidaPorUsuarioId"", ""MotivoConcesion"", ""CreadoEnUtc"", ""Version"", ""Origen"")
VALUES (@id, @beneficiario, 'SoporteLectura', true, now() - interval '1 day', now() + interval '400 days',
        'Vigente', @concedidaPor, 'Fila anterior al tope (test)', now() - interval '1 day', gen_random_uuid(), 'Ordinaria');";
            sembrar.Parameters.AddWithValue("id", historica);
            sembrar.Parameters.AddWithValue("beneficiario", _beneficiario);
            sembrar.Parameters.AddWithValue("concedidaPor", _admin);
            await sembrar.ExecuteNonQueryAsync();
        }

        await using var conexion = await AbrirRestringidaComoAsync(_beneficiario);

        await using (var leer = conexion.CreateCommand())
        {
            leer.CommandText = @"SELECT count(*) FROM public.""ConcesionesPrivilegio"" WHERE ""Id"" = @id;";
            leer.Parameters.AddWithValue("id", historica);
            (await leer.ExecuteScalarAsync()).Should().Be(1L, "el tope limita lo que nace, no lo que ya existe");
        }

        await using var cerrar = conexion.CreateCommand();
        cerrar.CommandText =
            @"UPDATE public.""ConcesionesPrivilegio"" SET ""Estado"" = 'Revocada', ""VigenciaHasta"" = now() WHERE ""Id"" = @id;";
        cerrar.Parameters.AddWithValue("id", historica);

        (await cerrar.ExecuteNonQueryAsync()).Should().Be(1,
            "una fila por encima del tope tiene que poder cerrarse: por eso el tope es política de INSERT y no CHECK de tabla");
        (await EstadoComoPropietarioAsync(historica)).Should().Be("Revocada");
    }

    // ── UPDATE y DELETE ────────────────────────────────────────────────────

    [Fact]
    public async Task Una_concesion_cerrada_no_vuelve_a_Vigente()
    {
        await EjecutarComoPropietarioAsync(
            @"UPDATE ""ConcesionesPrivilegio"" SET ""Estado"" = 'Revocada' WHERE ""Id"" = @id;", _concesionDelAdmin);

        await using var conexion = await AbrirRestringidaComoAsync(_admin);
        await using var comando = conexion.CreateCommand();
        comando.CommandText = @"UPDATE ""ConcesionesPrivilegio"" SET ""Estado"" = 'Vigente' WHERE ""Id"" = @id;";
        comando.Parameters.AddWithValue("id", _concesionDelAdmin);

        (await comando.ExecuteNonQueryAsync()).Should().Be(0,
            "el USING del UPDATE solo alcanza filas vigentes: una revocada no se puede seleccionar para cambiarla");
        (await EstadoComoPropietarioAsync(_concesionDelAdmin)).Should().Be("Revocada");
    }

    [Fact]
    public async Task Una_concesion_vigente_no_se_reescribe_sin_cerrarse()
    {
        await using var conexion = await AbrirRestringidaComoAsync(_admin);
        await using var comando = conexion.CreateCommand();
        comando.CommandText =
            @"UPDATE ""ConcesionesPrivilegio"" SET ""VigenciaHasta"" = NULL WHERE ""Id"" = @id;";
        comando.Parameters.AddWithValue("id", _concesionDelAdmin);

        var accion = async () => await comando.ExecuteNonQueryAsync();

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege,
                "la fila de llegada tiene que estar Revocada o Expirada: alargar una concesión vigente no es cerrarla");
    }

    [Fact]
    public async Task Una_fila_propia_que_nombra_a_otro_concedente_se_rechaza()
    {
        await using var conexion = await AbrirRestringidaComoAsync(_admin);

        var accion = async () => await InsertarAsync(
            conexion, beneficiario: _admin, concedidaPor: _beneficiario, "SoporteLectura",
            esAlcanceGlobal: true, conFin: true, origen: "Ordinaria");

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege,
                "una fila propia es propia en las dos coordenadas: atribuirla a otro concedente falsearía el rastro");
    }

    [Fact]
    public async Task Una_concesion_no_nace_cerrada_ni_en_otro_estado()
    {
        await using var conexion = await AbrirRestringidaComoAsync(_admin);

        var accion = async () => await InsertarAsync(
            conexion, beneficiario: _admin, concedidaPor: _admin, "SoporteLectura",
            esAlcanceGlobal: true, conFin: true, origen: "Ordinaria", estado: "Revocada");

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Quien_no_es_beneficiario_ni_concedente_no_cierra_una_concesion()
    {
        await using var conexion = await AbrirRestringidaComoAsync(_sinAutoridad);
        await using var comando = conexion.CreateCommand();
        comando.CommandText = @"UPDATE ""ConcesionesPrivilegio"" SET ""Estado"" = 'Revocada' WHERE ""Id"" = @id;";
        comando.Parameters.AddWithValue("id", _concesionDelAdmin);

        (await comando.ExecuteNonQueryAsync()).Should().Be(0,
            "el USING del UPDATE solo alcanza las concesiones que te nombran");
        (await EstadoComoPropietarioAsync(_concesionDelAdmin)).Should().Be("Vigente");
    }

    [Fact]
    public async Task Al_cerrar_no_se_reescribe_a_quien_ni_quien_concedio()
    {
        await using var conexion = await AbrirRestringidaComoAsync(_admin);
        await using var comando = conexion.CreateCommand();
        comando.CommandText =
            // Solo el concedente: cambiar también la capacidad tropezaría antes con un CHECK de la
            // tabla, que da el mismo código de error y haría pasar el test por otro motivo.
            @"UPDATE ""ConcesionesPrivilegio"" SET ""Estado"" = 'Revocada', ""ConcedidaPorUsuarioId"" = @otro
                  WHERE ""Id"" = @id;";
        comando.Parameters.AddWithValue("otro", _beneficiario);
        comando.Parameters.AddWithValue("id", _concesionDelAdmin);

        var accion = async () => await comando.ExecuteNonQueryAsync();

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation,
                "el WITH CHECK admite la fila cerrada, pero el trigger concesion_privilegio_solo_se_cierra exige " +
                "que al cerrar solo cambien Estado, VigenciaHasta y Version");
        (await EstadoComoPropietarioAsync(_concesionDelAdmin)).Should().Be("Vigente");
    }

    [Fact]
    public async Task Al_cerrar_no_se_alarga_el_fin()
    {
        var autoconcesion = await AutoConcederAsync(_admin, CapacidadPrivilegio.SoporteLectura);
        autoconcesion.EsExitoso.Should().BeTrue();

        await using var conexion = await AbrirRestringidaComoAsync(_admin);
        await using var comando = conexion.CreateCommand();
        comando.CommandText =
            @"UPDATE ""ConcesionesPrivilegio"" SET ""Estado"" = 'Revocada',
                  ""VigenciaHasta"" = ""VigenciaHasta"" + interval '10 years' WHERE ""Id"" = @id;";
        comando.Parameters.AddWithValue("id", autoconcesion.Valor);

        var accion = async () => await comando.ExecuteNonQueryAsync();

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation,
                "una concesión terminada no puede aparecer como vigente en una consulta histórica posterior a su fin");
    }

    [Fact]
    public async Task Una_concesion_no_se_borra()
    {
        await using var conexion = await AbrirRestringidaComoAsync(_admin);
        await using var comando = conexion.CreateCommand();
        comando.CommandText = @"DELETE FROM ""ConcesionesPrivilegio"" WHERE ""Id"" = @id;";
        comando.Parameters.AddWithValue("id", _concesionDelAdmin);

        (await comando.ExecuteNonQueryAsync()).Should().Be(0,
            "sin política de DELETE ningún rol sujeto a RLS borra el rastro de quién tuvo autoridad");
        (await ContarComoPropietarioAsync(_concesionDelAdmin)).Should().Be(1);
    }

    // ── EstadoBootstrapPlataforma: consumo monótono ────────────────────────

    [Fact]
    public async Task Consumido_el_bootstrap_la_raiz_no_lo_devuelve_a_sin_consumir()
    {
        (await AutoConcederAsync(_raiz, CapacidadPrivilegio.AdminPlataforma)).EsExitoso.Should().BeTrue();

        await using var conexion = await AbrirRestringidaComoAsync(_raiz);
        await using var comando = conexion.CreateCommand();
        comando.CommandText =
            @"UPDATE ""EstadoBootstrapPlataforma"" SET ""Consumido"" = false, ""ConsumidoEnUtc"" = NULL;";

        (await comando.ExecuteNonQueryAsync()).Should().Be(0,
            "el USING del consumo solo alcanza la fila sin consumir: reabrir el bootstrap es imposible");
        (await BootstrapConsumidoComoPropietarioAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task Sin_consumir_la_raiz_solo_puede_actualizar_su_fila_para_consumirla()
    {
        await using var conexion = await AbrirRestringidaComoAsync(_raiz);
        await using var comando = conexion.CreateCommand();
        comando.CommandText = @"UPDATE ""EstadoBootstrapPlataforma"" SET ""DesignadaEnUtc"" = now();";

        var accion = async () => await comando.ExecuteNonQueryAsync();

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege,
                "la fila de llegada tiene que estar consumida y con fecha");
        (await BootstrapConsumidoComoPropietarioAsync()).Should().BeFalse();
    }

    // ── Andamiaje ──────────────────────────────────────────────────────────

    private async Task<Domain.Common.Result<Guid>> AutoConcederAsync(Guid usuario, CapacidadPrivilegio capacidad)
    {
        await using var contexto = CrearContexto(usuario);
        var handler = new AutoConcederPrivilegioCommandHandler(
            new PlataformaWriter(contexto),
            new AutorizacionAutoConcesionPorMatriz(new RaizBootstrapPorIdentidadDesignada(contexto), contexto),
            contexto,
            Usuario(usuario),
            contexto);

        return await handler.Handle(new AutoConcederPrivilegioCommand(capacidad, DiasDeVigencia: 7), CancellationToken.None);
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

    private static async Task InsertarAsync(
        NpgsqlConnection conexion, Guid beneficiario, Guid concedidaPor, string capacidad,
        bool esAlcanceGlobal, bool conFin, string origen, string estado = "Vigente")
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = @"
INSERT INTO public.""ConcesionesPrivilegio""
    (""Id"", ""UsuarioPlataformaId"", ""Capacidad"", ""EsAlcanceGlobal"", ""VigenciaDesde"", ""VigenciaHasta"",
     ""Estado"", ""ConcedidaPorUsuarioId"", ""MotivoConcesion"", ""CreadoEnUtc"", ""Version"", ""Origen"")
VALUES (gen_random_uuid(), @beneficiario, @capacidad, @global, now(),
        CASE WHEN @conFin THEN now() + interval '1 day' END,
        @estado, @concedidaPor, 'Escritura por debajo del dominio (test)', now(), gen_random_uuid(), @origen);";
        comando.Parameters.AddWithValue("estado", estado);
        comando.Parameters.AddWithValue("beneficiario", beneficiario);
        comando.Parameters.AddWithValue("capacidad", capacidad);
        comando.Parameters.AddWithValue("global", esAlcanceGlobal);
        comando.Parameters.AddWithValue("conFin", conFin);
        comando.Parameters.AddWithValue("concedidaPor", concedidaPor);
        comando.Parameters.AddWithValue("origen", origen);
        await comando.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Una concesión con la forma exacta que admite cada puerta de alta: la autoconcesión de
    /// <c>SoporteLectura</c> o la concesión a otra persona. Lo único que varía es la vigencia, así que
    /// un rechazo solo puede venir de la condición del tope.
    /// </summary>
    private Task InsertarAltaAsync(NpgsqlConnection conexion, bool autoconcesion, TimeSpan inicio, TimeSpan duracion) =>
        autoconcesion
            ? InsertarConVigenciaAsync(conexion, _admin, _admin, "SoporteLectura", inicio, duracion, esAlcanceGlobal: true)
            : InsertarConVigenciaAsync(conexion, _beneficiario, _admin, "Aprovisionamiento", inicio, duracion, esAlcanceGlobal: false);

    /// <summary>
    /// <paramref name="inicio"/> es relativo a <c>now()</c> y <paramref name="duracion"/> al inicio. Se
    /// pasan en segundos por <c>make_interval(secs)</c> para que sean duraciones exactas: un
    /// <see cref="TimeSpan"/> llegaría como días, que es justo la aritmética de calendario que la
    /// política evita.
    /// </summary>
    private static async Task InsertarConVigenciaAsync(
        NpgsqlConnection conexion, Guid beneficiario, Guid concedidaPor, string capacidad,
        TimeSpan inicio, TimeSpan? duracion, bool esAlcanceGlobal, string origen = "Ordinaria")
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = @"
INSERT INTO public.""ConcesionesPrivilegio""
    (""Id"", ""UsuarioPlataformaId"", ""Capacidad"", ""EsAlcanceGlobal"", ""VigenciaDesde"", ""VigenciaHasta"",
     ""Estado"", ""ConcedidaPorUsuarioId"", ""MotivoConcesion"", ""CreadoEnUtc"", ""Version"", ""Origen"")
VALUES (gen_random_uuid(), @beneficiario, @capacidad, @global, now() + make_interval(secs => @inicio),
        now() + make_interval(secs => @inicio) + make_interval(secs => @duracion),
        'Vigente', @concedidaPor, 'Escritura por debajo del dominio (test)', now(), gen_random_uuid(), @origen);";
        comando.Parameters.AddWithValue("beneficiario", beneficiario);
        comando.Parameters.AddWithValue("capacidad", capacidad);
        comando.Parameters.AddWithValue("global", esAlcanceGlobal);
        comando.Parameters.AddWithValue("inicio", inicio.TotalSeconds);
        comando.Parameters.Add(new NpgsqlParameter("duracion", NpgsqlTypes.NpgsqlDbType.Double)
        {
            Value = duracion is { } valor ? valor.TotalSeconds : DBNull.Value,
        });
        comando.Parameters.AddWithValue("concedidaPor", concedidaPor);
        comando.Parameters.AddWithValue("origen", origen);
        await comando.ExecuteNonQueryAsync();
    }

    private Task<long> ContarConcesionesDeLaPuertaAsync(bool autoconcesion) =>
        ContarNuevasComoPropietarioAsync(autoconcesion ? _admin : _beneficiario, autoconcesion ? "SoporteLectura" : "Aprovisionamiento");

    private async Task<long> ContarNuevasComoPropietarioAsync(Guid beneficiario, string capacidad)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText =
            @"SELECT count(*) FROM ""ConcesionesPrivilegio"" WHERE ""UsuarioPlataformaId"" = @beneficiario AND ""Capacidad"" = @capacidad;";
        comando.Parameters.AddWithValue("beneficiario", beneficiario);
        comando.Parameters.AddWithValue("capacidad", capacidad);
        return (long)(await comando.ExecuteScalarAsync())!;
    }

    private Task<long> ContarFundacionalesAsync(Guid raiz) => ContarNuevasComoPropietarioAsync(raiz, "AdminPlataforma");

    private async Task EjecutarComoPropietarioAsync(string sql, Guid id)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        comando.Parameters.AddWithValue("id", id);
        await comando.ExecuteNonQueryAsync();
    }

    private async Task<long> ContarComoPropietarioAsync(Guid id)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = @"SELECT count(*) FROM ""ConcesionesPrivilegio"" WHERE ""Id"" = @id;";
        comando.Parameters.AddWithValue("id", id);
        return (long)(await comando.ExecuteScalarAsync())!;
    }

    private async Task<string> EstadoComoPropietarioAsync(Guid id)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = @"SELECT ""Estado"" FROM ""ConcesionesPrivilegio"" WHERE ""Id"" = @id;";
        comando.Parameters.AddWithValue("id", id);
        return (string)(await comando.ExecuteScalarAsync())!;
    }

    private async Task<bool> BootstrapConsumidoComoPropietarioAsync()
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = @"SELECT ""Consumido"" FROM ""EstadoBootstrapPlataforma"";";
        return (bool)(await comando.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// Una concesión revocable por el comando, sembrada como propietario: <c>SoporteLectura</c> de
    /// <c>_beneficiario</c>, concedida por <c>_admin</c>.
    /// </summary>
    private async Task<Guid> SembrarSoporteLecturaAsync()
    {
        var ahora = DateTime.UtcNow;
        var concesion = ConcesionPrivilegio.SoporteLecturaGlobal(
            _beneficiario, ahora.AddMinutes(-10), ahora.AddDays(30), concedidaPorUsuarioId: _admin);
        await using var contexto = CrearContexto(_admin, cadena: _cadenaConexion);
        contexto.ConcesionesPrivilegio.Add(concesion);
        await contexto.SaveChangesAsync();
        return concesion.Id;
    }

    private CurrentUserServiceFalso Usuario(Guid usuario) =>
        new(usuario, rol: null, tenantOrigenId: _tenantPlataforma, tieneDobleFactorActivo: true);

    /// <summary>
    /// Por defecto conecta como <c>cae_app_runtime</c>, con el contexto RLS firmado del usuario. Solo
    /// la siembra y las comprobaciones pasan la cadena del propietario.
    /// </summary>
    private CaeManagerDbContext CrearContexto(Guid usuario, string? cadena = null, ActorAuditoria? auditar = null)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenantPlataforma };
        var constructor = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(
                cadena ?? BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion),
                npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"));
        if (auditar is { } actor)
            constructor.AddInterceptors(new AuditoriaInterceptor(new ActorFijo(actor)));
        constructor.AddInterceptors(
            new TenantSelladoInterceptor(tenantActual),
            new TenantRlsConnectionInterceptor(
                tenantActual, new SinClienteSeleccionado(), Usuario(usuario),
                BaseDatosPostgresDePruebas.FirmanteContextoRls));
        // Con el auditor va la tubería de guardado de producción, en su orden: el UPDATE renueva
        // Version y compara la original, bajo RLS y con el trigger concesion_privilegio_solo_se_cierra.
        if (auditar is not null)
            constructor.AddInterceptors(new ConcurrenciaOptimistaInterceptor());

        return new CaeManagerDbContext(constructor.Options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private static Domain.Tenants.Tenant CrearTenant(Guid id, string nombre, bool esPlataforma = false)
    {
        var tenant = new Domain.Tenants.Tenant(nombre);
        typeof(Domain.Common.Entity).GetProperty(nameof(Domain.Common.Entity.Id))!.SetValue(tenant, id);
        if (esPlataforma) tenant.MarcarComoPlataforma();
        return tenant;
    }

    private sealed class ActorFijo(ActorAuditoria actor) : IActorAuditoria
    {
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(actor);
        public ActorAuditoria? ObtenerSiYaEstaResuelto() => actor;
    }

    private sealed class SinClienteSeleccionado : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;

        public Guid? AsignacionOperacionIdSeleccionada => null;

        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }
}
