using CaeManager.Application.ApiKeys.Queries.ObtenerClavesApi;
using CaeManager.Application.Common;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Application.Integraciones.Queries.ObtenerConexionesIntegracion;
using CaeManager.Application.Integraciones.Queries.ObtenerLineasWhatsApp;
using CaeManager.Domain.ApiKeys;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Tenants;
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
/// Soporte TALVEG no lee credenciales (migración <c>SoporteSinLecturaDeCredenciales</c>; OWASP ASVS 4.0.3
/// V4.1.3 y V2.10.4): <c>cae_app_soporte</c>, el rol de la conexión en Sesión Privilegiada, no tiene
/// ningún privilegio sobre <c>AspNetUserTokens</c> ni <c>CredencialesIntegracion</c>, y de
/// <c>ClavesApi</c> lee todas las columnas salvo <c>HashClave</c>.
///
/// <para>
/// Se comprueba en el catálogo (lo que está concedido, columna a columna, sobre la base migrada) y por
/// sus efectos (SQL directo y EF con el interceptor real). El catálogo cubre una columna nueva que se
/// añada mañana a esas tablas: el recuento exacto de columnas legibles de <c>ClavesApi</c> obliga a
/// decidir si la nueva es secreta.
/// </para>
///
/// <para>
/// Lo mismo para lo que retiró <c>SoporteSinColumnasSecretasRestantes</c>: <c>Contrasena</c> de
/// <c>CanalesGestionDocumental</c>, <c>CredencialesAccesoEmpresa</c> y <c>CredencialesAccesoSubcontrata</c>
/// (y de <c>AccesosPlataformaCaeEmpresa</c>, que no se concedía), <c>ClientState</c> de
/// <c>SuscripcionesWebhook</c>, <c>TokenAcceso</c> de <c>LineasWhatsApp</c> y <c>UltimoError</c> de
/// <c>ConexionesIntegracion</c>. Los listados de conexiones y de líneas de WhatsApp, que Soporte TALVEG abre
/// en Sesión Privilegiada, siguen funcionando sin el último error.
/// </para>
///
/// <para>
/// Y para lo que venga: toda columna de <c>public</c> con nombre de secreto que <c>cae_app_soporte</c> lea
/// falla aquí salvo que se justifique que no es secreta. <c>AspNetUsers.PasswordHash</c> y
/// <c>AspNetUsers.SecurityStamp</c> sí lo son y Soporte TALVEG aún los lee: es deuda abierta, fijada aparte.
/// La detección es solo por nombre: en verde no prueba que Soporte TALVEG no lea ningún secreto, porque no ve
/// uno guardado en una columna con otro nombre ni dentro de un texto.
/// </para>
/// </summary>
public class SoporteSinLecturaDeCredencialesTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private Guid _tenant = Guid.NewGuid();
    private Guid _tenantPlataforma;
    private Guid _delegacionId;

    /// <summary>Las columnas de <c>ClavesApi</c> que Soporte TALVEG puede leer: todas menos el hash.</summary>
    private static readonly string[] ColumnasLegiblesDeClavesApi =
    [
        "Id", "NombreDescriptivo", "PrefijoVisible", "CreadaPorUsuarioId", "UltimoUsoUtc", "RevocadaEnUtc",
        "CaducaEnUtc", "TenantId", "Version", "CreadoEnUtc", "EstaEliminado", "EliminadoEnUtc",
        "EliminadoPorUsuarioId",
    ];

    /// <summary>
    /// Las tablas que Soporte TALVEG lee por columnas, con la columna secreta y todas las que sí lee. El
    /// recuento exacto obliga a decidir, con cada columna nueva, si es secreta.
    /// </summary>
    public static TheoryData<string, string, string[]> TablasConColumnaSecreta => new()
    {
        { "CanalesGestionDocumental", "Contrasena", [
            "Id", "CentroId", "CreadoEnUtc", "EliminadoEnUtc", "EliminadoPorUsuarioId", "EmailsDestinatarios",
            "EsPrincipal", "EstaEliminado", "EtiquetaProposito", "NombreContacto", "Notas", "ProveedorPlataformaCaeId",
            "TenantId", "Tipo", "UrlAcceso", "Usuario", "Version"] },
        { "CredencialesAccesoEmpresa", "Contrasena", [
            "Id", "CampoEmpresa", "EmpresaId", "Notas", "TenantId", "UrlAcceso", "Usuario"] },
        { "CredencialesAccesoSubcontrata", "Contrasena", [
            "Id", "CampoEmpresa", "Notas", "SubcontrataId", "TenantId", "UrlAcceso", "Usuario"] },
        { "SuscripcionesWebhook", "ClientState", [
            "Id", "ConexionIntegracionId", "FechaExpiracionUtc", "GraphSubscriptionId", "TenantId"] },
        { "LineasWhatsApp", "TokenAcceso", [
            "Id", "ComercialAsignadoId", "ConexionIntegracionId", "MensajeAutoTriage", "Modo", "NumeroTelefono",
            "PhoneNumberId", "TenantId", "WabaId"] },
        { "ConexionesIntegracion", "UltimoError", [
            "Id", "BuzonEmail", "ClienteId", "CreadoEnUtc", "EliminadoEnUtc", "EliminadoPorUsuarioId", "EstaEliminado",
            "Estado", "FechaConectadaUtc", "GestorPropietarioId", "Nombre", "Proveedor", "TenantId", "Version"] },
    };

    private const string ErrorDelProveedor = "El token de Microsoft Graph caducó: hay que reconectar el buzón.";

    public async Task InitializeAsync()
    {
        await BaseDatosPostgresDePruebas.MigrarAsync(_cadenaConexion);

        // Una delegación de soporte del Tenant de plataforma sobre el Tenant visitado, sembrada como
        // propietario: es lo que necesita ObtenerClavesApiQueryHandler para llegar a leer ClavesApi.
        var tenantPlataforma = new Tenant("Plataforma de prueba", esPlataforma: true);
        var tenantVisitado = new Tenant("Tenant visitado de prueba");
        var delegacion = DelegacionTenant.ParaSoporte(tenantPlataforma.Id, tenantVisitado.Id);
        delegacion.ActivarParaSoporte("Prueba de integración", DateTime.UtcNow.AddHours(1), DateTime.UtcNow);

        _tenant = tenantVisitado.Id;
        _tenantPlataforma = tenantPlataforma.Id;
        _delegacionId = delegacion.Id;

        await using var contexto = CrearContexto();
        contexto.Tenants.AddRange(tenantPlataforma, tenantVisitado);
        contexto.DelegacionesTenant.Add(delegacion);
        // Sellada en el Tenant visitado por TenantSelladoInterceptor. El hash es un valor cualquiera
        // de 64 caracteres: aquí solo importa que exista la columna.
        contexto.ClavesApi.Add(new ClaveApi(
            "Integración de prueba", "hyd_prueba01", new string('a', 64), Guid.NewGuid(), DateTime.UtcNow.AddDays(30), "Prueba"));

        // Un buzón con error y una línea de WhatsApp con error, para los listados de Integraciones.
        var buzon = new ConexionIntegracion("avisos@buzon-de-prueba.local", "Buzón de prueba");
        buzon.MarcarConError(ErrorDelProveedor);
        var conexionWhatsApp = new ConexionIntegracion(
            "+34600000099", "Línea de prueba", proveedor: ProveedorIntegracion.WhatsApp);
        conexionWhatsApp.MarcarConError(ErrorDelProveedor);
        contexto.ConexionesIntegracion.AddRange(buzon, conexionWhatsApp);
        contexto.LineasWhatsApp.Add(new LineaWhatsApp(
            conexionWhatsApp.Id, "PNID-" + Guid.NewGuid().ToString("N"), "WABA-PRUEBA", "+34600000099", "token-de-prueba",
            ModoAsignacionLinea.PoolInbound));
        await contexto.SaveChangesAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    // ── En el catálogo ─────────────────────────────────────────────────────

    [Fact]
    public async Task Soporte_no_lee_columnas_con_nombres_de_secretos_fuera_de_las_excepciones()
    {
        var patrones = new[]
        {
            "Hash", "Secret", "Secreto", "Token", "Clave", "Password", "Contrasena", "Stamp", "Cifrad", "Credencial", "PrivateKey",
        };

        // Solo columnas que NO son secretas aunque su nombre lo parezca. Un secreto real no entra aquí.
        var noSecretas = new HashSet<(string Tabla, string Columna)>
        {
            // Guid opaco que distingue reintentos ante la plataforma CAE: la puerta rechaza cualquier otro valor.
            ("UsosConexionPlataformaCae", "ClaveIdempotencia"),
            // Booleano que obliga a cambiar la contraseña en el siguiente acceso; no la contiene.
            ("AspNetUsers", "DebeCambiarContrasena"),
            // Identity usa el sello de la cuenta para detectar escrituras concurrentes, no para autenticar.
            ("AspNetUsers", "ConcurrencyStamp"),
            // Identity usa el sello del rol para detectar escrituras concurrentes, no para autenticar.
            ("AspNetRoles", "ConcurrencyStamp"),
            // Huella SHA-256 del archivo procesado, mostrada en la auditoría de extracción de IA.
            ("AuditoriasExtraccionIa", "HashSha256"),
            // Huella SHA-256 del archivo, parte de la clave de la caché de extracción de IA.
            ("ExtraccionesIaCache", "HashSha256"),
            // Huella SHA-256 del PDF firmado que se incluye en la consulta de firmas en campo.
            ("FirmasEnCampoDocumento", "HashSha256Pdf"),
            // Huella reservada del documento impreso: hoy siempre vacía, y nunca una credencial.
            ("DocumentosGenerados", "HashSha256Impreso"),
            // Huella SHA-256 del archivo original, para detectar una versión idéntica de la plantilla.
            ("PlantillasDocumentoVersion", "HashSha256ArchivoOriginal"),
            // Identificadores de las cajas que determinan su orden en la ficha.
            ("OrdenesCajasFicha", "Claves"),
            // Identificador del archivo del logo; abrirlo exige el Tenant activo, no conocerlo.
            ("Tenants", "LogoArchivoClave"),
        };

        // Secretos reales que Soporte TALVEG aún lee: no son excepciones sino deuda de seguridad abierta.
        // Se fijan aquí para que la lista no crezca y para que retirarlos obligue a borrarlos de ella.
        // La revocación no es de una línea: UserManager materializa la cuenta entera en Sesión Privilegiada
        // (ver SoporteSinColumnasSecretasRestantes).
        var deudaDeSecretosLegibles = new HashSet<(string Tabla, string Columna)>
        {
            ("AspNetUsers", "PasswordHash"),
            ("AspNetUsers", "SecurityStamp"),
        };

        // Lo que las migraciones ya retiraron: los patrones tienen que seguir viéndolo.
        var secretosRevocados = new[]
        {
            ("AccesosPlataformaCaeEmpresa", "Contrasena"),
            ("CanalesGestionDocumental", "Contrasena"),
            ("ClavesApi", "HashClave"),
            ("CredencialesAccesoEmpresa", "Contrasena"),
            ("CredencialesConexionPlataformaCae", "SecretoApi"),
            ("CredencialesAccesoSubcontrata", "Contrasena"),
            ("CredencialesIntegracion", "AccessToken"),
            ("CredencialesIntegracion", "RefreshToken"),
            ("LineasWhatsApp", "TokenAcceso"),
        };

        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = """
            SELECT c.relname, a.attname,
                   has_column_privilege('cae_app_soporte', c.oid, a.attnum, 'SELECT')
            FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_catalog.pg_attribute a ON a.attrelid = c.oid
            WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p', 'v', 'm', 'f')
              AND a.attnum > 0 AND NOT a.attisdropped;
            """;

        var columnas = new List<(string Tabla, string Columna, bool Legible)>();
        await using var lector = await comando.ExecuteReaderAsync();
        while (await lector.ReadAsync())
            columnas.Add((lector.GetString(0), lector.GetString(1), lector.GetBoolean(2)));

        var candidatas = columnas
            .Where(c => patrones.Any(p => c.Columna.Contains(p, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var legibles = candidatas.Where(c => c.Legible).Select(c => (c.Tabla, c.Columna)).ToHashSet();
        var noLegibles = candidatas.Where(c => !c.Legible).Select(c => (c.Tabla, c.Columna)).ToHashSet();

        noLegibles.Should().Contain(secretosRevocados,
            "control del instrumento: cada secreto revocado aparece entre las candidatas y sin SELECT");
        legibles.Should().Contain(("AspNetRoles", "ConcurrencyStamp"),
            "control del instrumento: detecta una columna existente con SELECT");
        noSecretas.Except(legibles).Should().BeEmpty(
            "una excepción que ya no existe o ya no se lee se quita de la lista: no puede quedar para tapar otra columna");

        legibles.Except(noSecretas).Should().BeEquivalentTo(deudaDeSecretosLegibles,
            "Soporte TALVEG no debe leer columnas con nombre de secreto: cada una se revoca, o se justifica que no es "
            + "secreta. Si la deuda de AspNetUsers ya se revocó, se borra de la lista");
    }

    [Theory]
    [InlineData("AspNetUserTokens")]
    [InlineData("CredencialesIntegracion")]
    [InlineData("CredencialesConexionPlataformaCae")]
    [InlineData("ClavesApi")]
    public async Task Soporte_no_tiene_privilegio_de_tabla_sobre_los_almacenes_de_secretos(string tabla)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();

        foreach (var privilegio in new[] { "SELECT", "INSERT", "UPDATE", "DELETE", "REFERENCES", "TRIGGER" })
        {
            await using var comando = conexion.CreateCommand();
            comando.CommandText = "SELECT has_table_privilege('cae_app_soporte', format('public.%I', @tabla::text), @privilegio);";
            comando.Parameters.AddWithValue("tabla", tabla);
            comando.Parameters.AddWithValue("privilegio", privilegio);

            ((bool)(await comando.ExecuteScalarAsync())!).Should().BeFalse($"{privilegio} sobre {tabla}");
        }
    }

    [Theory]
    [InlineData("AspNetUserTokens")]
    [InlineData("CredencialesIntegracion")]
    [InlineData("CredencialesConexionPlataformaCae")]
    public async Task Soporte_no_lee_ninguna_columna_de_tokens_ni_de_credenciales_de_integracion(string tabla)
    {
        var (legibles, todas) = await ColumnasAsync(tabla);

        todas.Should().NotBeEmpty("control del instrumento: la tabla existe y tiene columnas");
        legibles.Should().BeEmpty($"ninguna lectura de Soporte TALVEG necesita una columna de {tabla}");
    }

    [Fact]
    public async Task Soporte_lee_de_ClavesApi_todas_las_columnas_salvo_el_hash()
    {
        var (legibles, todas) = await ColumnasAsync("ClavesApi");

        todas.Should().Contain("HashClave", "control del instrumento: la columna secreta existe");
        legibles.Should().NotContain("HashClave");
        legibles.Should().BeEquivalentTo(todas.Where(c => c != "HashClave"), "ClavesApi se lee entera salvo el hash");
        legibles.Should().BeEquivalentTo(ColumnasLegiblesDeClavesApi,
            "una columna nueva en ClavesApi no se concede sola: hay que decidir si es secreta y añadirla aquí y en la migración");
    }

    [Theory]
    [MemberData(nameof(TablasConColumnaSecreta))]
    public async Task Soporte_lee_por_columnas_todas_salvo_la_secreta(string tabla, string secreta, string[] legiblesEsperadas)
    {
        var (legibles, todas) = await ColumnasAsync(tabla);

        todas.Should().Contain(secreta, "control del instrumento: la columna secreta existe");
        legibles.Should().NotContain(secreta);
        // Una columna nueva sin conceder falla aquí y no en Sesión Privilegiada: todas menos la secreta.
        legibles.Should().BeEquivalentTo(todas.Where(c => c != secreta), $"{tabla} se lee entera salvo {secreta}");
        legibles.Should().BeEquivalentTo(legiblesEsperadas,
            $"una columna nueva en {tabla} no se concede sola: hay que decidir si es secreta y añadirla aquí y en la migración");
    }

    [Theory]
    [MemberData(nameof(TablasConColumnaSecreta))]
    public async Task Soporte_no_tiene_privilegio_de_tabla_sobre_las_tablas_con_columna_secreta(
        string tabla, string secreta, string[] legiblesEsperadas)
    {
        _ = (secreta, legiblesEsperadas);
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();

        foreach (var privilegio in new[] { "SELECT", "INSERT", "UPDATE", "DELETE", "REFERENCES", "TRIGGER" })
        {
            await using var comando = conexion.CreateCommand();
            comando.CommandText = "SELECT has_table_privilege('cae_app_soporte', format('public.%I', @tabla::text), @privilegio);";
            comando.Parameters.AddWithValue("tabla", tabla);
            comando.Parameters.AddWithValue("privilegio", privilegio);

            ((bool)(await comando.ExecuteScalarAsync())!).Should().BeFalse($"{privilegio} sobre {tabla}");
        }
    }

    [Fact]
    public async Task Soporte_no_lee_ninguna_columna_de_los_accesos_a_plataformas_CAE()
    {
        // AnadePlataformaCaeDeClienteEmpresarial ya se la retiró entera; aquí se mide.
        var (legibles, todas) = await ColumnasAsync("AccesosPlataformaCaeEmpresa");

        todas.Should().Contain("Contrasena", "control del instrumento: la tabla existe con su columna secreta");
        legibles.Should().BeEmpty();
    }

    // ── Por sus efectos ────────────────────────────────────────────────────

    [Theory]
    [InlineData("SELECT \"Value\" FROM \"AspNetUserTokens\";")]
    [InlineData("SELECT count(*) FROM \"AspNetUserTokens\";")]
    [InlineData("SELECT \"RefreshToken\" FROM \"CredencialesIntegracion\";")]
    [InlineData("SELECT \"AccessToken\" FROM \"CredencialesIntegracion\";")]
    [InlineData("SELECT \"SecretoApi\" FROM \"CredencialesConexionPlataformaCae\";")]
    [InlineData("SELECT count(*) FROM \"CredencialesConexionPlataformaCae\";")]
    [InlineData("SELECT \"HashClave\" FROM \"ClavesApi\";")]
    [InlineData("SELECT * FROM \"ClavesApi\";")]
    [InlineData("SELECT \"Contrasena\" FROM \"CanalesGestionDocumental\";")]
    [InlineData("SELECT * FROM \"CanalesGestionDocumental\";")]
    [InlineData("SELECT \"Contrasena\" FROM \"CredencialesAccesoEmpresa\";")]
    [InlineData("SELECT \"Contrasena\" FROM \"CredencialesAccesoSubcontrata\";")]
    [InlineData("SELECT \"Contrasena\" FROM \"AccesosPlataformaCaeEmpresa\";")]
    [InlineData("SELECT \"ClientState\" FROM \"SuscripcionesWebhook\";")]
    [InlineData("SELECT \"TokenAcceso\" FROM \"LineasWhatsApp\";")]
    [InlineData("SELECT * FROM \"LineasWhatsApp\";")]
    [InlineData("SELECT \"UltimoError\" FROM \"ConexionesIntegracion\";")]
    [InlineData("SELECT count(*) FROM \"ConexionesIntegracion\" WHERE \"UltimoError\" IS NOT NULL;")]
    public async Task Soporte_no_puede_leer_secretos_ni_con_sql_directo(string sql)
    {
        await using var conexion = await AbrirComoSoporteAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;

        var accion = async () => await comando.ExecuteScalarAsync();

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Soporte_lee_los_metadatos_de_las_claves_sin_el_hash()
    {
        // Control positivo: el rol no está ciego a ClavesApi, solo al hash. Es la forma de la
        // proyección de ObtenerClavesApiQuery.
        await using var conexion = await AbrirComoSoporteAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText =
            "SELECT count(*) FROM (SELECT \"Id\", \"NombreDescriptivo\", \"PrefijoVisible\", \"CreadoEnUtc\", " +
            "\"UltimoUsoUtc\", \"RevocadaEnUtc\" IS NULL, \"CaducaEnUtc\" FROM \"ClavesApi\" " +
            "WHERE NOT \"EstaEliminado\") claves;";

        ((long)(await comando.ExecuteScalarAsync())!).Should().Be(1, "la clave sembrada en el Tenant visitado");
    }

    [Fact]
    public async Task En_Sesion_Privilegiada_EF_proyecta_las_claves_pero_no_materializa_la_entidad()
    {
        // Por qué ObtenerClavesApiQuery proyecta RevocadaEnUtc == null y no ClaveApi.EstaActiva: una
        // propiedad sin mapear obliga a EF a traer la entidad entera, hash incluido.
        await using var contexto = CrearContexto(sesionPrivilegiadaId: Guid.NewGuid());

        var proyectadas = await contexto.ClavesApi
            .Select(c => new { c.Id, c.NombreDescriptivo, Activa = c.RevocadaEnUtc == null, c.CaducaEnUtc })
            .ToListAsync();
        proyectadas.Should().ContainSingle().Which.Activa.Should().BeTrue();

        var materializar = async () => await contexto.ClavesApi.ToListAsync();
        (await materializar.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task En_Sesion_Privilegiada_ObtenerClavesApiQuery_lista_sin_leer_el_hash()
    {
        // El handler real: las claves se leen por la conexión de la Sesión Privilegiada
        // (cae_app_soporte); la delegación y el Tenant de plataforma, por la del propietario. Si la
        // proyección volviera a usar ClaveApi.EstaActiva, EF traería HashClave y esto daría 42501.
        await using var contextoSoporte = CrearContexto(sesionPrivilegiadaId: Guid.NewGuid());
        await using var contextoTenants = CrearContexto();
        var handler = new ObtenerClavesApiQueryHandler(
            contextoSoporte, contextoTenants, new CurrentUserServiceFalso(Guid.NewGuid(), tenantOrigenId: _tenantPlataforma));

        var claves = await handler.Handle(new ObtenerClavesApiQuery(_delegacionId), CancellationToken.None);

        claves.Should().ContainSingle("control positivo: el handler llegó a leer ClavesApi bajo cae_app_soporte")
            .Which.EstaActiva.Should().BeTrue();
    }

    [Fact]
    public async Task En_Sesion_Privilegiada_EF_no_lee_las_credenciales_de_integracion()
    {
        await using var contexto = CrearContexto(sesionPrivilegiadaId: Guid.NewGuid());

        var leer = async () => await contexto.CredencialesIntegracion.ToListAsync();

        (await leer.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task En_Sesion_Privilegiada_EF_no_materializa_las_entidades_con_columna_secreta()
    {
        await using var contexto = CrearContexto(sesionPrivilegiadaId: Guid.NewGuid());

        var lecturas = new (string Entidad, Func<Task> Leer)[]
        {
            ("CanalGestionDocumental", () => contexto.CanalesGestionDocumental.ToListAsync()),
            ("CredencialAccesoEmpresa", () => contexto.CredencialesAccesoEmpresa.ToListAsync()),
            ("CredencialAccesoSubcontrata", () => contexto.CredencialesAccesoSubcontrata.ToListAsync()),
            ("SuscripcionWebhook", () => contexto.SuscripcionesWebhook.ToListAsync()),
            ("LineaWhatsApp", () => contexto.LineasWhatsApp.ToListAsync()),
            ("ConexionIntegracion", () => contexto.ConexionesIntegracion.ToListAsync()),
        };

        foreach (var (entidad, leer) in lecturas)
            (await leer.Should().ThrowAsync<PostgresException>(entidad))
                .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege, entidad);
    }

    [Fact]
    public async Task En_Sesion_Privilegiada_EF_proyecta_las_columnas_no_secretas()
    {
        // Control positivo: es la forma de las proyecciones de ObtenerCanalesGestionDeCentroQuery,
        // ObtenerCredencialAcceso*SinContrasenaQuery y ObtenerLineasWhatsAppQuery.
        await using var contexto = CrearContexto(sesionPrivilegiadaId: Guid.NewGuid());

        (await contexto.CanalesGestionDocumental.Select(c => new { c.Id, c.Usuario, c.UrlAcceso, c.Tipo }).ToListAsync())
            .Should().BeEmpty();
        (await contexto.CredencialesAccesoEmpresa.Select(c => new { c.Id, c.Usuario, c.Notas }).ToListAsync())
            .Should().BeEmpty();
        (await contexto.CredencialesAccesoSubcontrata.Select(c => new { c.Id, c.Usuario, c.Notas }).ToListAsync())
            .Should().BeEmpty();
        (await contexto.LineasWhatsApp.Select(l => new { l.Id, l.PhoneNumberId, l.Modo }).ToListAsync())
            .Should().ContainSingle("la línea sembrada en el Tenant visitado");
        (await contexto.ConexionesIntegracion.Select(c => new { c.Id, c.Nombre, c.Estado }).ToListAsync())
            .Should().HaveCount(2, "el buzón y la conexión de WhatsApp sembrados");
    }

    [Fact]
    public async Task En_Sesion_Privilegiada_los_listados_de_Integraciones_cargan_sin_el_ultimo_error()
    {
        // Los handlers reales, bajo cae_app_soporte. Si volvieran a proyectar UltimoError, esto daría 42501.
        await using var contexto = CrearContexto(sesionPrivilegiadaId: Guid.NewGuid());
        var seleccion = new ClienteActivoSeleccionadoFalso(_tenant, Guid.NewGuid());

        var conexiones = await new ObtenerConexionesIntegracionQueryHandler(
                contexto, contexto, new AlcanceDatosServiceFalso(), new CurrentUserServiceFalso(Guid.NewGuid()), seleccion)
            .Handle(new ObtenerConexionesIntegracionQuery(), CancellationToken.None);
        var lineas = await new ObtenerLineasWhatsAppQueryHandler(contexto, contexto, seleccion)
            .Handle(new ObtenerLineasWhatsAppQuery(), CancellationToken.None);

        conexiones.Should().ContainSingle("control positivo: el buzón sembrado se lista bajo cae_app_soporte")
            .Which.Should().Match<ConexionIntegracionListaDto>(c => c.Estado == EstadoConexionIntegracion.ConError && c.UltimoError == null);
        lineas.Should().ContainSingle("control positivo: la línea sembrada se lista bajo cae_app_soporte")
            .Which.Should().Match<LineaWhatsAppListaDto>(l => l.Estado == EstadoConexionIntegracion.ConError && l.UltimoError == null);
    }

    [Fact]
    public async Task En_Sesion_Privilegiada_el_panel_de_pendientes_por_plataforma_no_lee_la_contrasena_de_los_canales()
    {
        // El handler real compone «select canal» con un join: si EF no podara la subconsulta, traería
        // CanalesGestionDocumental.Contrasena y esto daría 42501. Sin filas, el SQL se ejecuta igual.
        await using var contexto = CrearContexto(sesionPrivilegiadaId: Guid.NewGuid());

        var pendientes = await new ObtenerPendientePorPlataformaQueryHandler(contexto, contexto, contexto, new AlcanceDatosServiceFalso())
            .Handle(new ObtenerPendientePorPlataformaQuery(), CancellationToken.None);

        pendientes.Should().BeEmpty();
    }

    [Fact]
    public async Task Sin_Sesion_Privilegiada_los_listados_de_Integraciones_muestran_el_ultimo_error()
    {
        // Guarda de no regresión: el Tenant sigue viendo por qué falla su conexión.
        await using var contexto = CrearContexto(cadenaConexion: BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion));
        var seleccion = new ClienteActivoSeleccionadoFalso(_tenant, sesionPrivilegiadaId: null);

        var conexiones = await new ObtenerConexionesIntegracionQueryHandler(
                contexto, contexto, new AlcanceDatosServiceFalso(), new CurrentUserServiceFalso(Guid.NewGuid()), seleccion)
            .Handle(new ObtenerConexionesIntegracionQuery(), CancellationToken.None);
        var lineas = await new ObtenerLineasWhatsAppQueryHandler(contexto, contexto, seleccion)
            .Handle(new ObtenerLineasWhatsAppQuery(), CancellationToken.None);

        conexiones.Should().ContainSingle().Which.UltimoError.Should().Be(ErrorDelProveedor);
        lineas.Should().ContainSingle().Which.UltimoError.Should().Be(ErrorDelProveedor);
    }

    [Fact]
    public async Task Sin_Sesion_Privilegiada_el_runtime_sigue_leyendo_las_credenciales()
    {
        // Guarda de no regresión: la migración solo toca cae_app_soporte.
        var comoRuntime = BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion);
        await using var contexto = CrearContexto(cadenaConexion: comoRuntime);

        (await contexto.ClavesApi.ToListAsync()).Should().ContainSingle()
            .Which.HashClave.Should().HaveLength(64, "el runtime sí lee el hash: lo necesita para autenticar");
        (await contexto.CredencialesIntegracion.ToListAsync()).Should().BeEmpty();
        (await contexto.UserTokens.ToListAsync()).Should().BeEmpty();
        // Sin descifrar: cada contexto de prueba tiene su propio EphemeralDataProtectionProvider.
        (await contexto.LineasWhatsApp.CountAsync(l => l.TokenAcceso != null))
            .Should().Be(1, "el runtime sí lee el token: lo necesita para enviar");
        (await contexto.ConexionesIntegracion.ToListAsync()).Should().HaveCount(2)
            .And.OnlyContain(c => c.UltimoError == ErrorDelProveedor);
        (await contexto.CanalesGestionDocumental.ToListAsync()).Should().BeEmpty();
        (await contexto.CredencialesAccesoEmpresa.ToListAsync()).Should().BeEmpty();
        (await contexto.CredencialesAccesoSubcontrata.ToListAsync()).Should().BeEmpty();
        (await contexto.SuscripcionesWebhook.ToListAsync()).Should().BeEmpty();
    }

    // ── Andamiaje ──────────────────────────────────────────────────────────

    private async Task<(List<string> Legibles, List<string> Todas)> ColumnasAsync(string tabla)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();

        await using var comando = conexion.CreateCommand();
        comando.CommandText =
            "SELECT a.attname, has_column_privilege('cae_app_soporte', a.attrelid, a.attnum, 'SELECT') " +
            "FROM pg_attribute a " +
            "WHERE a.attrelid = format('public.%I', @tabla::text)::regclass AND a.attnum > 0 AND NOT a.attisdropped;";
        comando.Parameters.AddWithValue("tabla", tabla);

        var legibles = new List<string>();
        var todas = new List<string>();
        await using var lector = await comando.ExecuteReaderAsync();
        while (await lector.ReadAsync())
        {
            todas.Add(lector.GetString(0));
            if (lector.GetBoolean(1)) legibles.Add(lector.GetString(0));
        }

        return (legibles, todas);
    }

    private async Task<NpgsqlConnection> AbrirComoSoporteAsync()
    {
        var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();

        await ContextoRlsDePruebas.FijarAsync(conexion, tenantId: _tenant);

        await using var setRol = conexion.CreateCommand();
        setRol.CommandText = "SET ROLE cae_app_soporte;";
        await setRol.ExecuteNonQueryAsync();

        return conexion;
    }

    private CaeManagerDbContext CrearContexto(Guid? sesionPrivilegiadaId = null, string? cadenaConexion = null)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(cadenaConexion ?? _cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(
                new TenantSelladoInterceptor(tenantActual),
                new TenantRlsConnectionInterceptor(
                    tenantActual,
                    new ClienteActivoSeleccionadoFalso(_tenant, sesionPrivilegiadaId),
                    new CurrentUserServiceFalso(Guid.NewGuid(), tenantOrigenId: _tenant), BaseDatosPostgresDePruebas.FirmanteContextoRls))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private sealed class ClienteActivoSeleccionadoFalso(Guid tenantId, Guid? sesionPrivilegiadaId)
        : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => tenantId;

        public Guid? AsignacionOperacionIdSeleccionada => null;

        public Guid? SesionPrivilegiadaIdSeleccionada => sesionPrivilegiadaId;
    }
}
