using CaeManager.Application.Common;
using CaeManager.Application.Plataforma.Commands.AbrirSesionPrivilegiada;
using CaeManager.Domain.Plataforma;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;
using PlataformaWriter = CaeManager.Infrastructure.Plataforma.PlataformaWriter;
using ConcesionAdministradorPorPlataforma = CaeManager.Infrastructure.Plataforma.ConcesionAdministradorPorPlataforma;

namespace CaeManager.IntegrationTests.Plataforma;

/// <summary>
/// Decisión del 2026-10-30: un Actor de Plataforma TALVEG concede Administrador en un Tenant ya
/// existente, desde una Sesión Privilegiada con la capacidad <c>ConcesionAdministrador</c>, contra
/// PostgreSQL real y con la identidad de producción, <c>cae_app_runtime</c>, que con la sesión
/// adopta <c>cae_app_soporte</c>. El propietario solo siembra y lee.
///
/// <para>
/// Lo que se ataca: la conexión de la sesión sigue siendo de solo lectura y la RLS no se ensancha.
/// La única escritura posible es <c>app_conceder_administrador_por_plataforma</c>, y cada
/// precondición de sesión, concesión, Tenant, actor, motivo y cuenta tiene aquí un caso que la
/// incumple y deja las cuentas intactas y sin auditoría. Las reglas de Application están en
/// <c>ConcederAdministradorPorPlataformaCommandTests</c>.
/// </para>
/// </summary>
public class PlataformaConcedeAdministradorBajoRuntimeTests : IAsyncLifetime
{
    private const string Motivo = "Petición del Tenant, ticket SOP-0042";

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenantPlataforma = Guid.NewGuid();
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();
    private readonly Guid _tecnico = Guid.NewGuid();
    private readonly Guid _adminPlataforma = Guid.NewGuid();
    private readonly Guid _gestoraDeA = Guid.NewGuid();
    private readonly Guid _coordinadorDeA = Guid.NewGuid();
    private readonly Guid _sinActivarDeA = Guid.NewGuid();
    private readonly Guid _desactivadaDeA = Guid.NewGuid();
    private readonly Guid _administradorDeA = Guid.NewGuid();
    private readonly Guid _gestoraDeB = Guid.NewGuid();
    private Guid _rolAdministrador;
    private Guid _rolGestor;

    public async Task InitializeAsync()
    {
        await BaseDatosPostgresDePruebas.MigrarAsync(_cadenaConexion);

        await using var contexto = CrearContexto(_tenantPlataforma, cadena: _cadenaConexion);
        contexto.Tenants.Add(CrearTenant(_tenantPlataforma, "Plataforma", esPlataforma: true));
        contexto.Tenants.Add(CrearTenant(_tenantA, "Tenant A S.L."));
        contexto.Tenants.Add(CrearTenant(_tenantB, "Tenant B S.L."));

        // Los roles los siembra la migración base (HasData): se reutilizan.
        _rolAdministrador = (await contexto.Roles.SingleAsync(r => r.Name == Roles.Administrador)).Id;
        _rolGestor = (await contexto.Roles.SingleAsync(r => r.Name == Roles.GestorCae)).Id;

        SembrarCuenta(contexto, "tecnico", _tenantPlataforma, _tecnico);
        SembrarCuenta(contexto, "admin-plataforma", _tenantPlataforma, _adminPlataforma);
        SembrarCuenta(contexto, "coordinador", _tenantA, _coordinadorDeA, _rolGestor);
        SembrarCuenta(contexto, "gestora", _tenantA, _gestoraDeA, _rolGestor);
        SembrarCuenta(contexto, "sin-activar", _tenantA, _sinActivarDeA, _rolGestor);
        SembrarCuenta(contexto, "desactivada", _tenantA, _desactivadaDeA, _rolGestor);
        SembrarCuenta(contexto, "admin-a", _tenantA, _administradorDeA, _rolAdministrador);
        SembrarCuenta(contexto, "gestora-b", _tenantB, _gestoraDeB, _rolGestor);
        await contexto.SaveChangesAsync();

        var gestora = await contexto.Users.SingleAsync(u => u.Id == _gestoraDeA);
        gestora.CoordinadorUsuarioId = _coordinadorDeA;
        gestora.PasswordHash = "hash-de-prueba";
        var desactivada = await contexto.Users.SingleAsync(u => u.Id == _desactivadaDeA);
        desactivada.Desactivar();
        var sinActivar = await contexto.Users.SingleAsync(u => u.Id == _sinActivarDeA);
        sinActivar.PasswordHash = null;
        sinActivar.EmailConfirmed = false;
        await contexto.SaveChangesAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    // ── Camino positivo ──────────────────────────────────────────────────────

    [Fact]
    public async Task Con_la_capacidad_deja_Administrador_como_unico_rol_rota_el_sello_y_audita_al_Actor_real()
    {
        var sesion = await AbrirSesionConCapacidadAsync();
        var selloAntes = await SelloAsync(_gestoraDeA);

        await using (var contexto = CrearContexto(_tenantA, sesionPrivilegiadaId: sesion))
        {
            var resultado = await new ConcesionAdministradorPorPlataforma(contexto, new PuertaAccesoDatos())
                .ConcederAsync(sesion, _gestoraDeA, Motivo, CancellationToken.None);
            resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Mensaje : "");
        }

        (await RolesAsync(_gestoraDeA)).Should().Be("Administrador");
        (await SelloAsync(_gestoraDeA)).Should().NotBe(selloAntes, "la sesión abierta no conserva el rol anterior");
        (await EscalarAsync<bool>(@"SELECT ""CoordinadorUsuarioId"" IS NULL FROM ""AspNetUsers"" WHERE ""Id"" = @u;", _gestoraDeA))
            .Should().BeTrue("como EditarUsuarioCommand: el Coordinador CAE es de un Gestor CAE");

        var fila = (await LeerAuditoriaDeSesionAsync(sesion)).Should().ContainSingle().Subject;
        fila.Should().BeEquivalentTo(new
        {
            TenantId = _tenantA, EntidadTipo = "Usuario", EntidadId = _gestoraDeA, Accion = "Modificado",
            UsuarioId = (Guid?)_tecnico, ActorRealUsuarioId = (Guid?)_tecnico, TipoActor = "Persona",
        }, o => o.ExcludingMissingMembers());
        fila.DatosAntes.Should().Contain("GestorCae");
        fila.DatosDespues.Should().Contain(Motivo).And.Contain("Administrador");
        (fila.DatosAntes + fila.DatosDespues).Should().NotContain(selloAntes!, "el sello no entra en la auditoría");
    }

    [Fact]
    public async Task Sirve_una_cuenta_que_sigue_sin_activar()
    {
        var sesion = await AbrirSesionConCapacidadAsync();

        var selloAntes = await SelloAsync(_sinActivarDeA);

        (await ConcederComoPlataformaAsync(_tenantA, sesion, _sinActivarDeA)).Should().Be("concedido");

        (await RolesAsync(_sinActivarDeA)).Should().Be("Administrador");
        (await SelloAsync(_sinActivarDeA)).Should().Be(selloAntes, "rotarlo invalidaría su enlace de activación");
    }

    [Fact]
    public async Task Una_cuenta_sin_contrasena_con_acceso_externo_si_rota_el_sello()
    {
        var sesion = await AbrirSesionConCapacidadAsync();
        await EjecutarAsync(@"
INSERT INTO ""AspNetUserLogins"" (""LoginProvider"", ""ProviderKey"", ""ProviderDisplayName"", ""UserId"")
VALUES ('Microsoft', 'clave-externa', 'Microsoft', @u);", _sinActivarDeA);
        var selloAntes = await SelloAsync(_sinActivarDeA);

        (await ConcederComoPlataformaAsync(_tenantA, sesion, _sinActivarDeA)).Should().Be("concedido");

        (await SelloAsync(_sinActivarDeA)).Should().NotBe(selloAntes, "ya puede tener una sesión abierta");
    }

    [Fact]
    public async Task Un_motivo_de_exactamente_500_caracteres_con_espacios_alrededor_si_concede()
    {
        var sesion = await AbrirSesionConCapacidadAsync();

        (await ConcederComoPlataformaAsync(_tenantA, sesion, _gestoraDeA, "  " + new string('a', 500) + "  "))
            .Should().Be("concedido");
    }

    // ── Cada precondición de la función, incumplida ──────────────────────────

    [Fact]
    public async Task Otro_actor_no_puede_ejercer_la_sesion()
    {
        var sesion = await AbrirSesionConCapacidadAsync();
        var antes = await InstantaneaAsync();
        await using var contexto = CrearContexto(_tenantA, sesionPrivilegiadaId: sesion, actor: _adminPlataforma);

        (await LlamarFuncionAsync(contexto, sesion, _gestoraDeA, Motivo)).Should().Be("sesion_no_autorizada");
        (await InstantaneaAsync()).Should().Be(antes);
    }

    [Theory]
    [InlineData(@"UPDATE ""ConcesionesPrivilegio"" SET ""Estado"" = 'Revocada' WHERE ""Id"" = (SELECT ""ConcesionPrivilegioId"" FROM ""SesionesPrivilegiadas"" WHERE ""Id"" = @u);")]
    [InlineData(@"UPDATE ""ConcesionesPrivilegio"" SET ""VigenciaHasta"" = now() - interval '1 minute' WHERE ""Id"" = (SELECT ""ConcesionPrivilegioId"" FROM ""SesionesPrivilegiadas"" WHERE ""Id"" = @u);")]
    [InlineData(@"UPDATE ""TenantsAlcanzadosPorConcesion"" SET ""TenantId"" = gen_random_uuid() WHERE ""ConcesionPrivilegioId"" = (SELECT ""ConcesionPrivilegioId"" FROM ""SesionesPrivilegiadas"" WHERE ""Id"" = @u);")]
    [InlineData(@"UPDATE ""SesionesPrivilegiadas"" SET ""UsuarioSimuladoId"" = gen_random_uuid() WHERE ""Id"" = @u;")]
    [InlineData(@"UPDATE ""SesionesPrivilegiadas"" SET ""CerradaEnUtc"" = now() WHERE ""Id"" = @u;")]
    [InlineData(@"UPDATE ""SesionesPrivilegiadas"" SET ""ExpiraEnUtc"" = now() - interval '1 minute' WHERE ""Id"" = @u;")]
    public async Task Concesion_no_vigente_sin_Tenant_sesion_cerrada_o_con_simulacion_no_concede(string preparacion)
    {
        var sesion = await AbrirSesionConCapacidadAsync();
        await EjecutarAsync(preparacion, sesion);
        var antes = await InstantaneaAsync();

        (await ConcederComoPlataformaAsync(_tenantA, sesion, _gestoraDeA)).Should().Be("sesion_no_autorizada");
        (await InstantaneaAsync()).Should().Be(antes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Sin_motivo_no_concede(string motivo)
    {
        var sesion = await AbrirSesionConCapacidadAsync();
        var antes = await InstantaneaAsync();

        (await ConcederComoPlataformaAsync(_tenantA, sesion, _gestoraDeA, motivo)).Should().Be("motivo_no_valido");
        (await InstantaneaAsync()).Should().Be(antes);
    }

    [Fact]
    public async Task Un_motivo_de_mas_de_500_caracteres_no_concede()
    {
        var sesion = await AbrirSesionConCapacidadAsync();
        var antes = await InstantaneaAsync();

        (await ConcederComoPlataformaAsync(_tenantA, sesion, _gestoraDeA, new string('a', 501))).Should().Be("motivo_no_valido");
        (await InstantaneaAsync()).Should().Be(antes);
    }

    [Fact]
    public async Task Una_sesion_de_SoporteLectura_no_concede()
    {
        var sesion = await AbrirSesionDeSoporteLecturaAsync();
        var antes = await InstantaneaAsync();

        (await ConcederComoPlataformaAsync(_tenantA, sesion, _gestoraDeA)).Should().Be("sesion_no_autorizada");
        (await InstantaneaAsync()).Should().Be(antes);
    }

    [Theory]
    [InlineData(CapacidadPrivilegio.RestablecimientoSegundoFactor)]
    [InlineData(CapacidadPrivilegio.RevocacionOperadorCaeExterno)]
    public async Task Una_sesion_por_Tenant_de_otra_capacidad_acotada_no_concede(CapacidadPrivilegio capacidad)
    {
        var sesion = await AbrirSesionConCapacidadAsync(capacidad: capacidad);
        var antes = await InstantaneaAsync();

        (await ConcederComoPlataformaAsync(_tenantA, sesion, _gestoraDeA)).Should().Be("sesion_no_autorizada");
        (await InstantaneaAsync()).Should().Be(antes);
    }

    [Fact]
    public async Task Una_concesion_que_se_dio_el_propio_tecnico_no_concede()
    {
        var sesion = await AbrirSesionConCapacidadAsync(concedidaPor: _tecnico);
        var antes = await InstantaneaAsync();

        (await ConcederComoPlataformaAsync(_tenantA, sesion, _gestoraDeA)).Should().Be("sesion_no_autorizada");
        (await InstantaneaAsync()).Should().Be(antes);
    }

    [Fact]
    public async Task La_concesion_global_no_ampara_el_acto()
    {
        var sesion = await AbrirSesionConCapacidadAsync();
        await EjecutarAsync(
            @"UPDATE ""ConcesionesPrivilegio"" SET ""EsAlcanceGlobal"" = true WHERE ""Id"" = (SELECT ""ConcesionPrivilegioId"" FROM ""SesionesPrivilegiadas"" WHERE ""Id"" = @u);",
            sesion);
        var antes = await InstantaneaAsync();

        (await ConcederComoPlataformaAsync(_tenantA, sesion, _gestoraDeA)).Should().Be("sesion_no_autorizada");
        (await InstantaneaAsync()).Should().Be(antes);
    }

    [Fact]
    public async Task Un_actor_que_no_es_de_un_Tenant_de_plataforma_no_concede()
    {
        var sesion = await AbrirSesionConCapacidadAsync();
        await EjecutarAsync(@"UPDATE ""Tenants"" SET ""EsPlataforma"" = false WHERE ""Id"" = @u;", _tenantPlataforma);
        var antes = await InstantaneaAsync();

        (await ConcederComoPlataformaAsync(_tenantA, sesion, _gestoraDeA)).Should().Be("sesion_no_autorizada");
        (await InstantaneaAsync()).Should().Be(antes);
    }

    [Fact]
    public async Task No_crea_Administradores_en_un_Tenant_de_plataforma()
    {
        var sesion = await AbrirSesionConCapacidadAsync();
        await EjecutarAsync(@"UPDATE ""Tenants"" SET ""EsPlataforma"" = true WHERE ""Id"" = @u;", _tenantA);
        var antes = await InstantaneaAsync();

        (await ConcederComoPlataformaAsync(_tenantA, sesion, _gestoraDeA)).Should().Be("sesion_no_autorizada");
        (await InstantaneaAsync()).Should().Be(antes);
    }

    [Fact]
    public async Task Una_cuenta_de_otro_Tenant_no_se_alcanza()
    {
        var sesion = await AbrirSesionConCapacidadAsync();
        var antes = await InstantaneaAsync();

        (await ConcederComoPlataformaAsync(_tenantA, sesion, _gestoraDeB)).Should().Be("cuenta_no_encontrada");
        (await InstantaneaAsync()).Should().Be(antes);
    }

    [Fact]
    public async Task Una_sesion_sobre_otro_Tenant_no_ampara_el_contexto_de_A()
    {
        var sesionSobreB = await AbrirSesionConCapacidadAsync(tenantObjetivo: _tenantB);
        var antes = await InstantaneaAsync();

        (await ConcederComoPlataformaAsync(_tenantA, sesionSobreB, _gestoraDeA)).Should().Be("sesion_no_autorizada");
        (await InstantaneaAsync()).Should().Be(antes);
    }

    [Fact]
    public async Task Una_cuenta_desactivada_no_se_alcanza()
    {
        var sesion = await AbrirSesionConCapacidadAsync();
        var antes = await InstantaneaAsync();

        (await ConcederComoPlataformaAsync(_tenantA, sesion, _desactivadaDeA)).Should().Be("cuenta_no_encontrada");
        (await InstantaneaAsync()).Should().Be(antes);
    }

    [Fact]
    public async Task Quien_ya_es_Administrador_no_se_toca()
    {
        var sesion = await AbrirSesionConCapacidadAsync();
        var antes = await InstantaneaAsync();

        (await ConcederComoPlataformaAsync(_tenantA, sesion, _administradorDeA)).Should().Be("ya_es_administrador");
        (await InstantaneaAsync()).Should().Be(antes);
    }

    [Fact]
    public async Task Un_usuario_de_negocio_sin_sesion_no_puede_ejecutar_la_funcion()
    {
        // Con su conexión normal (cae_app_runtime): la función solo tiene EXECUTE para cae_app_soporte.
        await using var contexto = CrearContexto(_tenantA, actor: _administradorDeA, tenantOrigen: _tenantA);

        var llamada = () => LlamarFuncionAsync(contexto, Guid.NewGuid(), _gestoraDeA, Motivo);

        (await llamada.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        (await RolesAsync(_gestoraDeA)).Should().Be("GestorCae");
    }

    [Fact]
    public async Task La_conexion_de_la_sesion_sigue_sin_poder_escribir_los_roles_directamente()
    {
        var sesion = await AbrirSesionConCapacidadAsync();
        await using var contexto = CrearContexto(_tenantA, sesionPrivilegiadaId: sesion);

        var escritura = () => contexto.Database.ExecuteSqlAsync(
            $@"INSERT INTO ""AspNetUserRoles"" (""UserId"", ""RoleId"") VALUES ({_gestoraDeA}, {_rolAdministrador})");

        (await escritura.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege,
                "la capacidad abre un acto por la función, no un camino de escritura");
        (await RolesAsync(_gestoraDeA)).Should().Be("GestorCae");
    }

    [Fact]
    public async Task Espera_el_candado_de_Administradores_del_Tenant()
    {
        var sesion = await AbrirSesionConCapacidadAsync();
        await using var propietario = new NpgsqlConnection(_cadenaConexion);
        await propietario.OpenAsync();
        await using var retenedor = await propietario.BeginTransactionAsync();
        await using (var tomar = new NpgsqlCommand(
            "SELECT public.app_bloquear_administradores_de_tenant(@t);", propietario, retenedor))
        {
            tomar.Parameters.AddWithValue("t", _tenantA);
            await tomar.ExecuteNonQueryAsync();
        }

        var concesion = ConcederComoPlataformaAsync(_tenantA, sesion, _gestoraDeA);
        await EsperarEsperaDeCandadoAsync();
        concesion.IsCompleted.Should().BeFalse();
        await retenedor.CommitAsync();

        (await concesion).Should().Be("concedido");
    }

    [Theory]
    [InlineData(@"UPDATE ""SesionesPrivilegiadas"" SET ""CerradaEnUtc"" = now() WHERE ""Id"" = @u;")]
    [InlineData(@"UPDATE ""ConcesionesPrivilegio"" SET ""Estado"" = 'Revocada'
WHERE ""Id"" = (SELECT ""ConcesionPrivilegioId"" FROM ""SesionesPrivilegiadas"" WHERE ""Id"" = @u);")]
    public async Task Lo_que_se_cierra_o_se_revoca_durante_la_espera_del_candado_ya_no_ampara(string retirada)
    {
        var sesion = await AbrirSesionConCapacidadAsync();
        await using var propietario = new NpgsqlConnection(_cadenaConexion);
        await propietario.OpenAsync();
        await using var retenedor = await propietario.BeginTransactionAsync();
        await using (var tomar = new NpgsqlCommand(
            "SELECT public.app_bloquear_administradores_de_tenant(@t);", propietario, retenedor))
        {
            tomar.Parameters.AddWithValue("t", _tenantA);
            await tomar.ExecuteNonQueryAsync();
        }

        var concesion = ConcederComoPlataformaAsync(_tenantA, sesion, _gestoraDeA);
        await EsperarEsperaDeCandadoAsync();
        await EjecutarAsync(retirada, sesion);
        await retenedor.CommitAsync();

        (await concesion).Should().Be("sesion_no_autorizada");
        (await RolesAsync(_gestoraDeA)).Should().Be("GestorCae");
        (await LeerAuditoriaDeSesionAsync(sesion)).Should().BeEmpty();
    }

    // ── Preparación y lectura ────────────────────────────────────────────────

    private async Task<Guid> AbrirSesionConCapacidadAsync(
        Guid? concedidaPor = null,
        CapacidadPrivilegio capacidad = CapacidadPrivilegio.ConcesionAdministrador,
        Guid? tenantObjetivo = null)
    {
        var objetivo = tenantObjetivo ?? _tenantA;
        var ahora = DateTime.UtcNow;
        var concesion = ConcesionPrivilegio.SobreTenants(
            _tecnico, capacidad, [objetivo], ahora.AddMinutes(-10), ahora.AddDays(1),
            concedidaPorUsuarioId: concedidaPor ?? _adminPlataforma);
        await using (var contexto = CrearContexto(_tenantPlataforma, cadena: _cadenaConexion))
        {
            contexto.ConcesionesPrivilegio.Add(concesion);
            await contexto.SaveChangesAsync();
        }

        return await AbrirComoRuntimeAsync(concesion.Id, objetivo);
    }

    private async Task<Guid> AbrirSesionDeSoporteLecturaAsync()
    {
        // Sembrada como propietario: bajo runtime la auto-concesión exige AdminPlataforma vigente
        // (privilegio_autoconcesion), y aquí solo hace falta la sesión de SoporteLectura.
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

    private async Task<string> ConcederComoPlataformaAsync(
        Guid tenantDelContexto, Guid sesion, Guid usuario, string motivo = Motivo)
    {
        await using var contexto = CrearContexto(tenantDelContexto, sesionPrivilegiadaId: sesion);
        return await LlamarFuncionAsync(contexto, sesion, usuario, motivo);
    }

    // La misma llamada que ConcesionAdministradorPorPlataforma, para leer el código exacto.
    private static Task<string> LlamarFuncionAsync(CaeManagerDbContext contexto, Guid sesion, Guid usuario, string motivo) =>
        contexto.Database
            .SqlQuery<string>($"SELECT app_conceder_administrador_por_plataforma({sesion}, {usuario}, {motivo}) AS \"Value\"")
            .SingleAsync();

    private Task<string> RolesAsync(Guid usuario) => EscalarAsync<string>(@"
SELECT string_agg(r.""Name"", ',' ORDER BY r.""Name"") FROM ""AspNetUserRoles"" ur
JOIN ""AspNetRoles"" r ON r.""Id"" = ur.""RoleId"" WHERE ur.""UserId"" = @u;", usuario);

    private Task<string> SelloAsync(Guid usuario) =>
        EscalarAsync<string>(@"SELECT ""SecurityStamp"" FROM ""AspNetUsers"" WHERE ""Id"" = @u;", usuario);

    private async Task EsperarEsperaDeCandadoAsync()
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        var limite = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < limite)
        {
            await using var comando = new NpgsqlCommand(
                "SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND NOT granted AND database = (SELECT oid FROM pg_database WHERE datname = current_database());",
                conexion);
            if ((long)(await comando.ExecuteScalarAsync())! >= 1)
                return;
            await Task.Delay(50);
        }

        throw new TimeoutException("la concesión no llegó a esperar el candado de Administradores del Tenant");
    }

    private Task<string> InstantaneaAsync() => EscalarAsync<string>(@"
SELECT concat_ws('|',
 (SELECT coalesce(json_agg(row_to_json(t) ORDER BY t.""UserId"", t.""RoleId"")::text, '[]') FROM ""AspNetUserRoles"" t),
 (SELECT coalesce(json_agg(row_to_json(t) ORDER BY t.""Id"")::text, '[]') FROM ""AspNetUsers"" t),
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

    private static void SembrarCuenta(CaeManagerDbContext contexto, string nombre, Guid tenant, Guid id, Guid? rol = null)
    {
        var correo = $"{nombre}@conceder-plataforma.test";
        if (rol is { } rolId)
            contexto.Set<IdentityUserRole<Guid>>().Add(new IdentityUserRole<Guid> { UserId = id, RoleId = rolId });
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
