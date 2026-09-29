using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Tenants;
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
/// PD-A3, commit 4: enforcement de la escritura acotada <b>en Postgres</b>, no
/// en el pipeline de la aplicación — mismo principio que
/// <see cref="SoloLecturaEnLaCapaDeDatosTests"/>, ahora para
/// <c>cae_app_aprovisionamiento</c>.
///
/// Dos capas separadas, cada una probando lo que de verdad garantiza:
/// <list type="bullet">
/// <item>SQL directo bajo <c>SET ROLE cae_app_aprovisionamiento</c>: el GRANT
/// del rol (commit 1) por sus efectos — INSERT dentro del tenant funciona,
/// fuera del tenant lo rechaza RLS, DELETE lo rechaza el GRANT mismo.</item>
/// <item>El circuito completo con <see cref="CaeManagerDbContext"/> real +
/// <see cref="TenantRlsConnectionInterceptor"/> real +
/// <see cref="AmbitoEscrituraPrivilegiada"/> establecido a mano (lo que
/// <c>ElevacionEscrituraAprovisionamientoBehavior</c> haría dentro de
/// MediatR, ya cubierto con dobles en Application): las TRES condiciones de
/// <c>DebeAdoptarRolDeAprovisionamiento</c> — ámbito abierto, sesión
/// coincidente, tenant coincidente — aisladas una por una.</item>
/// </list>
/// </summary>
public class EscrituraAprovisionamientoEnLaCapaDeDatosTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenantObjetivo = Guid.NewGuid();
    private readonly Guid _otroTenant = Guid.NewGuid();

    public Task InitializeAsync() => BaseDatosPostgresDePruebas.MigrarAsync(_cadenaConexion);

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    // ── El rol, en el catálogo ─────────────────────────────────────────────

    [Theory]
    [InlineData("SELECT", true)]
    [InlineData("INSERT", true)]
    [InlineData("UPDATE", true)]
    [InlineData("DELETE", false)]
    public async Task Los_privilegios_del_rol_sobre_Empresas_son_los_declarados(string privilegio, bool esperado)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();

        await using var comando = conexion.CreateCommand();
        comando.CommandText = "SELECT has_table_privilege('cae_app_aprovisionamiento', '\"Empresas\"', @privilegio);";
        comando.Parameters.AddWithValue("privilegio", privilegio);

        ((bool)(await comando.ExecuteScalarAsync())!).Should().Be(esperado);
    }

    // ── Tenants: solo las tres columnas del logo (selector de Tenant, lote 1) ──
    //
    // "Tenants" es administración de plano 3, no contenido CAE del alta: el rol no
    // puede crear, borrar ni cambiar un Tenant. Excepción declarada (contrato del
    // selector de Tenant, decisión 4 e invariante I8): Soporte TALVEG con
    // Aprovisionamiento escribe el logo, así que el rol actualiza esas tres
    // columnas y ninguna otra. El SELECT de tabla es el que exige cargar la
    // entidad con EF; cae_app_soporte ya lo tenía para el mismo actor.

    [Theory]
    [InlineData("SELECT", true)]
    [InlineData("INSERT", false)]
    [InlineData("UPDATE", false)]
    [InlineData("DELETE", false)]
    public async Task Los_privilegios_de_tabla_del_rol_sobre_Tenants_son_los_declarados(string privilegio, bool esperado)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();

        await using var comando = conexion.CreateCommand();
        comando.CommandText = "SELECT has_table_privilege('cae_app_aprovisionamiento', '\"Tenants\"', @privilegio);";
        comando.Parameters.AddWithValue("privilegio", privilegio);

        ((bool)(await comando.ExecuteScalarAsync())!).Should().Be(esperado);
    }

    [Fact]
    public async Task El_rol_solo_puede_actualizar_las_tres_columnas_del_logo_de_Tenants()
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();

        await using var comando = conexion.CreateCommand();
        comando.CommandText = """
            SELECT column_name FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = 'Tenants'
              AND has_column_privilege('cae_app_aprovisionamiento', '"Tenants"', column_name, 'UPDATE')
            ORDER BY column_name;
            """;
        var columnas = new List<string>();
        await using (var lector = await comando.ExecuteReaderAsync())
            while (await lector.ReadAsync())
                columnas.Add(lector.GetString(0));

        columnas.Should().Equal("LogoActualizadoEnUtc", "LogoArchivoClave", "LogoVersion");
    }

    [Fact]
    public async Task Por_SQL_el_rol_cambia_el_logo_pero_ninguna_otra_columna_de_un_Tenant()
    {
        var tenantId = await CrearTenantAsync();
        await using var conexion = await AbrirComoAprovisionamientoAsync(tenantId);

        async Task<int> ActualizarAsync(string asignacion)
        {
            await using var comando = conexion.CreateCommand();
            comando.CommandText = $"UPDATE \"Tenants\" SET {asignacion} WHERE \"Id\" = @id;";
            comando.Parameters.AddWithValue("id", tenantId);
            return await comando.ExecuteNonQueryAsync();
        }

        (await ActualizarAsync(
                "\"LogoVersion\" = '0123456789abcdef', \"LogoArchivoClave\" = 'x/y.png', \"LogoActualizadoEnUtc\" = now()"))
            .Should().Be(1, "control positivo: las columnas del logo sí");

        foreach (var asignacion in new[]
                 {
                     "\"Nombre\" = 'Renombrado'",
                     "\"EstadoComercial\" = \"EstadoComercial\"",
                     "\"Estado\" = \"Estado\"",
                     "\"EsPlataforma\" = true",
                     "\"PuedeActuarComoOperadorCaeExterno\" = true",
                     "\"StripeCustomerId\" = 'cus_x'",
                 })
        {
            var accion = async () => await ActualizarAsync(asignacion);
            (await accion.Should().ThrowAsync<PostgresException>(asignacion))
                .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege, asignacion);
        }
    }

    [Fact]
    public async Task Elevado_por_el_ambito_EF_guarda_el_logo_de_un_Tenant()
    {
        var tenantId = await CrearTenantAsync();
        var sesionId = Guid.NewGuid();
        var clave = $"{tenantId:N}/{Guid.NewGuid():N}.png";

        await using (var contexto = CrearContexto(tenantId, sesionPrivilegiadaId: sesionId, tenantSeleccionado: tenantId))
        using (AmbitoEscrituraPrivilegiada.Establecer(sesionId, tenantId))
        {
            var tenant = await contexto.Tenants.SingleAsync(t => t.Id == tenantId);
            tenant.EstablecerLogo(clave, "0123456789abcdef", DateTime.UtcNow);
            await contexto.SaveChangesAsync();
        }

        await using var lectura = CrearContexto(tenantId);
        (await lectura.Tenants.SingleAsync(t => t.Id == tenantId)).LogoArchivoClave.Should().Be(clave);
    }

    [Fact]
    public async Task Elevado_por_el_ambito_EF_no_puede_renombrar_un_Tenant()
    {
        var tenantId = await CrearTenantAsync();
        var sesionId = Guid.NewGuid();

        await using var contexto = CrearContexto(tenantId, sesionPrivilegiadaId: sesionId, tenantSeleccionado: tenantId);
        using (AmbitoEscrituraPrivilegiada.Establecer(sesionId, tenantId))
        {
            var tenant = await contexto.Tenants.SingleAsync(t => t.Id == tenantId);
            tenant.RenombrarA("Renombrado por aprovisionamiento");

            var accion = async () => await contexto.SaveChangesAsync();

            (await accion.Should().ThrowAsync<DbUpdateException>())
                .Which.InnerException.Should().BeOfType<PostgresException>()
                .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        }
    }

    [Fact]
    public async Task Dos_subidas_simultaneas_del_logo_una_gana_y_la_otra_da_conflicto()
    {
        // LogoVersion es token de concurrencia (revisión Codex C9): la segunda no puede pisar la
        // columna de la primera ni dejarla apuntando a un blob que la primera borraría.
        var tenantId = await CrearTenantAsync();
        await using var primera = CrearContexto(tenantId);
        await using var segunda = CrearContexto(tenantId);
        var enPrimera = await primera.Tenants.SingleAsync(t => t.Id == tenantId);
        var enSegunda = await segunda.Tenants.SingleAsync(t => t.Id == tenantId);

        enPrimera.EstablecerLogo($"{tenantId:N}/{Guid.NewGuid():N}.png", "aaaaaaaaaaaaaaaa", DateTime.UtcNow);
        await primera.SaveChangesAsync();

        enSegunda.EstablecerLogo($"{tenantId:N}/{Guid.NewGuid():N}.png", "bbbbbbbbbbbbbbbb", DateTime.UtcNow);
        var accion = async () => await segunda.SaveChangesAsync();

        await accion.Should().ThrowAsync<DbUpdateConcurrencyException>();
        await using var lectura = CrearContexto(tenantId);
        (await lectura.Tenants.SingleAsync(t => t.Id == tenantId)).LogoVersion.Should().Be("aaaaaaaaaaaaaaaa");
    }

    // ── El rol, por sus efectos (SQL directo, bajo cae_app_runtime) ────────

    [Fact]
    public async Task El_rol_inserta_dentro_del_tenant_objetivo()
    {
        await using var conexion = await AbrirComoAprovisionamientoAsync(_tenantObjetivo);

        var filas = await InsertarEmpresaAsync(conexion, _tenantObjetivo);

        filas.Should().Be(1);
    }

    [Fact]
    public async Task El_rol_no_inserta_fuera_del_tenant_fijado()
    {
        // WITH CHECK ("TenantId" = app.tenant_id) de la política base: el rol
        // tiene GRANT de tabla, pero RLS sigue siendo la primera barrera.
        await using var conexion = await AbrirComoAprovisionamientoAsync(_tenantObjetivo);

        var accion = async () => await InsertarEmpresaAsync(conexion, _otroTenant);

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task El_rol_no_puede_borrar_ni_dentro_de_su_propio_tenant()
    {
        await using var conexion = await AbrirComoAprovisionamientoAsync(_tenantObjetivo);
        await InsertarEmpresaAsync(conexion, _tenantObjetivo);

        await using var comando = conexion.CreateCommand();
        comando.CommandText = "DELETE FROM \"Empresas\" WHERE \"TenantId\" = @tenant;";
        comando.Parameters.AddWithValue("tenant", _tenantObjetivo);

        var accion = async () => await comando.ExecuteNonQueryAsync();

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege,
                "ninguna línea del GRANT de la migración concede DELETE");
    }

    // ── El circuito completo, extremo a extremo ─────────────────────────────

    [Fact]
    public async Task Con_las_tres_condiciones_el_ambito_eleva_y_EF_guarda()
    {
        var sesionId = Guid.NewGuid();

        await using var contexto = CrearContexto(
            _tenantObjetivo, sesionPrivilegiadaId: sesionId, tenantSeleccionado: _tenantObjetivo);

        using (AmbitoEscrituraPrivilegiada.Establecer(sesionId, _tenantObjetivo))
        {
            contexto.Empresas.Add(Empresa.CrearComoCliente(
                "Alta por aprovisionamiento S.L.", "B11111119", esCritico: false, notas: null, ejecutivoUsuarioId: null));

            await contexto.SaveChangesAsync();
        }

        (await contexto.Empresas.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Sin_ambito_abierto_la_escritura_cae_a_soporte_y_falla()
    {
        // La cookie sola no basta — invariante central del diseño. Aquí hay
        // SesionPrivilegiadaIdSeleccionada, pero nadie abrió el AsyncLocal.
        var sesionId = Guid.NewGuid();

        await using var contexto = CrearContexto(
            _tenantObjetivo, sesionPrivilegiadaId: sesionId, tenantSeleccionado: _tenantObjetivo);

        contexto.Empresas.Add(Empresa.CrearComoCliente(
            "Sin ambito S.L.", "B22222228", esCritico: false, notas: null, ejecutivoUsuarioId: null));

        var accion = async () => await contexto.SaveChangesAsync();

        (await accion.Should().ThrowAsync<DbUpdateException>())
            .Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Un_ambito_de_otra_sesion_no_eleva_y_la_escritura_falla()
    {
        // El AsyncLocal existe, pero nombra una sesión distinta de la que trae
        // la cookie de esta conexión — no puede venir de otra request real
        // (AsyncLocal no cruza async contexts independientes), pero aísla la
        // condición de comparación de sesión por si acaso se filtrara.
        var sesionDelAmbito = Guid.NewGuid();
        var sesionDeLaCookie = Guid.NewGuid();

        await using var contexto = CrearContexto(
            _tenantObjetivo, sesionPrivilegiadaId: sesionDeLaCookie, tenantSeleccionado: _tenantObjetivo);

        using (AmbitoEscrituraPrivilegiada.Establecer(sesionDelAmbito, _tenantObjetivo))
        {
            contexto.Empresas.Add(Empresa.CrearComoCliente(
                "Sesion no coincide S.L.", "B33333337", esCritico: false, notas: null, ejecutivoUsuarioId: null));

            var accion = async () => await contexto.SaveChangesAsync();

            (await accion.Should().ThrowAsync<DbUpdateException>())
                .Which.InnerException.Should().BeOfType<PostgresException>()
                .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        }
    }

    [Fact]
    public async Task Un_ambito_sobre_otro_tenant_no_eleva_y_la_escritura_falla()
    {
        var sesionId = Guid.NewGuid();

        // El tenant activo de ESTA conexión es _otroTenant, pero el ámbito de
        // elevación se abrió para _tenantObjetivo — el interceptor exige que
        // ITenantActual.TenantId coincida con el del ámbito en este instante,
        // no que exista un ámbito cualquiera.
        await using var contexto = CrearContexto(
            _otroTenant, sesionPrivilegiadaId: sesionId, tenantSeleccionado: _otroTenant);

        using (AmbitoEscrituraPrivilegiada.Establecer(sesionId, _tenantObjetivo))
        {
            contexto.Empresas.Add(Empresa.CrearComoCliente(
                "Tenant no coincide S.L.", "B44444446", esCritico: false, notas: null, ejecutivoUsuarioId: null));

            var accion = async () => await contexto.SaveChangesAsync();

            (await accion.Should().ThrowAsync<DbUpdateException>())
                .Which.InnerException.Should().BeOfType<PostgresException>()
                .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        }
    }

    [Fact]
    public async Task Al_cerrar_el_ambito_la_conexion_vuelve_a_soporte_y_deja_de_poder_escribir()
    {
        var sesionId = Guid.NewGuid();

        await using var contexto = CrearContexto(
            _tenantObjetivo, sesionPrivilegiadaId: sesionId, tenantSeleccionado: _tenantObjetivo);

        using (AmbitoEscrituraPrivilegiada.Establecer(sesionId, _tenantObjetivo))
        {
            contexto.Empresas.Add(Empresa.CrearComoCliente(
                "Dentro del ambito S.L.", "B55555551", esCritico: false, notas: null, ejecutivoUsuarioId: null));
            await contexto.SaveChangesAsync();
        }

        // El using ya se cerró: SET ROLE cae_app_soporte, nunca RESET ROLE.
        contexto.Empresas.Add(Empresa.CrearComoCliente(
            "Fuera del ambito S.L.", "B66666660", esCritico: false, notas: null, ejecutivoUsuarioId: null));

        var accion = async () => await contexto.SaveChangesAsync();

        (await accion.Should().ThrowAsync<DbUpdateException>())
            .Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege,
                "cerrado el ámbito, la MISMA conexión ya no puede escribir — la elevación no se queda pegada");
    }

    // ── Andamiaje ──────────────────────────────────────────────────────────

    private CaeManagerDbContext CrearContexto(Guid tenantId, Guid? sesionPrivilegiadaId = null, Guid? tenantSeleccionado = null)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(
                new TenantSelladoInterceptor(tenantActual),
                new TenantRlsConnectionInterceptor(
                    tenantActual,
                    new ClienteActivoSeleccionadoFalso(tenantSeleccionado, sesionPrivilegiadaId),
                    new CurrentUserServiceFalso(Guid.NewGuid(), tenantOrigenId: tenantId), BaseDatosPostgresDePruebas.FirmanteContextoRls))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private async Task<Guid> CrearTenantAsync()
    {
        await using var contexto = CrearContexto(Guid.NewGuid());
        var tenant = new Tenant("Tenant con logo de prueba");
        contexto.Tenants.Add(tenant);
        await contexto.SaveChangesAsync();
        return tenant.Id;
    }

    private async Task<NpgsqlConnection> AbrirComoAprovisionamientoAsync(Guid tenantId)
    {
        var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();

        await using (var fijar = conexion.CreateCommand())
        {
            fijar.CommandText = "SELECT set_config('app.tenant_id', @tenantId, false);";
            fijar.Parameters.AddWithValue("tenantId", tenantId.ToString());
            await fijar.ExecuteNonQueryAsync();
        }

        await using var setRol = conexion.CreateCommand();
        setRol.CommandText = "SET ROLE cae_app_aprovisionamiento;";
        await setRol.ExecuteNonQueryAsync();

        return conexion;
    }

    private static async Task<int> InsertarEmpresaAsync(NpgsqlConnection conexion, Guid tenantId)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = @"
INSERT INTO ""Empresas""
    (""Id"", ""TenantId"", ""RazonSocial"", ""Cif"", ""EsPropia"", ""EsActividadAnexoI"", ""CreadoEnUtc"", ""EstaEliminado"", ""Version"")
VALUES (gen_random_uuid(), @tenant, 'Alta SQL directo S.L.', 'B99999996', false, false, now(), false, gen_random_uuid());";
        comando.Parameters.AddWithValue("tenant", tenantId);

        return await comando.ExecuteNonQueryAsync();
    }

    private sealed class ClienteActivoSeleccionadoFalso(Guid? tenantSeleccionado, Guid? sesionPrivilegiadaId)
        : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => tenantSeleccionado;

        public Guid? AsignacionOperacionIdSeleccionada => null;

        public Guid? SesionPrivilegiadaIdSeleccionada => sesionPrivilegiadaId;
    }
}
