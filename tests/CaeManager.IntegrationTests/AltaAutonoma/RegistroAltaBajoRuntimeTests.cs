using CaeManager.Application.AltaAutonoma;
using CaeManager.Application.Common;
using CaeManager.Domain.AltaAutonoma;
using CaeManager.Infrastructure.AltaAutonoma;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.AltaAutonoma;

/// <summary>
/// Vía de acceso del registro anónimo del alta autónoma (A3) a <c>SolicitudesAlta</c>, medida con conexiones
/// autenticadas como <c>cae_app_runtime</c> (no superusuario ni <c>SET ROLE</c> desde el propietario):
/// <list type="bullet">
/// <item>la tabla sigue cerrada al rol, y solo las dos funciones <c>SECURITY DEFINER</c> llegan a ella;</item>
/// <item>las funciones exigen el contexto RLS firmado, y con el contexto anónimo de una petición sí responden;</item>
/// <item>la verificación es de un solo uso y respeta la caducidad del enlace y de la Solicitud de alta;</item>
/// <item>el catálogo: definidoras, <c>search_path</c> fijo, sin <c>PUBLIC</c>, ni soporte ni aprovisionamiento.</item>
/// </list>
/// Las comprobaciones de estado leen la fila con la conexión de pruebas, que es superusuario.
/// </summary>
public class RegistroAltaBajoRuntimeTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();

    public Task InitializeAsync() => BaseDatosPostgresDePruebas.MigrarAsync(_cadenaConexion);

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task El_runtime_registra_y_verifica_por_las_funciones_y_el_enlace_solo_vale_una_vez()
    {
        var token = ServicioRegistroAlta.NuevoToken();
        var solicitud = Nueva(token);

        await using (var conexion = await AbrirComoRuntimeFirmadoAsync())
        await using (var contexto = ContextoSobre(conexion))
        {
            var acceso = new AccesoSolicitudesAlta(contexto);
            await acceso.IniciarAsync(solicitud, CancellationToken.None);

            (await acceso.VerificarCorreoAsync(ServicioRegistroAlta.NuevoToken(), CancellationToken.None))
                .Should().Be(ResultadoVerificacionCorreoAlta.NoValido, "un token que no es el del enlace");
            (await LeerAsync(solicitud.Id)).Estado.Should().Be("Iniciada", "un token equivocado no cambia nada");

            (await acceso.VerificarCorreoAsync(token, CancellationToken.None)).Should().Be(ResultadoVerificacionCorreoAlta.Verificado);
            (await acceso.VerificarCorreoAsync(token, CancellationToken.None))
                .Should().Be(ResultadoVerificacionCorreoAlta.NoValido, "el enlace es de un solo uso");
        }

        var fila = await LeerAsync(solicitud.Id);
        fila.Estado.Should().Be("CorreoVerificado");
        fila.HashToken.Should().BeNull("el hash se borra al usarse");
        fila.HashContrasena.Should().Be(solicitud.HashContrasena);
        fila.Version.Should().Be(1);
    }

    [Fact]
    public async Task La_base_guarda_solo_el_hash_del_token()
    {
        var token = ServicioRegistroAlta.NuevoToken();
        var solicitud = Nueva(token);
        await using (var conexion = await AbrirComoRuntimeFirmadoAsync())
        await using (var contexto = ContextoSobre(conexion))
            await new AccesoSolicitudesAlta(contexto).IniciarAsync(solicitud, CancellationToken.None);

        var fila = await LeerAsync(solicitud.Id);
        fila.HashToken.Should().Be(SolicitudAlta.CalcularHashToken(token));
        fila.HashToken.Should().NotContain(token);
        fila.Estado.Should().Be("Iniciada");
    }

    [Theory]
    [InlineData("\"TokenVerificacionCaducaEnUtc\"", ResultadoVerificacionCorreoAlta.EnlaceCaducado)]
    [InlineData("\"CaducaEnUtc\"", ResultadoVerificacionCorreoAlta.SolicitudCaducada)]
    public async Task Un_enlace_o_una_solicitud_caducados_no_verifican(string columna, ResultadoVerificacionCorreoAlta esperado)
    {
        var token = ServicioRegistroAlta.NuevoToken();
        var solicitud = Nueva(token);
        await using (var conexion = await AbrirComoRuntimeFirmadoAsync())
        await using (var contexto = ContextoSobre(conexion))
            await new AccesoSolicitudesAlta(contexto).IniciarAsync(solicitud, CancellationToken.None);

        await EjecutarComoPropietarioAsync($"UPDATE \"SolicitudesAlta\" SET {columna} = now() - interval '1 second' WHERE \"Id\" = '{solicitud.Id}'");

        await using (var conexion = await AbrirComoRuntimeFirmadoAsync())
        await using (var contexto = ContextoSobre(conexion))
            (await new AccesoSolicitudesAlta(contexto).VerificarCorreoAsync(token, CancellationToken.None)).Should().Be(esperado);

        (await LeerAsync(solicitud.Id)).Estado.Should().Be("Iniciada", "caducado, el enlace no verifica");
    }

    [Theory]
    [InlineData("Bloqueada")]
    [InlineData("Caducada")]
    public async Task Fuera_de_Iniciada_el_enlace_no_verifica_aunque_conserve_el_hash(string estado)
    {
        var token = ServicioRegistroAlta.NuevoToken();
        var solicitud = Nueva(token);
        await using (var conexion = await AbrirComoRuntimeFirmadoAsync())
        await using (var contexto = ContextoSobre(conexion))
            await new AccesoSolicitudesAlta(contexto).IniciarAsync(solicitud, CancellationToken.None);

        await EjecutarComoPropietarioAsync($"UPDATE \"SolicitudesAlta\" SET \"Estado\" = '{estado}' WHERE \"Id\" = '{solicitud.Id}'");

        await using (var conexion = await AbrirComoRuntimeFirmadoAsync())
        await using (var contexto = ContextoSobre(conexion))
            (await new AccesoSolicitudesAlta(contexto).VerificarCorreoAsync(token, CancellationToken.None))
                .Should().Be(ResultadoVerificacionCorreoAlta.NoValido);

        var fila = await LeerAsync(solicitud.Id);
        fila.Estado.Should().Be(estado, "solo una Solicitud de alta Iniciada pasa a CorreoVerificado");
        fila.Version.Should().Be(0);
    }

    [Fact]
    public async Task Cuatro_confirmaciones_simultaneas_del_mismo_enlace_verifican_una_sola_vez()
    {
        const int confirmaciones = 4;
        var token = ServicioRegistroAlta.NuevoToken();
        var solicitud = Nueva(token);
        await using (var conexion = await AbrirComoRuntimeFirmadoAsync())
        await using (var contexto = ContextoSobre(conexion))
            await new AccesoSolicitudesAlta(contexto).IniciarAsync(solicitud, CancellationToken.None);

        async Task<ResultadoVerificacionCorreoAlta> ConfirmarAsync()
        {
            await using var conexion = await AbrirComoRuntimeFirmadoAsync();
            await using var contexto = ContextoSobre(conexion);
            return await new AccesoSolicitudesAlta(contexto).VerificarCorreoAsync(token, CancellationToken.None);
        }

        // Solape determinista: el propietario retiene la fila con FOR UPDATE, las cuatro confirmaciones quedan esperando
        // ese bloqueo (en el FOR UPDATE de la función o, si alguien lo quitara, en su UPDATE) y se sueltan a la vez.
        await using var retenedor = new NpgsqlConnection(_cadenaConexion);
        await retenedor.OpenAsync();
        await using var transaccion = await retenedor.BeginTransactionAsync();
        await using (var bloqueo = new NpgsqlCommand(
            $"SELECT 1 FROM \"SolicitudesAlta\" WHERE \"Id\" = '{solicitud.Id}' FOR UPDATE", retenedor, transaccion))
            await bloqueo.ExecuteScalarAsync();

        var tareas = Enumerable.Range(0, confirmaciones).Select(_ => Task.Run(ConfirmarAsync)).ToArray();

        await using (var observador = new NpgsqlConnection(_cadenaConexion))
        {
            await observador.OpenAsync();
            await using var esperando = new NpgsqlCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND usename = 'cae_app_runtime' AND wait_event_type = 'Lock'",
                observador);
            var limite = DateTime.UtcNow.AddSeconds(30);
            while ((long)(await esperando.ExecuteScalarAsync())! < confirmaciones)
            {
                DateTime.UtcNow.Should().BeBefore(limite, "las cuatro confirmaciones deben quedar esperando el bloqueo de la fila");
                await Task.Delay(25);
            }
        }

        await transaccion.CommitAsync();
        var resultados = await Task.WhenAll(tareas);

        resultados.Count(r => r == ResultadoVerificacionCorreoAlta.Verificado).Should().Be(1, "el enlace es de un solo uso también en carrera");
        resultados.Where(r => r != ResultadoVerificacionCorreoAlta.Verificado).Should().AllBeEquivalentTo(ResultadoVerificacionCorreoAlta.NoValido);
        (await LeerAsync(solicitud.Id)).Version.Should().Be(1, "la fila se actualiza una sola vez");
    }

    [Fact]
    public async Task Sin_contexto_firmado_las_funciones_no_responden()
    {
        await using var conexion = await AbrirComoRuntimeAsync();
        var hash = SolicitudAlta.CalcularHashToken(ServicioRegistroAlta.NuevoToken());

        (await RechazoAsync(conexion, $"SELECT app_alta_verificar_correo('{hash}')")).SqlState.Should().Be("42501");
        (await RechazoAsync(conexion,
                $"SELECT app_alta_iniciar_solicitud('{Guid.NewGuid()}', 'a@b.es', 'A@B.ES', 'h', '{hash}', now() + interval '1 hour', " +
                "'203.0.113.9', null, now(), now() + interval '1 day')"))
            .SqlState.Should().Be("42501");
    }

    [Fact]
    public async Task Con_contexto_firmado_la_tabla_sigue_cerrada_al_runtime()
    {
        await using var conexion = await AbrirComoRuntimeFirmadoAsync();

        (await RechazoAsync(conexion, "SELECT count(*) FROM \"SolicitudesAlta\"")).SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task El_registro_rechaza_plazos_fuera_de_los_del_dominio()
    {
        await using var conexion = await AbrirComoRuntimeFirmadoAsync();
        var hash = SolicitudAlta.CalcularHashToken(ServicioRegistroAlta.NuevoToken());

        (await RechazoAsync(conexion,
                $"SELECT app_alta_iniciar_solicitud('{Guid.NewGuid()}', 'a@b.es', 'A@B.ES', 'h', '{hash}', now() + interval '30 days', " +
                "'203.0.113.9', null, now(), now() + interval '1 day')"))
            .SqlState.Should().Be("22023", "un enlace no puede durar más de 24 horas");
        (await RechazoAsync(conexion,
                $"SELECT app_alta_iniciar_solicitud('{Guid.NewGuid()}', 'a@b.es', 'A@B.ES', 'h', 'token-en-claro', now() + interval '1 hour', " +
                "'203.0.113.9', null, now(), now() + interval '1 day')"))
            .SqlState.Should().Be("22023", "el token solo entra como SHA-256");
    }

    [Fact]
    public async Task Contrato_de_catalogo_de_las_funciones_del_registro()
    {
        await using var propietario = new NpgsqlConnection(_cadenaConexion);
        await propietario.OpenAsync();
        await using var orden = new NpgsqlCommand(
            """
            SELECT p.proname,
                   p.prosecdef,
                   array_to_string(p.proconfig, ';'),
                   has_function_privilege('cae_app_runtime', p.oid, 'EXECUTE'),
                   has_function_privilege('cae_app_soporte', p.oid, 'EXECUTE'),
                   has_function_privilege('cae_app_aprovisionamiento', p.oid, 'EXECUTE'),
                   p.proacl IS NULL OR EXISTS (SELECT 1 FROM aclexplode(p.proacl) a WHERE a.grantee = 0)
            FROM pg_proc p
            WHERE p.proname IN ('app_alta_iniciar_solicitud', 'app_alta_verificar_correo') AND p.pronamespace = 'public'::regnamespace
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
        }

        vistas.Should().Be(2, "las dos funciones existen, con una sola sobrecarga");
    }

    private static SolicitudAlta Nueva(string token) => SolicitudAlta.Iniciar(
        $"persona-{Guid.NewGuid():N}@empresa.test", "AQAAAAIAAYagAAAAEHashDePrueba", token, "203.0.113.9", "Navegador de prueba",
        DateTime.UtcNow);

    private async Task<NpgsqlConnection> AbrirComoRuntimeAsync()
    {
        var conexion = new NpgsqlConnection(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion));
        await conexion.OpenAsync();

        await using var identidad = new NpgsqlCommand("SELECT session_user::text || '/' || current_user::text", conexion);
        ((string)(await identidad.ExecuteScalarAsync())!).Should().Be("cae_app_runtime/cae_app_runtime");
        return conexion;
    }

    /// <summary>El contexto de una petición anónima: firmado, sin Tenant ni usuario.</summary>
    private async Task<NpgsqlConnection> AbrirComoRuntimeFirmadoAsync()
    {
        var conexion = await AbrirComoRuntimeAsync();
        await ContextoRlsDePruebas.FijarAsync(conexion);
        return conexion;
    }

    private static CaeManagerDbContext ContextoSobre(NpgsqlConnection conexion)
    {
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>().UseNpgsql(conexion).Options;
        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), new SinTenant());
    }

    private async Task<(string Estado, string? HashToken, string? HashContrasena, int Version)> LeerAsync(Guid id)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var orden = new NpgsqlCommand(
            "SELECT \"Estado\", \"HashTokenVerificacion\", \"HashContrasena\", \"VersionFila\" FROM \"SolicitudesAlta\" WHERE \"Id\" = @id", conexion);
        orden.Parameters.AddWithValue("id", id);
        await using var lector = await orden.ExecuteReaderAsync();
        (await lector.ReadAsync()).Should().BeTrue("la Solicitud de alta existe");
        return (lector.GetString(0), lector.IsDBNull(1) ? null : lector.GetString(1), lector.IsDBNull(2) ? null : lector.GetString(2), lector.GetInt32(3));
    }

    private async Task EjecutarComoPropietarioAsync(string sql)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var orden = new NpgsqlCommand(sql, conexion);
        (await orden.ExecuteNonQueryAsync()).Should().Be(1);
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

    private sealed class SinTenant : ITenantActual
    {
        public Guid? TenantId => null;
    }
}
