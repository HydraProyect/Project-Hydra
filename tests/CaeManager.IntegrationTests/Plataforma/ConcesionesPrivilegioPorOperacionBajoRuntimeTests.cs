using CaeManager.Application.Common;
using CaeManager.Application.Plataforma.Commands.AutoConcederPrivilegio;
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

    private CurrentUserServiceFalso Usuario(Guid usuario) =>
        new(usuario, rol: null, tenantOrigenId: _tenantPlataforma, tieneDobleFactorActivo: true);

    /// <summary>
    /// Por defecto conecta como <c>cae_app_runtime</c>, con el contexto RLS firmado del usuario. Solo
    /// la siembra y las comprobaciones pasan la cadena del propietario.
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
