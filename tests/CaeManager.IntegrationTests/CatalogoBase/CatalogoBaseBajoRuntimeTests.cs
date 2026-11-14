using CaeManager.Application.CatalogoBase.Queries;
using CaeManager.Domain.CatalogoBase;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.CatalogoBase;

/// <summary>
/// El catálogo base de Plataforma (B-13) bajo los roles reales. Sus cinco tablas son globales, sin <c>TenantId</c> ni
/// RLS (excepción documentada en <c>CoberturaRlsDelModeloTests.CatalogoBaseDePlataformaSinRls</c>): lo que las
/// protege es el privilegio. <c>cae_app_runtime</c> las lee enteras desde cualquier Tenant, también sin Tenant, y no
/// las escribe; soporte, aprovisionamiento y PUBLIC no tienen nada. La siembra es inventada y solo vive en esta base
/// de pruebas: el contenido real espera a la revisión del técnico habilitado (CA-7).
/// </summary>
public class CatalogoBaseBajoRuntimeTests : IAsyncLifetime
{
    private const string Runtime = "cae_app_runtime";

    /// <summary>
    /// Las tablas del catálogo base, sacadas del modelo de EF (las entidades del espacio de nombres de
    /// <see cref="ActividadCatalogo"/>): una tabla nueva del catálogo entra sola en cada barrido de privilegios. La lista
    /// exacta la fija <c>ModeloCatalogoBaseTests.Tablas</c> en Architecture; aquí solo se exige que no salga vacía.
    /// </summary>
    private static readonly string[] Tablas = TablasDelModelo();

    private static string[] TablasDelModelo()
    {
        using var contexto = new CaeManagerDbContext(
            new DbContextOptionsBuilder<CaeManagerDbContext>()
                .UseNpgsql("Host=localhost;Database=solo-para-construir-el-modelo;Username=x;Password=x").Options,
            new EphemeralDataProtectionProvider(), new TenantActualAmbiental());
        var tablas = contexto.Model.GetEntityTypes()
            .Where(t => t.ClrType.Namespace == typeof(ActividadCatalogo).Namespace)
            .Select(t => t.GetTableName()!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return tablas.Length > 0
            ? tablas
            : throw new InvalidOperationException("El modelo no tiene tablas del catálogo base: el barrido no observaría nada.");
    }

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();
    private ActividadCatalogo _actividad = null!;
    private RiesgoCatalogo _riesgo = null!;
    private RequisitoBase _requisito = null!;

    public async Task InitializeAsync()
    {
        await BaseDatosPostgresDePruebas.MigrarAsync(_cadenaConexion);

        // Siembra con la identidad propietaria (la de las migraciones): ningún rol de la aplicación puede escribir.
        await using var propietario = new NpgsqlConnection(_cadenaConexion);
        await propietario.OpenAsync();
        await using var contexto = CrearContexto(propietario);

        _actividad = new ActividadCatalogo("actividad-prueba", "Actividad de prueba");
        _actividad.LigarCodigoOficial(SistemaCodigoOficial.Cnae2025, "00.01");
        _actividad.LigarCodigoOficial(SistemaCodigoOficial.Cno11, "0001");
        var actividadRetirada = new ActividadCatalogo("actividad-retirada", "Actividad retirada");
        actividadRetirada.Retirar();
        _riesgo = RiesgoCatalogo.Nuevo("riesgo-prueba", "Riesgo de prueba");
        var otro = RiesgoCatalogo.Otro("otro", "Otro riesgo");
        _requisito = new RequisitoBase("requisito-prueba", "Requisito de prueba", "Norma de prueba, art. 1", NivelExigencia.N1);

        contexto.CatalogoActividades.AddRange(_actividad, actividadRetirada);
        contexto.CatalogoRiesgos.AddRange(_riesgo, otro);
        contexto.CatalogoRequisitosBase.Add(_requisito);
        contexto.CatalogoReglasRiesgoRequisito.Add(new ReglaRiesgoRequisito(_riesgo, _requisito));
        await contexto.SaveChangesAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Las_tablas_del_catalogo_no_tienen_RLS_ni_TenantId()
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();

        foreach (var tabla in Tablas)
        {
            await using var comando = conexion.CreateCommand();
            comando.CommandText = """
                SELECT c.relrowsecurity,
                       (SELECT count(*) FROM pg_policies p WHERE p.schemaname = 'public' AND p.tablename = c.relname),
                       (SELECT count(*) FROM information_schema.columns
                         WHERE table_schema = 'public' AND table_name = c.relname AND column_name = 'TenantId')
                FROM pg_class c
                WHERE c.oid = format('public.%I', @tabla::text)::regclass;
                """;
            comando.Parameters.AddWithValue("tabla", tabla);
            await using var lector = await comando.ExecuteReaderAsync();
            (await lector.ReadAsync()).Should().BeTrue(tabla);

            lector.GetBoolean(0).Should().BeFalse($"{tabla} es global, igual para todos los Tenants");
            lector.GetInt64(1).Should().Be(0, tabla);
            lector.GetInt64(2).Should().Be(0, tabla);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task El_runtime_lee_el_catalogo_entero_desde_cualquier_Tenant_y_sin_Tenant(bool conTenant)
    {
        await using var desdeA = await AbrirComoAsync(Runtime, conTenant ? _tenantA : null);
        await using var desdeB = await AbrirComoAsync(Runtime, conTenant ? _tenantB : null);

        foreach (var (tabla, filas) in new[]
                 {
                     ("CatalogoActividades", 2L), ("CatalogoActividadesCodigosOficiales", 2L), ("CatalogoRiesgos", 2L),
                     ("CatalogoRequisitosBase", 1L), ("CatalogoReglasRiesgoRequisito", 1L),
                 })
        {
            (await ContarAsync(desdeA, tabla)).Should().Be(filas, tabla);
            (await ContarAsync(desdeB, tabla)).Should().Be(filas, tabla);
        }
    }

    [Fact]
    public async Task La_Query_devuelve_las_Actividades_y_los_riesgos_leyendo_como_runtime()
    {
        await using var conexion = await AbrirComoAsync(Runtime, _tenantA);
        await using var contexto = CrearContexto(conexion);

        var catalogo = await new ObtenerCatalogoBaseQueryHandler(contexto).Handle(new ObtenerCatalogoBaseQuery(), CancellationToken.None);

        catalogo.Actividades.Should().ContainSingle().Which.Should().BeEquivalentTo(new ActividadCatalogoDto(
            _actividad.Id, "actividad-prueba", "Actividad de prueba",
            [new CodigoOficialDto(SistemaCodigoOficial.Cnae2025, "00.01"), new CodigoOficialDto(SistemaCodigoOficial.Cno11, "0001")]));
        catalogo.Riesgos.Select(r => (r.Codigo, r.EsOtro)).Should().Equal(("riesgo-prueba", false), ("otro", true));
        catalogo.Riesgos[0].Requisitos.Should().Equal(
            new RequisitoBaseDto(_requisito.Id, "requisito-prueba", "Requisito de prueba", "Norma de prueba, art. 1", NivelExigencia.N1));
    }

    public static TheoryData<string> EscriturasDelRuntime()
    {
        var datos = new TheoryData<string>();
        foreach (var tabla in Tablas)
        {
            datos.Add($"UPDATE \"{tabla}\" SET \"Id\" = \"Id\";");
            datos.Add($"DELETE FROM \"{tabla}\";");
            datos.Add($"TRUNCATE \"{tabla}\" CASCADE;");
        }

        datos.Add("""INSERT INTO "CatalogoActividades" ("Id", "Codigo", "Nombre", "Activa") VALUES (gen_random_uuid(), 'inventada', 'Inventada', true);""");
        datos.Add("""INSERT INTO "CatalogoRiesgos" ("Id", "Codigo", "Nombre", "EsOtro", "Activo") VALUES (gen_random_uuid(), 'inventado', 'Inventado', false, true);""");
        datos.Add("""INSERT INTO "CatalogoRequisitosBase" ("Id", "Codigo", "Nombre", "Fundamento", "Nivel", "Activo") VALUES (gen_random_uuid(), 'inventado', 'Inventado', 'Norma', 'N1', true);""");
        datos.Add("""INSERT INTO "CatalogoActividadesCodigosOficiales" ("Id", "ActividadId", "Sistema", "Codigo") SELECT gen_random_uuid(), "Id", 'Regcon', '0002' FROM "CatalogoActividades";""");
        datos.Add("""INSERT INTO "CatalogoReglasRiesgoRequisito" ("Id", "RiesgoId", "RequisitoId") SELECT gen_random_uuid(), r."Id", q."Id" FROM "CatalogoRiesgos" r CROSS JOIN "CatalogoRequisitosBase" q WHERE r."EsOtro";""");
        return datos;
    }

    [Theory]
    [MemberData(nameof(EscriturasDelRuntime))]
    public async Task El_runtime_no_escribe_el_catalogo(string sql)
    {
        await using var conexion = await AbrirComoAsync(Runtime, _tenantA);

        await DebeDenegarseAsync(conexion, sql);
    }

    [Theory]
    [InlineData("cae_app_soporte")]
    [InlineData("cae_app_aprovisionamiento")]
    public async Task Soporte_y_aprovisionamiento_no_leen_el_catalogo(string rol)
    {
        await using var conexion = await AbrirComoAsync(rol, _tenantA);

        foreach (var tabla in Tablas)
            await DebeDenegarseAsync(conexion, $"SELECT count(*) FROM \"{tabla}\";");
    }

    /// <summary>
    /// El catálogo de privilegios, no solo los intentos: lo que cada rol de la aplicación (y PUBLIC) tiene concedido
    /// sobre las cinco tablas, por tabla y por columna. Un privilegio de más aparece aquí aunque ningún test lo intente.
    /// </summary>
    [Fact]
    public async Task Cada_rol_tiene_exactamente_los_privilegios_que_concede_la_migracion()
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        var concedidos = new List<string>();

        foreach (var rol in new[] { Runtime, "cae_app_soporte", "cae_app_aprovisionamiento", "public" })
        {
            foreach (var tabla in Tablas)
            {
                foreach (var privilegio in new[] { "SELECT", "INSERT", "UPDATE", "DELETE", "TRUNCATE", "REFERENCES", "TRIGGER" })
                {
                    await using var deTabla = conexion.CreateCommand();
                    deTabla.CommandText = "SELECT has_table_privilege(@rol, format('public.%I', @tabla::text), @privilegio);";
                    deTabla.Parameters.AddWithValue("rol", rol);
                    deTabla.Parameters.AddWithValue("tabla", tabla);
                    deTabla.Parameters.AddWithValue("privilegio", privilegio);
                    if ((bool)(await deTabla.ExecuteScalarAsync())!)
                    {
                        concedidos.Add($"{rol} {tabla} {privilegio}");
                        continue;
                    }

                    if (privilegio is "DELETE" or "TRUNCATE" or "TRIGGER")
                        continue;

                    await using var deColumnas = conexion.CreateCommand();
                    deColumnas.CommandText = """
                        SELECT column_name::text FROM information_schema.columns
                        WHERE table_schema = 'public' AND table_name = @tabla
                          AND has_column_privilege(@rol, format('public.%I', @tabla::text), column_name, @privilegio)
                        ORDER BY column_name;
                        """;
                    deColumnas.Parameters.AddWithValue("rol", rol);
                    deColumnas.Parameters.AddWithValue("tabla", tabla);
                    deColumnas.Parameters.AddWithValue("privilegio", privilegio);
                    await using var lector = await deColumnas.ExecuteReaderAsync();
                    while (await lector.ReadAsync())
                        concedidos.Add($"{rol} {tabla} {privilegio}({lector.GetString(0)})");
                }
            }
        }

        concedidos.Should().BeEquivalentTo(Tablas.Select(t => $"{Runtime} {t} SELECT"),
            "el runtime solo lee el catálogo base; soporte, aprovisionamiento y PUBLIC no tienen nada");
    }

    [Fact]
    public async Task La_base_admite_como_mucho_una_opcion_otro()
    {
        await using var propietario = new NpgsqlConnection(_cadenaConexion);
        await propietario.OpenAsync();

        var accion = () => EjecutarAsync(propietario,
            """INSERT INTO "CatalogoRiesgos" ("Id", "Codigo", "Nombre", "EsOtro", "Activo") VALUES (gen_random_uuid(), 'otro-bis', 'Otro bis', true, true);""");

        (await accion.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
        (await EjecutarAsync(propietario,
                """INSERT INTO "CatalogoRiesgos" ("Id", "Codigo", "Nombre", "EsOtro", "Activo") VALUES (gen_random_uuid(), 'riesgo-bis', 'Riesgo bis', false, true);"""))
            .Should().Be(1, "el índice único solo cuenta la opción «otro»");
    }

    [Theory]
    [InlineData("""INSERT INTO "CatalogoRequisitosBase" ("Id", "Codigo", "Nombre", "Fundamento", "Nivel", "Activo") VALUES (gen_random_uuid(), 'nivel-malo', 'Nivel malo', 'Norma', 'N4', true);""")]
    [InlineData("""INSERT INTO "CatalogoActividadesCodigosOficiales" ("Id", "ActividadId", "Sistema", "Codigo") SELECT gen_random_uuid(), "Id", 'Iae', '0001' FROM "CatalogoActividades" LIMIT 1;""")]
    [InlineData("""INSERT INTO "CatalogoActividadesCodigosOficiales" ("Id", "ActividadId", "Sistema", "Codigo") SELECT gen_random_uuid(), "Id", 'Cnae2025', '00 01' FROM "CatalogoActividades" LIMIT 1;""")]
    [InlineData("""INSERT INTO "CatalogoActividades" ("Id", "Codigo", "Nombre", "Activa") VALUES (gen_random_uuid(), 'Con-Mayusculas', 'Mal formada', true);""")]
    [InlineData("""INSERT INTO "CatalogoActividades" ("Id", "Codigo", "Nombre", "Activa") VALUES (gen_random_uuid(), E'salto-final\n', 'Mal formada', true);""")]
    [InlineData("""INSERT INTO "CatalogoRiesgos" ("Id", "Codigo", "Nombre", "EsOtro", "Activo") VALUES (gen_random_uuid(), 'doble--guion', 'Mal formado', false, true);""")]
    [InlineData("""INSERT INTO "CatalogoRequisitosBase" ("Id", "Codigo", "Nombre", "Fundamento", "Nivel", "Activo") VALUES (gen_random_uuid(), 'con espacio', 'Mal formado', 'Norma', 'N1', true);""")]
    public async Task La_base_rechaza_valores_fuera_del_catalogo(string sql)
    {
        await using var propietario = new NpgsqlConnection(_cadenaConexion);
        await propietario.OpenAsync();

        var accion = () => EjecutarAsync(propietario, sql);

        (await accion.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
    }

    private static CaeManagerDbContext CrearContexto(NpgsqlConnection conexion) =>
        new(new DbContextOptionsBuilder<CaeManagerDbContext>().UseNpgsql(conexion).Options,
            new EphemeralDataProtectionProvider(), new TenantActualAmbiental());

    /// <summary>
    /// Una conexión que <b>autentica como <c>cae_app_runtime</c></b> (<see cref="BaseDatosPostgresDePruebas.CadenaComoRuntime"/>),
    /// no una propietaria con <c>SET ROLE</c>. Para soporte y aprovisionamiento eleva desde ella con <c>SET ROLE</c>, como
    /// hace la aplicación. Comprueba la identidad efectiva antes de devolverla.
    /// </summary>
    private async Task<NpgsqlConnection> AbrirComoAsync(string rol, Guid? tenantId)
    {
        var conexion = new NpgsqlConnection(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion));
        await conexion.OpenAsync();

        await ContextoRlsDePruebas.FijarAsync(conexion, tenantId: tenantId);

        if (rol != Runtime)
        {
            await using var setRol = conexion.CreateCommand();
            setRol.CommandText = $"SET ROLE {rol};";
            await setRol.ExecuteNonQueryAsync();
        }

        await using var identidad = conexion.CreateCommand();
        identidad.CommandText = "SELECT session_user::text || '/' || current_user::text;";
        ((string)(await identidad.ExecuteScalarAsync())!).Should().Be($"{Runtime}/{rol}");

        return conexion;
    }

    private static async Task<int> EjecutarAsync(NpgsqlConnection conexion, string sql)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        return await comando.ExecuteNonQueryAsync();
    }

    private static async Task DebeDenegarseAsync(NpgsqlConnection conexion, string sql)
    {
        var accion = () => EjecutarAsync(conexion, sql);

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    private static async Task<long> ContarAsync(NpgsqlConnection conexion, string tabla)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = $"SELECT count(*) FROM \"{tabla}\";";
        return (long)(await comando.ExecuteScalarAsync())!;
    }
}
