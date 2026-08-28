using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests;

/// <summary>
/// F3c — verificación previa al <c>DROP</c> de <c>Clientes</c>/<c>Subcontratas</c>.
///
/// <para>
/// El valor de esta migración no está en el <c>DROP</c> (dos líneas), sino en
/// que se niegue a ejecutarlo cuando queda una fila legacy sin contraparte en
/// <c>Empresas</c>. Un test que solo comprobara "las tablas ya no están" no
/// distinguiría una verificación que funciona de una que no mira nada: los dos
/// casos dan verde. Por eso cada comprobación de la migración tiene aquí su
/// control positivo (con contraparte → retira) y su control negativo (sin
/// contraparte → aborta Y las tablas siguen en pie).
/// </para>
///
/// <para>
/// <b>Lo que estos tests NO demuestran</b>: la conexión de pruebas es
/// superusuario, y un superusuario ignora RLS. El recorrido por tenants del
/// <c>Up()</c> existe para que la verificación vea filas cuando la ejecuta el
/// rol propietario, que sí está sujeto a <c>FORCE ROW LEVEL SECURITY</c> — y
/// eso no se observa desde aquí. Queda como hueco explícito, no como propiedad
/// probada.
/// </para>
/// </summary>
public class F3cRetiradaClientesSubcontratasLegacyTests : IAsyncLifetime
{
    private const string MigracionAnteriorAF3c = "AgregarDatosDemoCompletadosATenant";
    private const string MigracionF3c = "F3cRetiradaClientesSubcontratasLegacy";

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private Guid _tenantId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto(Guid.NewGuid());
        var migrador = contexto.GetInfrastructure().GetRequiredService<IMigrator>();
        await migrador.MigrateAsync(MigracionAnteriorAF3c);

        // El recorrido del Up() itera los tenants reales, así que las filas de
        // prueba se cuelgan de uno que exista de verdad — el que siembra la
        // propia cadena de migraciones.
        _tenantId = await ObtenerPrimerTenantAsync();
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Con_todas_las_filas_legacy_respaldadas_en_Empresas_la_migracion_retira_las_dos_tablas()
    {
        var clienteId = Guid.NewGuid();
        await SiembraTablasLegacyF3.InsertarClienteAsync(
            _cadenaConexion, clienteId, _tenantId, "Cliente respaldado S.L.", "B12345674", esCritico: true);
        await InsertarContraparteEnEmpresasAsync(clienteId, "Cliente respaldado S.L.", "B12345674");

        var subcontrataId = Guid.NewGuid();
        await SiembraTablasLegacyF3.InsertarSubcontrataAsync(
            _cadenaConexion, subcontrataId, _tenantId, "Subcontrata respaldada S.L.", "B87654323");
        await InsertarContraparteEnEmpresasAsync(subcontrataId, "Subcontrata respaldada S.L.", "B87654323");

        await AplicarF3cAsync();

        (await ExisteTablaAsync("Clientes")).Should().BeFalse();
        (await ExisteTablaAsync("Subcontratas")).Should().BeFalse();
    }

    [Fact]
    public async Task Un_Cliente_legacy_sin_contraparte_en_Empresas_aborta_la_migracion_y_no_retira_nada()
    {
        // El escenario real que esto protege: el UPSERT de T1 que el diseño
        // pedía a F3b nunca se implementó, así que un Cliente creado en la
        // ventana entre el backfill de F3a y la congelación vive SOLO en la
        // tabla legacy. Dejar pasar el DROP sobre esa fila es pérdida de datos.
        var huerfanoId = Guid.NewGuid();
        await SiembraTablasLegacyF3.InsertarClienteAsync(
            _cadenaConexion, huerfanoId, _tenantId, "Cliente huérfano S.L.", "B12345674", esCritico: false);

        var accion = AplicarF3cAsync;

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.MessageText.Should().Contain(huerfanoId.ToString(),
                "el diagnóstico debe nombrar la fila exacta, no decir sólo que algo falló");

        (await ExisteTablaAsync("Clientes")).Should().BeTrue(
            "PostgreSQL revierte el Up() entero: si la verificación aborta, no se retira ninguna tabla");
        (await ExisteTablaAsync("Subcontratas")).Should().BeTrue();
    }

    [Fact]
    public async Task Una_Subcontrata_legacy_sin_contraparte_en_Empresas_aborta_la_migracion()
    {
        var huerfanaId = Guid.NewGuid();
        await SiembraTablasLegacyF3.InsertarSubcontrataAsync(
            _cadenaConexion, huerfanaId, _tenantId, "Subcontrata huérfana S.L.", "B12345674");

        var accion = AplicarF3cAsync;

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.MessageText.Should().Contain(huerfanaId.ToString());

        (await ExisteTablaAsync("Subcontratas")).Should().BeTrue();
    }

    [Fact]
    public async Task Una_contraparte_en_otro_tenant_no_cuenta_como_respaldo()
    {
        // Mismo Id, tenant distinto: no es la misma fila. Sin la condición de
        // TenantId la verificación daría por respaldada una fila que no lo
        // está — y el DROP se la llevaría.
        var otroTenant = await CrearTenantAdicionalAsync();
        var clienteId = Guid.NewGuid();

        await SiembraTablasLegacyF3.InsertarClienteAsync(
            _cadenaConexion, clienteId, _tenantId, "Cliente cruzado S.L.", "B12345674", esCritico: false);
        await InsertarContraparteEnEmpresasAsync(clienteId, "Cliente cruzado S.L.", "B12345674", tenantId: otroTenant);

        var accion = AplicarF3cAsync;

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.MessageText.Should().Contain(clienteId.ToString());
    }

    [Fact]
    public async Task Un_CreadoEnUtc_divergente_aborta_la_migracion()
    {
        var clienteId = Guid.NewGuid();
        await SiembraTablasLegacyF3.InsertarClienteAsync(
            _cadenaConexion, clienteId, _tenantId, "Cliente reescrito S.L.", "B12345674", esCritico: false);
        await InsertarContraparteEnEmpresasAsync(
            clienteId, "Cliente reescrito S.L.", "B12345674",
            creadoEnUtc: new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var accion = AplicarF3cAsync;

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.MessageText.Should().Contain("CreadoEnUtc");
    }

    [Fact]
    public async Task El_contenido_editable_puede_diverger_sin_abortar_nada()
    {
        // Desde F3b, Empresas es la fuente viva: editar un cliente cambia su
        // razón social ahí y no en la fila congelada. Esa divergencia es
        // CORRECTA, y una comparación campo a campo —lo que pedía el diseño
        // original de F3c— abortaría sobre datos sanos. Este test fija que no
        // se vuelva a esa comparación por descuido.
        var clienteId = Guid.NewGuid();
        await SiembraTablasLegacyF3.InsertarClienteAsync(
            _cadenaConexion, clienteId, _tenantId, "Nombre congelado S.L.", "B12345674",
            esCritico: false, notas: "notas viejas");
        await InsertarContraparteEnEmpresasAsync(clienteId, "Nombre YA EDITADO S.L.", "B87654323");

        await AplicarF3cAsync();

        (await ExisteTablaAsync("Clientes")).Should().BeFalse();
    }

    // ---------- utilidades ----------

    private async Task AplicarF3cAsync()
    {
        await using var contexto = CrearContexto(_tenantId);
        var migrador = contexto.GetInfrastructure().GetRequiredService<IMigrator>();
        await migrador.MigrateAsync(MigracionF3c);
    }

    private async Task<Guid> ObtenerPrimerTenantAsync()
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = """SELECT "Id" FROM "Tenants" ORDER BY "Id" LIMIT 1;""";
        var id = await comando.ExecuteScalarAsync();
        id.Should().NotBeNull(
            "sin ningún tenant, el recorrido del Up() no ejecutaría su cuerpo y este test daría verde sin medir nada");
        return (Guid)id!;
    }

    private async Task<Guid> CrearTenantAdicionalAsync()
    {
        var id = Guid.NewGuid();
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        // Columnas obligatorias leídas del esquema real (snapshot del modelo
        // + information_schema), no escritas de memoria: un 23502 aquí no se
        // parecería en nada a lo que el test pretende medir.
        comando.CommandText = """
            INSERT INTO "Tenants"
                ("Id", "Nombre", "CreadoEnUtc", "EsPlataforma", "Estado", "EstadoComercial", "PerfilVocabulario")
            VALUES (@id, 'Tenant adicional de prueba', now(), false, 'Activo', 'SinSuscripcion', 'ClienteDirecto');
            """;
        comando.Parameters.AddWithValue("id", id);
        await comando.ExecuteNonQueryAsync();
        return id;
    }

    /// <summary>
    /// Inserta la fila de <c>Empresas</c> que respalda a una fila legacy: mismo
    /// <c>Id</c>, mismo <c>TenantId</c> y mismo <c>CreadoEnUtc</c>, que es
    /// exactamente lo que dejó el backfill de F3a. Por SQL directo y no por EF
    /// porque el <c>CreadoEnUtc</c> tiene que coincidir con el de la fila
    /// legacy, y EF lo sella él solo.
    /// </summary>
    private async Task InsertarContraparteEnEmpresasAsync(
        Guid id, string razonSocial, string cif, Guid? tenantId = null, DateTime? creadoEnUtc = null)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = """
            INSERT INTO "Empresas"
                ("Id", "TenantId", "RazonSocial", "Cif", "EsPropia", "EsActividadAnexoI",
                 "CreadoEnUtc", "EstaEliminado", "Version")
            VALUES (@id, @tenantId, @razonSocial, @cif, false, false,
                    COALESCE(@creadoEnUtc, (SELECT "CreadoEnUtc" FROM "Clientes" WHERE "Id" = @id
                                            UNION ALL
                                            SELECT "CreadoEnUtc" FROM "Subcontratas" WHERE "Id" = @id
                                            LIMIT 1)),
                    false, gen_random_uuid());
            """;
        comando.Parameters.AddWithValue("id", id);
        comando.Parameters.AddWithValue("tenantId", tenantId ?? _tenantId);
        comando.Parameters.AddWithValue("razonSocial", razonSocial);
        comando.Parameters.AddWithValue("cif", cif);
        comando.Parameters.AddWithValue(
            "creadoEnUtc", creadoEnUtc.HasValue ? creadoEnUtc.Value : DBNull.Value);
        await comando.ExecuteNonQueryAsync();
    }

    private async Task<bool> ExisteTablaAsync(string tabla)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = "SELECT to_regclass(@tabla) IS NOT NULL;";
        comando.Parameters.AddWithValue("tabla", $"public.\"{tabla}\"");
        return (bool)(await comando.ExecuteScalarAsync())!;
    }

    private CaeManagerDbContext CrearContexto(Guid tenantId)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
