using CaeManager.Application.Common;
using CaeManager.Domain.Empresas;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Plataforma;

/// <summary>
/// Enforcement de solo lectura del plano 3 <b>en Postgres</b>, no en el
/// pipeline de la aplicación (ADR-011 § 4bis.7.4).
///
/// La diferencia importa. <c>AutorizacionEscrituraBehavior</c> deniega la
/// escritura de una sesión privilegiada, pero solo ve lo que pasa por MediatR:
/// es una lista que hay que acordarse de respetar. Estos tests atacan por
/// debajo de esa lista — SQL directo sobre la conexión, y un
/// <c>SaveChangesAsync</c> de EF que no pasa por ningún behavior— y exigen que
/// la escritura falle igual.
///
/// Y exigen la otra mitad, que es la que hace útil al rol: que la lectura siga
/// funcionando <b>y siga acotada por RLS</b>. Un rol de solo lectura que además
/// ignorase el aislamiento por tenant habría comprado una fuga mucho peor que
/// la que evita, así que <c>NOBYPASSRLS</c> se comprueba por sus efectos y
/// también en el catálogo.
/// </summary>
public class SoloLecturaEnLaCapaDeDatosTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenantVisitado = Guid.NewGuid();
    private readonly Guid _otroTenant = Guid.NewGuid();
    private Guid _clienteId;

    public async Task InitializeAsync()
    {
        // Se siembra como el rol propietario, que es lo que hace la app hoy al
        // migrar: el rol de soporte no puede crear nada, justamente.
        await using var contexto = CrearContexto(_tenantVisitado);
        await contexto.Database.MigrateAsync();

        var cliente = Empresa.CrearComoCliente(
            "Cliente Visitado S.L.", "B12345674", esCritico: false, notas: null, ejecutivoUsuarioId: null);
        contexto.Empresas.Add(cliente);
        await contexto.SaveChangesAsync();
        _clienteId = cliente.Id;

        await using var contextoAjeno = CrearContexto(_otroTenant);
        contextoAjeno.Empresas.Add(Empresa.CrearComoCliente(
            "Cliente Ajeno S.L.", "B87654323", esCritico: false, notas: null, ejecutivoUsuarioId: null));
        await contextoAjeno.SaveChangesAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    // ── El rol, por sus efectos ────────────────────────────────────────────

    [Fact]
    public async Task El_rol_de_soporte_lee_las_filas_del_tenant_que_visita()
    {
        // Control positivo. Sin esto, el resto podría estar pasando porque el
        // rol no ve absolutamente nada, que no es solo lectura sino ceguera.
        await using var conexion = await AbrirComoSoporteAsync(_tenantVisitado);

        (await ContarClientesAsync(conexion)).Should().Be(1);
    }

    [Fact]
    public async Task El_rol_de_soporte_no_ve_las_filas_de_otro_tenant()
    {
        // NOBYPASSRLS por sus efectos: el rol es de solo lectura, pero la
        // lectura sigue acotada. Un rol de soporte que leyera todos los tenants
        // sería una fuga peor que la escritura que este rol impide.
        await using var conexion = await AbrirComoSoporteAsync(_otroTenant);

        (await ContarClientesAsync(conexion)).Should().Be(1, "cada tenant tiene exactamente un cliente sembrado");

        await FijarTenantAsync(conexion, _tenantVisitado);
        var nombres = await LeerNombresClientesAsync(conexion);
        nombres.Should().ContainSingle().Which.Should().Be("Cliente Visitado S.L.");
    }

    [Fact]
    public async Task El_rol_de_soporte_no_ve_nada_sin_tenant_de_sesion()
    {
        await using var conexion = await AbrirComoSoporteAsync(tenantId: null);

        (await ContarClientesAsync(conexion)).Should().Be(0, "fallo cerrado: sin tenant fijado, ninguna fila");
    }

    [Theory]
    [InlineData("INSERT INTO \"Empresas\" (\"Id\", \"TenantId\", \"RazonSocial\", \"Cif\", \"EsPropia\", \"EsActividadAnexoI\", \"CreadoEnUtc\", \"EstaEliminado\", \"Version\") VALUES (gen_random_uuid(), @tenant, 'Intruso S.L.', 'B00000000', false, false, now(), false, gen_random_uuid());")]
    [InlineData("UPDATE \"Empresas\" SET \"RazonSocial\" = 'Manipulado' WHERE \"Id\" = @cliente;")]
    [InlineData("DELETE FROM \"Empresas\" WHERE \"Id\" = @cliente;")]
    public async Task El_rol_de_soporte_no_puede_escribir_ni_con_sql_directo(string sql)
    {
        // Por debajo de MediatR, de los repositorios y del propio EF: si la
        // garantía dependiera de la capa de aplicación, esto pasaría.
        await using var conexion = await AbrirComoSoporteAsync(_tenantVisitado);

        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        comando.Parameters.AddWithValue("tenant", _tenantVisitado);
        comando.Parameters.AddWithValue("cliente", _clienteId);

        var accion = async () => await comando.ExecuteNonQueryAsync();

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    // ── El rol, en el catálogo ─────────────────────────────────────────────

    [Fact]
    public async Task El_rol_de_soporte_no_puede_saltarse_rls_ni_iniciar_sesion_por_su_cuenta()
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();

        await using var comando = conexion.CreateCommand();
        comando.CommandText =
            "SELECT rolbypassrls, rolsuper, rolcanlogin, rolcreatedb, rolcreaterole " +
            "FROM pg_roles WHERE rolname = 'cae_app_soporte';";

        await using var lector = await comando.ExecuteReaderAsync();
        (await lector.ReadAsync()).Should().BeTrue("la migración RolSoporteSoloLectura tiene que haber creado el rol");

        lector.GetBoolean(0).Should().BeFalse("con BYPASSRLS el rol vería todos los tenants");
        lector.GetBoolean(1).Should().BeFalse("un superusuario ignora RLS y todos los permisos");
        lector.GetBoolean(2).Should().BeFalse("no es un rol de conexión: se adopta con SET ROLE desde el rol de la app");
        lector.GetBoolean(3).Should().BeFalse();
        lector.GetBoolean(4).Should().BeFalse();
    }

    [Theory]
    [InlineData("SELECT", true)]
    [InlineData("INSERT", false)]
    [InlineData("UPDATE", false)]
    [InlineData("DELETE", false)]
    public async Task Los_privilegios_del_rol_de_soporte_sobre_las_tablas_son_los_declarados(string privilegio, bool esperado)
    {
        // Se comprueban en el catálogo además de por sus efectos: los efectos
        // prueban una tabla, esto prueba la concesión.
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();

        await using var comando = conexion.CreateCommand();
        comando.CommandText = "SELECT has_table_privilege('cae_app_soporte', '\"Empresas\"', @privilegio);";
        comando.Parameters.AddWithValue("privilegio", privilegio);

        ((bool)(await comando.ExecuteScalarAsync())!).Should().Be(esperado);
    }

    // ── El interceptor, extremo a extremo ──────────────────────────────────

    [Fact]
    public async Task Con_sesion_privilegiada_en_el_token_EF_no_puede_guardar()
    {
        // La prueba que cierra el círculo: un DbContext real, con el
        // interceptor real, guardando por el camino normal de EF — sin pasar
        // por MediatR y por tanto sin que AutorizacionEscrituraBehavior tenga
        // nada que decir. Falla en la base.
        await using var contexto = CrearContexto(_tenantVisitado, sesionPrivilegiadaId: Guid.NewGuid());
        contexto.Empresas.Add(Empresa.CrearComoCliente(
            "Escrito por soporte S.L.", "B66666678", esCritico: false, notas: null, ejecutivoUsuarioId: null));

        var accion = async () => await contexto.SaveChangesAsync();

        var excepcion = await accion.Should().ThrowAsync<DbUpdateException>();

        excepcion.Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Con_sesion_privilegiada_en_el_token_EF_si_puede_leer()
    {
        await using var contexto = CrearContexto(_tenantVisitado, sesionPrivilegiadaId: Guid.NewGuid());

        var clientes = await contexto.Empresas.ToListAsync();

        clientes.Should().ContainSingle().Which.RazonSocial.Should().Be("Cliente Visitado S.L.");
    }

    [Fact]
    public async Task Sin_sesion_privilegiada_en_el_token_la_escritura_sigue_funcionando()
    {
        // Guarda de no regresión, y la más importante de este incremento: el
        // rol solo se adopta cuando el token nombra una sesión privilegiada.
        // Adoptarlo de más dejaría la aplicación entera en solo lectura.
        await using var contexto = CrearContexto(_tenantVisitado);
        contexto.Empresas.Add(Empresa.CrearComoCliente(
            "Operador normal S.L.", "B33333337", esCritico: false, notas: null, ejecutivoUsuarioId: null));

        await contexto.SaveChangesAsync();

        (await contexto.Empresas.CountAsync()).Should().Be(2);
    }

    /// <summary>
    /// <b>El mismo circuito, bajo la identidad de conexión de PRODUCCIÓN.</b>
    ///
    /// <para>
    /// Todo lo de arriba adopta <c>cae_app_soporte</c> desde la conexión
    /// PROPIETARIA, y un propietario puede adoptar cualquier rol <b>sin ser
    /// miembro de nada</b>. Eso hacía que el enforcement de solo lectura del
    /// plano 3 estuviera demostrado por un camino que producción no recorre:
    /// allí la identidad de conexión es <c>cae_app_runtime</c>, y el
    /// <c>SET ROLE</c> exige una membresía que nadie concedía. La comprobación
    /// que faltaba no era del producto: era del instrumento.
    /// </para>
    ///
    /// <para>
    /// <b>La lectura va primero, y no es adorno.</b> Sin membresía, el propio
    /// <c>SET ROLE</c> falla con <c>42501</c> — el mismo código que una
    /// escritura denegada—, así que un test que solo mirara el código de error
    /// pasaría en verde por el motivo contrario al que dice comprobar: no
    /// porque la base impida escribir, sino porque la sesión de soporte nunca
    /// llegó a abrirse. Leer primero separa las dos causas.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Bajo_la_identidad_de_conexion_de_produccion_el_soporte_lee_pero_no_escribe()
    {
        var comoRuntime = BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion);

        await using var contexto = CrearContexto(
            _tenantVisitado, sesionPrivilegiadaId: Guid.NewGuid(), cadenaConexion: comoRuntime);

        var clientes = await contexto.Empresas.ToListAsync();

        clientes.Should().ContainSingle().Which.RazonSocial.Should().Be("Cliente Visitado S.L.",
            "si esto falla, la sesión de soporte no llegó a abrirse —falta la membresía— y la aserción de " +
            "escritura de abajo mediría otra cosa");

        contexto.Empresas.Add(Empresa.CrearComoCliente(
            "Escrito por soporte S.L.", "B66666678", esCritico: false, notas: null, ejecutivoUsuarioId: null));

        var accion = async () => await contexto.SaveChangesAsync();

        (await accion.Should().ThrowAsync<DbUpdateException>())
            .Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege,
                "el rol restringido ya está adoptado —la lectura lo demuestra—, así que la denegación solo " +
                "puede venir de los privilegios de tabla");
    }

    // ── Andamiaje ──────────────────────────────────────────────────────────

    private CaeManagerDbContext CrearContexto(
        Guid tenantId, Guid? sesionPrivilegiadaId = null, string? cadenaConexion = null)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(cadenaConexion ?? _cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(
                new TenantSelladoInterceptor(tenantActual),
                new TenantRlsConnectionInterceptor(
                    tenantActual,
                    new ClienteActivoSeleccionadoFalso(tenantId, sesionPrivilegiadaId),
                    new CurrentUserServiceFalso(Guid.NewGuid(), tenantOrigenId: tenantId)))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private async Task<NpgsqlConnection> AbrirComoSoporteAsync(Guid? tenantId)
    {
        var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();

        await FijarTenantAsync(conexion, tenantId);

        await using var setRol = conexion.CreateCommand();
        setRol.CommandText = "SET ROLE cae_app_soporte;";
        await setRol.ExecuteNonQueryAsync();

        return conexion;
    }

    private static async Task FijarTenantAsync(NpgsqlConnection conexion, Guid? tenantId)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = "SELECT set_config('app.tenant_id', @valor, false);";
        comando.Parameters.AddWithValue("valor", tenantId?.ToString() ?? string.Empty);
        await comando.ExecuteNonQueryAsync();
    }

    private static async Task<long> ContarClientesAsync(NpgsqlConnection conexion)
    {
        await using var consulta = conexion.CreateCommand();
        consulta.CommandText = "SELECT count(*) FROM \"Empresas\";";
        return (long)(await consulta.ExecuteScalarAsync())!;
    }

    private static async Task<List<string>> LeerNombresClientesAsync(NpgsqlConnection conexion)
    {
        await using var consulta = conexion.CreateCommand();
        consulta.CommandText = "SELECT \"RazonSocial\" FROM \"Empresas\";";

        var nombres = new List<string>();
        await using var lector = await consulta.ExecuteReaderAsync();
        while (await lector.ReadAsync()) nombres.Add(lector.GetString(0));

        return nombres;
    }

    private sealed class ClienteActivoSeleccionadoFalso(Guid tenantId, Guid? sesionPrivilegiadaId)
        : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => tenantId;

        public Guid? AsignacionOperacionIdSeleccionada => null;

        public Guid? SesionPrivilegiadaIdSeleccionada => sesionPrivilegiadaId;
    }
}
