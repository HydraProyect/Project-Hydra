using CaeManager.Application.AltaAutonoma;
using CaeManager.Application.Common;
using CaeManager.Domain.AltaAutonoma;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.AltaAutonoma;
using CaeManager.Infrastructure.MultiTenancy;
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
/// <item>la tabla sigue cerrada al rol, y solo las funciones <c>SECURITY DEFINER</c> llegan a ella (A3: iniciar y
/// verificar; A4: consultar el enlace, verificar con la contraseña comprobada y completar los datos de la organización);</item>
/// <item>A4: la contraseña errónea no gasta el enlace, el credencial de continuación solo vale en <c>CorreoVerificado</c>
/// y una vez, y la función del CIF no devuelve nada del Tenant propietario;</item>
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

            (await acceso.ConsultarEnlaceAsync(ServicioRegistroAlta.NuevoToken(), CancellationToken.None))
                .Should().Be(EnlaceVerificacionAlta.NoValido, "un token que no es el del enlace no obtiene el hash");
            (await acceso.VerificarCorreoAsync(ServicioRegistroAlta.NuevoToken(), solicitud.HashContrasena!, ServicioRegistroAlta.NuevoToken(), CancellationToken.None))
                .Should().Be(ResultadoVerificacionCorreoAlta.NoValido, "un token que no es el del enlace");
            (await LeerAsync(solicitud.Id)).Estado.Should().Be("Iniciada", "un token equivocado no cambia nada");

            (await acceso.ConsultarEnlaceAsync(token, CancellationToken.None))
                .Should().Be(new EnlaceVerificacionAlta(null, solicitud.HashContrasena), "con el enlace vigente, el hash para comparar");
            (await acceso.VerificarCorreoAsync(token, solicitud.HashContrasena!, ServicioRegistroAlta.NuevoToken(), CancellationToken.None))
                .Should().Be(ResultadoVerificacionCorreoAlta.Verificado);
            (await acceso.VerificarCorreoAsync(token, solicitud.HashContrasena!, ServicioRegistroAlta.NuevoToken(), CancellationToken.None))
                .Should().Be(ResultadoVerificacionCorreoAlta.NoValido, "el enlace es de un solo uso");
            (await acceso.ConsultarEnlaceAsync(token, CancellationToken.None))
                .Should().Be(EnlaceVerificacionAlta.NoValido, "gastado el enlace, tampoco sale el hash");
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
        {
            var acceso = new AccesoSolicitudesAlta(contexto);
            (await acceso.ConsultarEnlaceAsync(token, CancellationToken.None)).Should().Be(new EnlaceVerificacionAlta(esperado, null));
            (await acceso.VerificarCorreoAsync(token, solicitud.HashContrasena!, ServicioRegistroAlta.NuevoToken(), CancellationToken.None))
                .Should().Be(esperado);
        }

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
        {
            var acceso = new AccesoSolicitudesAlta(contexto);
            (await acceso.ConsultarEnlaceAsync(token, CancellationToken.None)).Should().Be(EnlaceVerificacionAlta.NoValido);
            (await acceso.VerificarCorreoAsync(token, solicitud.HashContrasena!, ServicioRegistroAlta.NuevoToken(), CancellationToken.None))
                .Should().Be(ResultadoVerificacionCorreoAlta.NoValido);
        }

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
            return await new AccesoSolicitudesAlta(contexto).VerificarCorreoAsync(
                token, solicitud.HashContrasena!, ServicioRegistroAlta.NuevoToken(), CancellationToken.None);
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

        (await RechazoAsync(conexion, $"SELECT app_alta_verificar_correo('{hash}', 'h', '{hash}')")).SqlState.Should().Be("42501");
        (await RechazoAsync(conexion, $"SELECT * FROM app_alta_consultar_enlace('{hash}')")).SqlState.Should().Be("42501");
        (await RechazoAsync(conexion,
                $"SELECT * FROM app_alta_completar_datos_organizacion('{hash}', 'Empresa SL', 'B12345674', 'Calle 1', null, 'De1A10', true, false)"))
            .SqlState.Should().Be("42501");
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
            WHERE p.proname IN ('app_alta_iniciar_solicitud', 'app_alta_verificar_correo', 'app_alta_consultar_enlace', 'app_alta_completar_datos_organizacion', 'app_alta_consultar_continuacion') AND p.pronamespace = 'public'::regnamespace
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

        vistas.Should().Be(6, "cinco funciones, y la verificación con dos sobrecargas: la de un argumento sigue para la ranura anterior");
    }

    [Fact]
    public async Task La_verificacion_de_un_argumento_de_la_ranura_anterior_responde_NoValido_y_no_gasta_el_enlace()
    {
        var token = ServicioRegistroAlta.NuevoToken();
        var solicitud = Nueva(token);
        await using (var conexion = await AbrirComoRuntimeFirmadoAsync())
        {
            await using (var contexto = ContextoSobre(conexion))
                await new AccesoSolicitudesAlta(contexto).IniciarAsync(solicitud, CancellationToken.None);

            await using var orden = new NpgsqlCommand("SELECT app_alta_verificar_correo(@h)", conexion);
            orden.Parameters.AddWithValue("h", SolicitudAlta.CalcularHashToken(token));
            ((string)(await orden.ExecuteScalarAsync())!).Should().Be("NoValido", "sin la contraseña no se verifica (pre-secuestro)");
        }

        var fila = await LeerAsync(solicitud.Id);
        fila.Estado.Should().Be("Iniciada");
        fila.HashToken.Should().Be(SolicitudAlta.CalcularHashToken(token), "el enlace sigue valiendo para la verificación con contraseña");
    }

    [Fact]
    public async Task Consultar_la_continuacion_dice_el_estado_sin_cambiar_nada()
    {
        var (solicitud, credencial) = await VerificadaAsync();

        (await ContinuacionAsync(ServicioRegistroAlta.NuevoToken())).Should().Be(EstadoContinuacionAlta.NoValido, "credencial ajeno");
        (await ContinuacionAsync(credencial)).Should().Be(EstadoContinuacionAlta.CorreoVerificado);
        (await LeerAsync(solicitud.Id)).Version.Should().Be(1, "consultar no escribe");

        (await CompletarAsync(credencial, "B12345674")).Resultado.Should().Be(ResultadoDatosOrganizacionAlta.DatosCompletos);
        (await ContinuacionAsync(credencial)).Should().Be(EstadoContinuacionAlta.DatosCompletos, "recargar muestra lo guardado");
        (await LeerAsync(solicitud.Id)).Version.Should().Be(2);

        await EjecutarComoPropietarioAsync(
            $"UPDATE \"SolicitudesAlta\" SET \"CredencialContinuacionCaducaEnUtc\" = now() - interval '1 second' WHERE \"Id\" = '{solicitud.Id}'");
        (await ContinuacionAsync(credencial)).Should().Be(EstadoContinuacionAlta.Caducada);
    }

    [Fact]
    public async Task Consultar_la_continuacion_de_una_Solicitud_vencida_dice_Caducada_y_tras_derivar_NoValido()
    {
        var (vencida, credencialVencida) = await VerificadaAsync();
        await EjecutarComoPropietarioAsync(
            $"UPDATE \"SolicitudesAlta\" SET \"CaducaEnUtc\" = now() - interval '1 second' WHERE \"Id\" = '{vencida.Id}'");
        (await ContinuacionAsync(credencialVencida)).Should().Be(EstadoContinuacionAlta.Caducada);

        await TenantConCifAsync("B12345674");
        var (_, credencial) = await VerificadaAsync();
        (await CompletarAsync(credencial, "B12345674")).Resultado.Should().Be(ResultadoDatosOrganizacionAlta.DerivadaAAcceso);
        (await ContinuacionAsync(credencial)).Should().Be(EstadoContinuacionAlta.NoValido, "derivada, el credencial ya no existe");
    }

    [Fact]
    public async Task Sin_contexto_firmado_la_consulta_de_la_continuacion_no_responde()
    {
        await using var conexion = await AbrirComoRuntimeAsync();
        var hash = SolicitudAlta.CalcularHashToken(ServicioRegistroAlta.NuevoToken());

        (await RechazoAsync(conexion, $"SELECT app_alta_consultar_continuacion('{hash}')")).SqlState.Should().Be("42501");
        (await RechazoAsync(conexion, $"SELECT app_alta_verificar_correo('{hash}')")).SqlState.Should().Be("42501");
    }

    // B6(d): los Tenants creados antes de A1 no tienen fila en CifsPropietarioTenant; su CIF es el de su Empresa propia.
    [Theory]
    [InlineData("B12345674", "B12345674")]
    [InlineData("b12345674", "B12345674")]
    [InlineData("1234567L", "01234567L")]
    [InlineData(" B-12.345.674 ", "B12345674")]
    [InlineData("b 1234567-4", "B12345674")]
    public async Task El_CIF_de_la_Empresa_propia_de_un_Tenant_sin_fila_de_CIF_propietario_deriva_a_acceso(string guardado, string enviado)
    {
        var tenant = await TenantConEmpresaAsync(enviado, esPropia: true);
        await EjecutarComoPropietarioAsync($"UPDATE \"Empresas\" SET \"Cif\" = '{guardado}' WHERE \"TenantId\" = '{tenant}'");
        await ExigirSinCifPropietarioAsync(tenant);
        var (solicitud, credencial) = await VerificadaAsync();

        (await CompletarAsync(credencial, enviado))
            .Should().Be(new TransicionDatosOrganizacionAlta(ResultadoDatosOrganizacionAlta.DerivadaAAcceso, solicitud.Id));
        (await LeerAsync(solicitud.Id)).Estado.Should().Be("DerivadaAAcceso");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task El_CIF_de_una_Empresa_contraparte_o_de_una_Empresa_propia_eliminada_no_ocupa(bool esPropia, bool eliminada)
    {
        var tenant = await TenantConEmpresaAsync("B12345674", esPropia);
        if (eliminada)
            await EjecutarComoPropietarioAsync($"UPDATE \"Empresas\" SET \"EstaEliminado\" = true WHERE \"TenantId\" = '{tenant}'");
        var (_, credencial) = await VerificadaAsync();

        (await CompletarAsync(credencial, "B12345674")).Resultado.Should().Be(ResultadoDatosOrganizacionAlta.DatosCompletos);
    }

    private async Task<Guid> TenantConEmpresaAsync(string cif, bool esPropia)
    {
        var tenant = new Tenant("Anterior a A1 " + Guid.NewGuid());
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>().UseNpgsql(_cadenaConexion).Options;
        await using (var contexto = new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), new TenantActualAmbiental { TenantId = Guid.NewGuid() }))
        {
            contexto.Tenants.Add(tenant);
            await contexto.SaveChangesAsync();
        }

        var actual = new TenantActualAmbiental { TenantId = tenant.Id };
        var opcionesSelladas = new DbContextOptionsBuilder<CaeManagerDbContext>().UseNpgsql(_cadenaConexion)
            .AddInterceptors(new TenantSelladoInterceptor(actual)).Options;
        await using (var contexto = new CaeManagerDbContext(opcionesSelladas, new EphemeralDataProtectionProvider(), actual))
        {
            contexto.Empresas.Add(esPropia
                ? new Empresa("Empresa propia anterior a A1 SL", cif)
                : Empresa.CrearComoCliente("Cliente empresarial SL", cif, esCritico: false, notas: null, ejecutivoUsuarioId: null));
            await contexto.SaveChangesAsync();
        }

        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var orden = new NpgsqlCommand(
            "SELECT count(*) FROM \"Empresas\" WHERE \"TenantId\" = @t AND \"EsPropia\" = @p", conexion);
        orden.Parameters.AddWithValue("t", tenant.Id);
        orden.Parameters.AddWithValue("p", esPropia);
        ((long)(await orden.ExecuteScalarAsync())!).Should().Be(1, "guarda del instrumento: la Empresa quedó en ese Tenant");
        return tenant.Id;
    }

    private async Task ExigirSinCifPropietarioAsync(Guid tenant)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var orden = new NpgsqlCommand("SELECT count(*) FROM \"CifsPropietarioTenant\" WHERE \"TenantId\" = @t", conexion);
        orden.Parameters.AddWithValue("t", tenant);
        ((long)(await orden.ExecuteScalarAsync())!).Should().Be(0, "el caso es un Tenant sin fila de CIF propietario");
    }

    private async Task<EstadoContinuacionAlta> ContinuacionAsync(string credencial)
    {
        await using var conexion = await AbrirComoRuntimeFirmadoAsync();
        await using var contexto = ContextoSobre(conexion);
        return await new AccesoSolicitudesAlta(contexto).ConsultarContinuacionAsync(credencial, CancellationToken.None);
    }

    // ── Paso «Empresa» (A4) ──

    [Fact]
    public async Task Una_contrasena_que_no_casa_no_verifica_ni_gasta_el_enlace()
    {
        var token = ServicioRegistroAlta.NuevoToken();
        var solicitud = Nueva(token);
        await using var conexion = await AbrirComoRuntimeFirmadoAsync();
        await using var contexto = ContextoSobre(conexion);
        var acceso = new AccesoSolicitudesAlta(contexto);
        await acceso.IniciarAsync(solicitud, CancellationToken.None);

        (await acceso.VerificarCorreoAsync(token, "AQAAAAIAAYagAAAAEOtroHash", ServicioRegistroAlta.NuevoToken(), CancellationToken.None))
            .Should().Be(ResultadoVerificacionCorreoAlta.NoValido, "el hash comparado no es el guardado");
        var fila = await LeerAsync(solicitud.Id);
        fila.Estado.Should().Be("Iniciada");
        fila.HashToken.Should().Be(SolicitudAlta.CalcularHashToken(token), "el enlace no se gasta");

        (await acceso.VerificarCorreoAsync(token, solicitud.HashContrasena!, ServicioRegistroAlta.NuevoToken(), CancellationToken.None))
            .Should().Be(ResultadoVerificacionCorreoAlta.Verificado, "con la contraseña buena el mismo enlace sigue valiendo");
    }

    [Fact]
    public async Task Verificar_guarda_solo_el_hash_del_credencial_con_dos_horas_como_mucho()
    {
        var (solicitud, credencial) = await VerificadaAsync();

        var (hash, caduca) = await LeerCredencialAsync(solicitud.Id);
        hash.Should().Be(SolicitudAlta.CalcularHashToken(credencial)).And.NotBe(credencial);
        caduca.Should().NotBeNull();
        caduca!.Value.Should().BeAfter(DateTime.UtcNow.AddHours(1).AddMinutes(55)).And.BeBefore(DateTime.UtcNow.AddHours(2).AddMinutes(1));
    }

    [Fact]
    public async Task Con_un_CIF_libre_la_Solicitud_de_alta_pasa_a_DatosCompletos_y_el_credencial_sigue()
    {
        var (solicitud, credencial) = await VerificadaAsync();

        var transicion = await CompletarAsync(credencial, "B12345674");

        transicion.Should().Be(new TransicionDatosOrganizacionAlta(ResultadoDatosOrganizacionAlta.DatosCompletos, solicitud.Id));
        var fila = await LeerAsync(solicitud.Id);
        fila.Estado.Should().Be("DatosCompletos");
        fila.Version.Should().Be(2);
        (await LeerCredencialAsync(solicitud.Id)).Hash.Should().NotBeNull("el paso siguiente lo necesita");
        (await CompletarAsync(credencial, "B12345674")).Resultado
            .Should().Be(ResultadoDatosOrganizacionAlta.NoValido, "fuera de CorreoVerificado el paso no se repite");
    }

    [Fact]
    public async Task Con_un_CIF_con_Tenant_propietario_deriva_a_acceso_y_la_funcion_no_devuelve_nada_del_Tenant()
    {
        var tenant = await TenantConCifAsync("B12345674");
        var (solicitud, credencial) = await VerificadaAsync();

        await using (var conexion = await AbrirComoRuntimeFirmadoAsync())
        {
            await using var orden = new NpgsqlCommand(
                "SELECT * FROM app_alta_completar_datos_organizacion(@h, 'Empresa SL', 'B12345674', 'Calle 1', null, 'De1A10', true, false)", conexion);
            orden.Parameters.AddWithValue("h", SolicitudAlta.CalcularHashToken(credencial));
            await using var lector = await orden.ExecuteReaderAsync();
            Enumerable.Range(0, lector.FieldCount).Select(lector.GetName).Should().Equal("resultado", "solicitud_alta_id");
            (await lector.ReadAsync()).Should().BeTrue();
            lector.GetString(0).Should().Be("DerivadaAAcceso");
            lector.GetGuid(1).Should().Be(solicitud.Id).And.NotBe(tenant, "el Id que sale es el de la propia Solicitud de alta");
            (await lector.ReadAsync()).Should().BeFalse("una sola fila");
        }

        var fila = await LeerAsync(solicitud.Id);
        fila.Estado.Should().Be("DerivadaAAcceso");
        (await LeerCredencialAsync(solicitud.Id)).Should().Be(((string?)null, (DateTime?)null), "estado final: el credencial deja de servir");
        (await CompletarAsync(credencial, "B12345674")).Resultado.Should().Be(ResultadoDatosOrganizacionAlta.NoValido);
    }

    [Fact]
    public async Task Otra_Solicitud_de_alta_en_curso_con_el_mismo_CIF_no_deriva_a_acceso()
    {
        var (primera, credencialPrimera) = await VerificadaAsync();
        (await CompletarAsync(credencialPrimera, "B12345674")).Resultado.Should().Be(ResultadoDatosOrganizacionAlta.DatosCompletos);
        await EjecutarComoPropietarioAsync($"UPDATE \"SolicitudesAlta\" SET \"Estado\" = 'Pagada' WHERE \"Id\" = '{primera.Id}'");

        var (_, credencial) = await VerificadaAsync();

        (await CompletarAsync(credencial, "B12345674")).Resultado.Should().Be(ResultadoDatosOrganizacionAlta.DatosCompletos,
            "en A4 solo decide el CIF del Tenant propietario: un alta en curso no se revela y su reserva es de A6");
    }

    [Theory]
    [InlineData("ajeno")]
    [InlineData("caducado")]
    [InlineData("solicitud vencida")]
    [InlineData("Bloqueada")]
    [InlineData("Iniciada")]
    public async Task Un_credencial_ajeno_o_caducado_o_fuera_de_CorreoVerificado_no_transiciona(string caso)
    {
        var (solicitud, credencial) = await VerificadaAsync();
        switch (caso)
        {
            case "ajeno":
                credencial = ServicioRegistroAlta.NuevoToken();
                break;
            case "caducado":
                await EjecutarComoPropietarioAsync(
                    $"UPDATE \"SolicitudesAlta\" SET \"CredencialContinuacionCaducaEnUtc\" = now() - interval '1 second' WHERE \"Id\" = '{solicitud.Id}'");
                break;
            case "solicitud vencida":
                await EjecutarComoPropietarioAsync(
                    $"UPDATE \"SolicitudesAlta\" SET \"CaducaEnUtc\" = now() - interval '1 second' WHERE \"Id\" = '{solicitud.Id}'");
                break;
            default:
                await EjecutarComoPropietarioAsync($"UPDATE \"SolicitudesAlta\" SET \"Estado\" = '{caso}' WHERE \"Id\" = '{solicitud.Id}'");
                break;
        }

        (await CompletarAsync(credencial, "B12345674"))
            .Should().Be(new TransicionDatosOrganizacionAlta(ResultadoDatosOrganizacionAlta.NoValido, null));

        var fila = await LeerAsync(solicitud.Id);
        fila.Estado.Should().Be(caso is "Bloqueada" or "Iniciada" ? caso : "CorreoVerificado");
        fila.Version.Should().Be(1, "nada cambia");
    }

    [Fact]
    public async Task Cuatro_envios_simultaneos_con_el_mismo_credencial_transicionan_una_sola_vez()
    {
        const int envios = 4;
        var (solicitud, credencial) = await VerificadaAsync();

        await using var retenedor = new NpgsqlConnection(_cadenaConexion);
        await retenedor.OpenAsync();
        await using var transaccion = await retenedor.BeginTransactionAsync();
        await using (var bloqueo = new NpgsqlCommand(
            $"SELECT 1 FROM \"SolicitudesAlta\" WHERE \"Id\" = '{solicitud.Id}' FOR UPDATE", retenedor, transaccion))
            await bloqueo.ExecuteScalarAsync();

        var tareas = Enumerable.Range(0, envios).Select(_ => Task.Run(() => CompletarAsync(credencial, "B12345674"))).ToArray();

        await using (var observador = new NpgsqlConnection(_cadenaConexion))
        {
            await observador.OpenAsync();
            await using var esperando = new NpgsqlCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND usename = 'cae_app_runtime' AND wait_event_type = 'Lock'",
                observador);
            var limite = DateTime.UtcNow.AddSeconds(30);
            while ((long)(await esperando.ExecuteScalarAsync())! < envios)
            {
                DateTime.UtcNow.Should().BeBefore(limite, "los cuatro envíos deben quedar esperando el bloqueo de la fila");
                await Task.Delay(25);
            }
        }

        await transaccion.CommitAsync();
        var resultados = await Task.WhenAll(tareas);

        resultados.Count(r => r.Resultado == ResultadoDatosOrganizacionAlta.DatosCompletos).Should().Be(1);
        resultados.Where(r => r.Resultado != ResultadoDatosOrganizacionAlta.DatosCompletos)
            .Should().AllSatisfy(r => r.Resultado.Should().Be(ResultadoDatosOrganizacionAlta.NoValido));
        (await LeerAsync(solicitud.Id)).Version.Should().Be(2, "verificar y completar: dos actualizaciones, ni una más");
    }

    [Fact]
    public async Task Con_contexto_firmado_el_runtime_sigue_sin_leer_los_CIF_propietarios()
    {
        await using var conexion = await AbrirComoRuntimeFirmadoAsync();

        (await RechazoAsync(conexion, "SELECT count(*) FROM \"CifsPropietarioTenant\"")).SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        (await RechazoAsync(conexion, "SELECT count(*) FROM \"SolicitudesAlta\"")).SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task La_funcion_del_CIF_rechaza_datos_que_el_dominio_no_admitiria()
    {
        var (_, credencial) = await VerificadaAsync();
        await using var conexion = await AbrirComoRuntimeFirmadoAsync();
        var hash = SolicitudAlta.CalcularHashToken(credencial);

        (await RechazoAsync(conexion,
                $"SELECT * FROM app_alta_completar_datos_organizacion('{hash}', 'Empresa SL', 'B12345674', 'Calle 1', null, 'De1A10', false, false)"))
            .SqlState.Should().Be("22023", "sin declaración de representación (DA-3)");
        (await RechazoAsync(conexion,
                $"SELECT * FROM app_alta_completar_datos_organizacion('{hash}', 'Ana', '12345678Z', 'Calle 1', null, 'De1A10', true, false)"))
            .SqlState.Should().Be("22023", "persona física sin declaración de actividad profesional (DA-11)");
        (await RechazoAsync(conexion,
                $"SELECT * FROM app_alta_completar_datos_organizacion('{hash}', 'Empresa SL', 'B12345674', 'Calle 1', null, 'De1A9999', true, false)"))
            .SqlState.Should().Be("22023", "tramo fuera de la lista");
    }

    private async Task<(SolicitudAlta Solicitud, string Credencial)> VerificadaAsync()
    {
        var token = ServicioRegistroAlta.NuevoToken();
        var credencial = ServicioRegistroAlta.NuevoToken();
        var solicitud = Nueva(token);
        await using var conexion = await AbrirComoRuntimeFirmadoAsync();
        await using var contexto = ContextoSobre(conexion);
        var acceso = new AccesoSolicitudesAlta(contexto);
        await acceso.IniciarAsync(solicitud, CancellationToken.None);
        (await acceso.VerificarCorreoAsync(token, solicitud.HashContrasena!, credencial, CancellationToken.None))
            .Should().Be(ResultadoVerificacionCorreoAlta.Verificado, "guarda del instrumento");
        return (solicitud, credencial);
    }

    private async Task<TransicionDatosOrganizacionAlta> CompletarAsync(string credencial, string cif)
    {
        var datos = DatosOrganizacionAlta.Crear("Empresa de prueba SL", cif, "Calle Mayor 1, Tortosa", TramoTrabajadoresActivos.De11A40, true, true);
        await using var conexion = await AbrirComoRuntimeFirmadoAsync();
        await using var contexto = ContextoSobre(conexion);
        return await new AccesoSolicitudesAlta(contexto).CompletarDatosOrganizacionAsync(credencial, datos, CancellationToken.None);
    }

    private async Task<Guid> TenantConCifAsync(string cif)
    {
        var tenant = new Tenant("Con CIF " + Guid.NewGuid());
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>().UseNpgsql(_cadenaConexion).Options;
        await using var contexto = new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), new TenantActualAmbiental { TenantId = Guid.NewGuid() });
        contexto.Tenants.Add(tenant);
        contexto.CifsPropietarioTenant.Add(CifPropietarioTenant.Declarar(tenant.Id, cif));
        await contexto.SaveChangesAsync();
        return tenant.Id;
    }

    private async Task<(string? Hash, DateTime? Caduca)> LeerCredencialAsync(Guid id)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var orden = new NpgsqlCommand(
            "SELECT \"HashCredencialContinuacion\", \"CredencialContinuacionCaducaEnUtc\" FROM \"SolicitudesAlta\" WHERE \"Id\" = @id", conexion);
        orden.Parameters.AddWithValue("id", id);
        await using var lector = await orden.ExecuteReaderAsync();
        (await lector.ReadAsync()).Should().BeTrue("la Solicitud de alta existe");
        return (lector.IsDBNull(0) ? null : lector.GetString(0), lector.IsDBNull(1) ? null : lector.GetDateTime(1));
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
