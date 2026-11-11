using CaeManager.Application.AltaAutonoma;
using CaeManager.Infrastructure.AltaAutonoma;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.AltaAutonoma;

/// <summary>
/// Caducidad y borrado de datos personales de las Solicitudes de alta (A8, DA-7), medidos con una conexión
/// autenticada como <c>cae_app_runtime</c>, el rol del servicio de fondo:
/// <list type="bullet">
/// <item>caduca a los 7 días y no antes, solo desde los estados que admite el dominio, y nunca una Solicitud de alta
/// pagada, completada, reembolsada, derivada ni bloqueada;</item>
/// <item>borra los datos personales a los 30 días desde la creación, deja el resto y es idempotente;</item>
/// <item>trabaja por lotes, salta lo que otra transacción tiene bloqueado y deja traza en <c>TrazasPurga</c>;</item>
/// <item>la tabla sigue cerrada al runtime y las funciones no las ejecuta ningún otro rol.</item>
/// </list>
/// Sembrar y leer el estado lo hace la conexión de pruebas, que es superusuario.
/// </summary>
public class LimpiezaSolicitudesAltaBajoRuntimeTests : IAsyncLifetime
{
    private const int Lote = 100;

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();

    public Task InitializeAsync() => BaseDatosPostgresDePruebas.MigrarAsync(_cadenaConexion);

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Theory]
    [InlineData("Iniciada")]
    [InlineData("CorreoVerificado")]
    [InlineData("DatosCompletos")]
    [InlineData("PendientePago")]
    public async Task Caduca_a_los_7_dias_y_no_antes_sin_borrar_todavia_los_datos(string estado)
    {
        var reciente = await SembrarAsync(estado, edadHoras: 6 * 24 + 23, haceHorasDelUltimoCambio: 48);
        var vencida = await SembrarAsync(estado, edadHoras: 7 * 24 + 1, haceHorasDelUltimoCambio: 48);

        (await CaducarAsync()).Should().Be(1);

        (await LeerAsync(reciente)).Estado.Should().Be(estado, "le queda menos de una hora de plazo");
        var fila = await LeerAsync(vencida);
        fila.Estado.Should().Be("Caducada");
        fila.Version.Should().Be(4, "la transición incrementa VersionFila (la siembra parte de 3)");
        fila.UltimoCambio.Should().BeAfter(fila.Creada.AddDays(7), "la caducidad se anota con el reloj de la base");
        fila.Correo.Should().NotBeNull("caducar no borra: los datos se borran a los 30 días");
        fila.DatosPersonalesBorradosEnUtc.Should().BeNull();
    }

    [Theory]
    [InlineData("Pagada")]
    [InlineData("Completada")]
    [InlineData("Reembolsada")]
    [InlineData("DerivadaAAcceso")]
    [InlineData("Bloqueada")]
    public async Task No_toca_una_solicitud_en_un_estado_que_no_caduca_ni_a_los_7_ni_a_los_60_dias(string estado)
    {
        var id = await SembrarAsync(estado, edadHoras: 60 * 24, haceHorasDelUltimoCambio: 60 * 24);
        var antes = await LeerAsync(id);

        (await CaducarAsync()).Should().Be(0);
        (await BorrarAsync()).Should().Be(0);

        (await LeerAsync(id)).Should().Be(antes, "ni un solo campo ni VersionFila");
    }

    [Fact]
    public async Task Una_solicitud_pendiente_de_pago_con_la_sesion_de_Checkout_en_curso_espera_a_que_acabe()
    {
        var enCurso = await SembrarAsync("PendientePago", edadHoras: 7 * 24 + 1, haceHorasDelUltimoCambio: 0.2);
        var terminada = await SembrarAsync("PendientePago", edadHoras: 7 * 24 + 1, haceHorasDelUltimoCambio: 2);

        (await CaducarAsync()).Should().Be(1);

        (await LeerAsync(enCurso)).Estado.Should().Be("PendientePago",
            "el webhook de un pago hecho a tiempo no podría registrarlo sobre una fila ya Caducada");
        (await LeerAsync(terminada)).Estado.Should().Be("Caducada");
    }

    [Fact]
    public async Task Borra_los_datos_personales_a_los_30_dias_desde_la_creacion_y_deja_el_resto()
    {
        var joven = await SembrarAsync("Caducada", edadHoras: 29 * 24 + 23, haceHorasDelUltimoCambio: 22 * 24, conAceptaciones: true);
        var vieja = await SembrarAsync("Caducada", edadHoras: 30 * 24 + 1, haceHorasDelUltimoCambio: 23 * 24, conAceptaciones: true);
        var ajena = await SembrarAsync("PendientePago", edadHoras: 24, haceHorasDelUltimoCambio: 24, conAceptaciones: true);
        var antesJoven = await LeerAsync(joven);
        var antesAjena = await LeerAsync(ajena);
        var antesVieja = await LeerAsync(vieja);

        (await BorrarAsync()).Should().Be(1);

        (await LeerAsync(joven)).Should().Be(antesJoven, "le falta menos de una hora para los 30 días");
        (await LeerAsync(ajena)).Should().Be(antesAjena, "las aceptaciones de otra Solicitud de alta no se tocan");

        var fila = await LeerAsync(vieja);
        fila.Estado.Should().Be("Caducada");
        fila.Correo.Should().BeNull();
        fila.CorreoNormalizado.Should().BeNull();
        fila.HashContrasena.Should().BeNull();
        fila.HashToken.Should().BeNull();
        fila.TokenCaducaEnUtc.Should().BeNull();
        fila.Ip.Should().BeNull();
        fila.UserAgent.Should().BeNull();
        fila.RazonSocial.Should().BeNull();
        fila.CifNif.Should().BeNull();
        fila.DomicilioFiscal.Should().BeNull();
        fila.Telefono.Should().BeNull();
        fila.CheckoutSessionId.Should().BeNull();
        fila.Aceptaciones.Should().Be(0, "las aceptaciones llevan la IP");
        fila.DatosPersonalesBorradosEnUtc.Should().NotBeNull();
        fila.Version.Should().Be(4);
        fila.Tramo.Should().Be("De1A10", "el tramo y la modalidad quedan, como en el dominio");
        fila.Modalidad.Should().Be("Mensual");
        fila.UltimoCambio.Should().Be(antesVieja.UltimoCambio, "el borrado no es una transición de estado");
        fila.Creada.Should().Be(antesVieja.Creada);
    }

    [Fact]
    public async Task Es_idempotente_y_deja_una_traza_con_el_recuento()
    {
        await SembrarAsync("Caducada", edadHoras: 31 * 24, haceHorasDelUltimoCambio: 24 * 24, conAceptaciones: true);
        await SembrarAsync("Caducada", edadHoras: 31 * 24, haceHorasDelUltimoCambio: 24 * 24);

        (await BorrarAsync()).Should().Be(2);
        var primera = await LeerTodasAsync();

        (await BorrarAsync()).Should().Be(0, "repetirlo no cambia nada");
        (await CaducarAsync()).Should().Be(0);
        (await LeerTodasAsync()).Should().BeEquivalentTo(primera, "ni VersionFila ni la fecha de borrado cambian");

        await using var propietario = await AbrirComoPropietarioAsync();
        await using var traza = new NpgsqlCommand(
            "SELECT \"Filas\", \"Rol\", \"CorteUtc\" FROM \"TrazasPurga\" WHERE \"Purga\" = 'SolicitudesAlta'", propietario);
        await using var lector = await traza.ExecuteReaderAsync();
        (await lector.ReadAsync()).Should().BeTrue("la purga deja su evidencia");
        lector.GetInt32(0).Should().Be(2);
        lector.GetString(1).Should().Be("cae_app_runtime");
        lector.GetDateTime(2).Should().BeCloseTo(DateTime.UtcNow.AddDays(-30), TimeSpan.FromMinutes(5));
        (await lector.ReadAsync()).Should().BeFalse("la segunda llamada, sin filas, no escribe traza");
    }

    [Fact]
    public async Task Caduca_y_borra_por_lotes_acotados()
    {
        for (var i = 0; i < 5; i++)
            await SembrarAsync("Iniciada", edadHoras: 31 * 24, haceHorasDelUltimoCambio: 31 * 24);

        await using var conexion = await AbrirComoRuntimeAsync();
        (await EscalarAsync(conexion, "SELECT app_alta_caducar_solicitudes(2)")).Should().Be(2);
        (await EscalarAsync(conexion, "SELECT app_alta_caducar_solicitudes(2)")).Should().Be(2);
        (await EscalarAsync(conexion, "SELECT app_alta_caducar_solicitudes(2)")).Should().Be(1);
        (await EscalarAsync(conexion, "SELECT app_alta_caducar_solicitudes(2)")).Should().Be(0);

        (await EscalarAsync(conexion, "SELECT app_alta_borrar_datos_personales(4)")).Should().Be(4);
        (await EscalarAsync(conexion, "SELECT app_alta_borrar_datos_personales(4)")).Should().Be(1);
        (await EscalarAsync(conexion, "SELECT app_alta_borrar_datos_personales(4)")).Should().Be(0);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1001")]
    [InlineData("NULL")]
    public async Task Un_lote_fuera_de_rango_se_rechaza(string lote)
    {
        await using var conexion = await AbrirComoRuntimeAsync();

        (await RechazoAsync(conexion, $"SELECT app_alta_caducar_solicitudes({lote})")).SqlState.Should().Be("22023");
        (await RechazoAsync(conexion, $"SELECT app_alta_borrar_datos_personales({lote})")).SqlState.Should().Be("22023");
    }

    [Fact]
    public async Task Salta_lo_que_otra_transaccion_tiene_bloqueado_sin_esperarlo()
    {
        var libre = await SembrarAsync("Iniciada", edadHoras: 8 * 24, haceHorasDelUltimoCambio: 8 * 24);
        var bloqueada = await SembrarAsync("Iniciada", edadHoras: 8 * 24, haceHorasDelUltimoCambio: 8 * 24);

        // El «webhook de pago» (aquí, el propietario) retiene la fila con FOR UPDATE.
        await using var retenedor = await AbrirComoPropietarioAsync();
        await using var transaccion = await retenedor.BeginTransactionAsync();
        await using (var bloqueo = new NpgsqlCommand(
            $"SELECT 1 FROM \"SolicitudesAlta\" WHERE \"Id\" = '{bloqueada}' FOR UPDATE", retenedor, transaccion))
            await bloqueo.ExecuteScalarAsync();

        await using (var conexion = await AbrirComoRuntimeAsync())
        {
            var tarea = EscalarAsync(conexion, "SELECT app_alta_caducar_solicitudes(10)");
            (await Task.WhenAny(tarea, Task.Delay(TimeSpan.FromSeconds(20)))).Should().BeSameAs(tarea, "SKIP LOCKED no espera");
            (await tarea).Should().Be(1);
        }

        (await LeerAsync(libre)).Estado.Should().Be("Caducada");
        (await LeerAsync(bloqueada)).Estado.Should().Be("Iniciada", "la fila bloqueada se deja para la siguiente pasada");

        await transaccion.CommitAsync();
        (await CaducarAsync()).Should().Be(1);
        (await LeerAsync(bloqueada)).Estado.Should().Be("Caducada");
    }

    [Fact]
    public async Task Una_pasada_del_servicio_bajo_el_runtime_caduca_y_borra_lo_que_toca()
    {
        var sinPagar = await SembrarAsync("DatosCompletos", edadHoras: 31 * 24, haceHorasDelUltimoCambio: 31 * 24);
        var pagada = await SembrarAsync("Pagada", edadHoras: 31 * 24, haceHorasDelUltimoCambio: 31 * 24);

        await using var conexion = await AbrirComoRuntimeAsync();
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>().UseNpgsql(conexion).Options;
        await using var contexto = new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), new SinTenant());
        var servicio = new ServicioLimpiezaSolicitudesAlta(
            new LimpiezaSolicitudesAlta(contexto), Options.Create(new LimpiezaSolicitudesAltaOptions()),
            NullLogger<ServicioLimpiezaSolicitudesAlta>.Instance);

        (await servicio.EjecutarAsync(CancellationToken.None)).Should().Be(new ResultadoLimpiezaAlta(1, 1));
        (await servicio.EjecutarAsync(CancellationToken.None)).Should().Be(new ResultadoLimpiezaAlta(0, 0));

        var fila = await LeerAsync(sinPagar);
        fila.Estado.Should().Be("Caducada");
        fila.Correo.Should().BeNull();
        fila.DatosPersonalesBorradosEnUtc.Should().NotBeNull();
        var intacta = await LeerAsync(pagada);
        intacta.Estado.Should().Be("Pagada");
        intacta.Correo.Should().NotBeNull();
    }

    [Fact]
    public async Task La_tabla_sigue_cerrada_al_runtime_aunque_ejecute_las_funciones()
    {
        await using var conexion = await AbrirComoRuntimeAsync();
        await EscalarAsync(conexion, "SELECT app_alta_caducar_solicitudes(1)");

        foreach (var tabla in new[] { "SolicitudesAlta", "AceptacionesSolicitudAlta" })
        {
            (await RechazoAsync(conexion, $"SELECT count(*) FROM \"{tabla}\"")).SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
            (await RechazoAsync(conexion, $"DELETE FROM \"{tabla}\"")).SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        }

        (await RechazoAsync(conexion, "UPDATE \"SolicitudesAlta\" SET \"Estado\" = 'Caducada'")).SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        (await RechazoAsync(conexion, "SELECT count(*) FROM \"TrazasPurga\"")).SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege,
            "la traza sigue sin ser legible por el runtime");
    }

    [Theory]
    [InlineData("cae_app_soporte")]
    [InlineData("cae_app_aprovisionamiento")]
    public async Task Otro_rol_de_la_aplicacion_no_puede_ejecutar_las_funciones(string rol)
    {
        await using var propietario = await AbrirComoPropietarioAsync();
        await using (var cambio = new NpgsqlCommand($"SET ROLE {rol}", propietario))
            await cambio.ExecuteNonQueryAsync();

        (await RechazoAsync(propietario, "SELECT app_alta_caducar_solicitudes(1)")).SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        (await RechazoAsync(propietario, "SELECT app_alta_borrar_datos_personales(1)")).SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Contrato_de_catalogo_de_las_dos_funciones_de_limpieza()
    {
        await using var propietario = await AbrirComoPropietarioAsync();
        await using var orden = new NpgsqlCommand(
            """
            SELECT p.proname,
                   p.prosecdef,
                   array_to_string(p.proconfig, ';'),
                   has_function_privilege('cae_app_runtime', p.oid, 'EXECUTE'),
                   has_function_privilege('cae_app_soporte', p.oid, 'EXECUTE'),
                   has_function_privilege('cae_app_aprovisionamiento', p.oid, 'EXECUTE'),
                   p.proacl IS NULL OR EXISTS (SELECT 1 FROM aclexplode(p.proacl) a WHERE a.grantee = 0),
                   pg_get_function_result(p.oid)
            FROM pg_proc p
            WHERE p.proname IN ('app_alta_caducar_solicitudes', 'app_alta_borrar_datos_personales') AND p.pronamespace = 'public'::regnamespace
            """, propietario);

        var vistas = 0;
        await using var lector = await orden.ExecuteReaderAsync();
        while (await lector.ReadAsync())
        {
            var nombre = lector.GetString(0);
            vistas++;
            lector.GetBoolean(1).Should().BeTrue($"{nombre}: SECURITY DEFINER");
            lector.GetString(2).Should().Be("search_path=pg_catalog, pg_temp", nombre);
            lector.GetBoolean(3).Should().BeTrue($"{nombre}: cae_app_runtime");
            lector.GetBoolean(4).Should().BeFalse($"{nombre}: cae_app_soporte");
            lector.GetBoolean(5).Should().BeFalse($"{nombre}: cae_app_aprovisionamiento");
            lector.GetBoolean(6).Should().BeFalse($"{nombre}: REVOKE ALL ... FROM PUBLIC");
            lector.GetString(7).Should().Be("integer", $"{nombre}: solo devuelve un recuento");
        }

        vistas.Should().Be(2, "las dos funciones existen, con una sola sobrecarga");
    }

    private sealed record Fila(
        string Estado, string? Correo, string? CorreoNormalizado, string? HashContrasena, string? HashToken,
        DateTime? TokenCaducaEnUtc, string? Ip, string? UserAgent, string? RazonSocial, string? CifNif,
        string? DomicilioFiscal, string? Telefono, string? CheckoutSessionId, DateTime? DatosPersonalesBorradosEnUtc,
        int Version, string? Tramo, string? Modalidad, DateTime UltimoCambio, DateTime Creada, long Aceptaciones);

    private const string ColumnasFila =
        """
        s."Estado", s."Correo", s."CorreoNormalizado", s."HashContrasena", s."HashTokenVerificacion",
        s."TokenVerificacionCaducaEnUtc", s."IpOrigen", s."UserAgentOrigen", s."RazonSocial", s."CifNif",
        s."DomicilioFiscal", s."Telefono", s."CheckoutSessionId", s."DatosPersonalesBorradosEnUtc",
        s."VersionFila", s."Tramo", s."Modalidad", s."UltimoCambioEstadoEnUtc", s."CreadaEnUtc",
        (SELECT count(*) FROM "AceptacionesSolicitudAlta" a WHERE a."SolicitudAltaId" = s."Id")
        """;

    private static Fila LeerFila(NpgsqlDataReader l)
    {
        string? Texto(int i) => l.IsDBNull(i) ? null : l.GetString(i);
        DateTime? Fecha(int i) => l.IsDBNull(i) ? null : l.GetDateTime(i);
        return new Fila(
            l.GetString(0), Texto(1), Texto(2), Texto(3), Texto(4), Fecha(5), Texto(6), Texto(7), Texto(8), Texto(9),
            Texto(10), Texto(11), Texto(12), Fecha(13), l.GetInt32(14), Texto(15), Texto(16), l.GetDateTime(17),
            l.GetDateTime(18), l.GetInt64(19));
    }

    private async Task<Fila> LeerAsync(Guid id)
    {
        await using var conexion = await AbrirComoPropietarioAsync();
        await using var orden = new NpgsqlCommand($"SELECT {ColumnasFila} FROM \"SolicitudesAlta\" s WHERE s.\"Id\" = @id", conexion);
        orden.Parameters.AddWithValue("id", id);
        await using var lector = await orden.ExecuteReaderAsync();
        (await lector.ReadAsync()).Should().BeTrue("la Solicitud de alta existe");
        return LeerFila(lector);
    }

    private async Task<List<Fila>> LeerTodasAsync()
    {
        await using var conexion = await AbrirComoPropietarioAsync();
        await using var orden = new NpgsqlCommand($"SELECT {ColumnasFila} FROM \"SolicitudesAlta\" s ORDER BY s.\"Id\"", conexion);
        await using var lector = await orden.ExecuteReaderAsync();
        var filas = new List<Fila>();
        while (await lector.ReadAsync())
            filas.Add(LeerFila(lector));
        return filas;
    }

    /// <summary>
    /// Siembra una Solicitud de alta de la edad dada (<c>CaducaEnUtc</c> = creación + 7 días) con todos los datos
    /// personales rellenos y <c>VersionFila</c> 3. El último cambio de estado es hace <paramref name="haceHorasDelUltimoCambio"/>.
    /// </summary>
    private async Task<Guid> SembrarAsync(string estado, double edadHoras, double haceHorasDelUltimoCambio, bool conAceptaciones = false)
    {
        var id = Guid.NewGuid();
        await using var conexion = await AbrirComoPropietarioAsync();
        await using (var orden = new NpgsqlCommand(
            """
            INSERT INTO "SolicitudesAlta" (
                "Id", "Estado", "Correo", "CorreoNormalizado", "HashContrasena", "HashTokenVerificacion",
                "TokenVerificacionCaducaEnUtc", "IpOrigen", "UserAgentOrigen", "CreadaEnUtc", "CaducaEnUtc",
                "UltimoCambioEstadoEnUtc", "RazonSocial", "CifNif", "DomicilioFiscal", "Telefono", "Tramo", "Modalidad",
                "DeclaraRepresentacion", "DeclaraActividadProfesional", "CheckoutSessionId", "TenantIdCreado", "Motivo",
                "VersionFila")
            VALUES (
                @id, @estado, @correo, upper(@correo), 'AQAAAAIAAYagAAAAEHash', @hashToken,
                now() + interval '1 day', '203.0.113.9', 'Navegador de prueba',
                now() - @edad * interval '1 hour', now() - @edad * interval '1 hour' + interval '7 days',
                now() - @cambio * interval '1 hour', 'Empresa de prueba SL', 'B12345674', 'Calle Falsa 1', '600000000',
                'De1A10', 'Mensual', true, false, 'cs_test_1', @tenant, @motivo, 3)
            """, conexion))
        {
            orden.Parameters.AddWithValue("id", id);
            orden.Parameters.AddWithValue("estado", estado);
            orden.Parameters.AddWithValue("correo", $"persona-{id:N}@empresa.test");
            orden.Parameters.AddWithValue("hashToken", Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(id.ToByteArray())));
            orden.Parameters.AddWithValue("edad", edadHoras);
            orden.Parameters.AddWithValue("cambio", haceHorasDelUltimoCambio);
            orden.Parameters.AddWithValue("tenant", estado == "Completada" ? Guid.NewGuid() : DBNull.Value);
            orden.Parameters.AddWithValue("motivo", estado is "Bloqueada" or "Reembolsada" ? "motivo de prueba" : DBNull.Value);
            await orden.ExecuteNonQueryAsync();
        }

        if (conAceptaciones)
        {
            await using var aceptacion = new NpgsqlCommand(
                """
                INSERT INTO "AceptacionesSolicitudAlta" ("Id", "SolicitudAltaId", "Documento", "Version", "HashSha256", "DeclaraFacultades", "Ip", "AceptadaEnUtc")
                VALUES (@aceptacion, @id, 'Condiciones', '2026-11', repeat('a', 64), true, '203.0.113.9', now())
                """, conexion);
            aceptacion.Parameters.AddWithValue("aceptacion", Guid.NewGuid());
            aceptacion.Parameters.AddWithValue("id", id);
            await aceptacion.ExecuteNonQueryAsync();
        }

        return id;
    }

    private async Task<int> CaducarAsync()
    {
        await using var conexion = await AbrirComoRuntimeAsync();
        return await EscalarAsync(conexion, $"SELECT app_alta_caducar_solicitudes({Lote})");
    }

    private async Task<int> BorrarAsync()
    {
        await using var conexion = await AbrirComoRuntimeAsync();
        return await EscalarAsync(conexion, $"SELECT app_alta_borrar_datos_personales({Lote})");
    }

    private static async Task<int> EscalarAsync(NpgsqlConnection conexion, string sql)
    {
        await using var orden = new NpgsqlCommand(sql, conexion);
        return (int)(await orden.ExecuteScalarAsync())!;
    }

    private async Task<NpgsqlConnection> AbrirComoPropietarioAsync()
    {
        var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        return conexion;
    }

    /// <summary>Sin contexto RLS firmado a propósito: el servicio de fondo no tiene petición de usuario.</summary>
    private async Task<NpgsqlConnection> AbrirComoRuntimeAsync()
    {
        var conexion = new NpgsqlConnection(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion));
        await conexion.OpenAsync();

        await using var identidad = new NpgsqlCommand("SELECT session_user::text || '/' || current_user::text", conexion);
        ((string)(await identidad.ExecuteScalarAsync())!).Should().Be("cae_app_runtime/cae_app_runtime");
        return conexion;
    }

    private static async Task<PostgresException> RechazoAsync(NpgsqlConnection conexion, string sql)
    {
        var ejecutar = async () =>
        {
            await using var orden = new NpgsqlCommand(sql, conexion);
            await orden.ExecuteScalarAsync();
        };
        return (await ejecutar.Should().ThrowAsync<PostgresException>()).Which;
    }

    private sealed class SinTenant : CaeManager.Application.Common.ITenantActual
    {
        public Guid? TenantId => null;
    }
}
