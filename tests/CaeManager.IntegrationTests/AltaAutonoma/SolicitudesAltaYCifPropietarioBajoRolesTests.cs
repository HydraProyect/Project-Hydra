using CaeManager.Domain.AltaAutonoma;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.AltaAutonoma;

/// <summary>
/// Lo que la base aplica del incremento A1 del alta autónoma:
/// <list type="bullet">
/// <item>DM-5 en la base: el índice único parcial de <c>Tenants.CifPropietario</c> rechaza un segundo Tenant
/// propietario con el mismo CIF y admite cualquier número de Tenants sin CIF.</item>
/// <item><c>SolicitudesAlta</c> y <c>AceptacionesSolicitudAlta</c> están cerradas: RLS con FORCE, sin
/// políticas y sin ningún privilegio para los roles de la aplicación. Solo el rol propietario (el de esta
/// conexión, el de las migraciones) las lee y escribe.</item>
/// </list>
/// </summary>
public class SolicitudesAltaYCifPropietarioBajoRolesTests : IAsyncLifetime
{
    private static readonly DateTime Inicio = new(2026, 11, 4, 10, 0, 0, DateTimeKind.Utc);
    private static readonly string[] RolesDeLaAplicacion = ["cae_app_runtime", "cae_app_soporte", "cae_app_aprovisionamiento"];
    private static readonly string[] Privilegios = ["SELECT", "INSERT", "UPDATE", "DELETE"];

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();

    public Task InitializeAsync() => BaseDatosPostgresDePruebas.MigrarAsync(_cadenaConexion);

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Un_segundo_Tenant_propietario_con_el_mismo_CIF_lo_rechaza_la_base()
    {
        await using (var contexto = CrearContexto())
        {
            var primero = new Tenant("Primero " + Guid.NewGuid());
            primero.DeclararTipoYCifPropietario(TipoTenant.GestionPropia, "B12345674");
            contexto.Tenants.Add(primero);
            await contexto.SaveChangesAsync();
        }

        await using (var contexto = CrearContexto())
        {
            var segundo = new Tenant("Segundo " + Guid.NewGuid());
            segundo.DeclararTipoYCifPropietario(TipoTenant.GestionExterna, "b-1234567-4");
            contexto.Tenants.Add(segundo);

            var guardar = () => contexto.SaveChangesAsync();

            (await guardar.Should().ThrowAsync<DbUpdateException>())
                .Which.InnerException.Should().BeOfType<PostgresException>()
                .Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
        }
    }

    [Fact]
    public async Task Los_Tenants_sin_CIF_propietario_no_compiten_por_la_unicidad()
    {
        await using var contexto = CrearContexto();
        contexto.Tenants.Add(new Tenant("Sin CIF A " + Guid.NewGuid()));
        contexto.Tenants.Add(new Tenant("Sin CIF B " + Guid.NewGuid()));

        await contexto.SaveChangesAsync();

        (await contexto.Tenants.CountAsync(t => t.CifPropietario == null)).Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task Los_Tenants_existentes_quedan_sin_tipo_ni_CIF()
    {
        await using var contexto = CrearContexto();

        (await contexto.Tenants.AnyAsync(t => t.TipoTenant != null || t.CifPropietario != null)).Should().BeFalse(
            "la migración no rellena las filas existentes");
        (await contexto.Tenants.AnyAsync()).Should().BeTrue("guarda del instrumento: la migración siembra el Tenant de plataforma");
    }

    [Theory]
    [InlineData("SolicitudesAlta")]
    [InlineData("AceptacionesSolicitudAlta")]
    public async Task Las_tablas_tienen_RLS_forzada_sin_politicas(string tabla)
    {
        await using var conexion = await AbrirAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = """
            SELECT c.relrowsecurity AND c.relforcerowsecurity,
                   (SELECT count(*) FROM pg_policies p WHERE p.schemaname = 'public' AND p.tablename = c.relname)
            FROM pg_class c
            WHERE c.oid = format('public.%I', @tabla::text)::regclass;
            """;
        comando.Parameters.AddWithValue("tabla", tabla);
        await using var lector = await comando.ExecuteReaderAsync();
        (await lector.ReadAsync()).Should().BeTrue();

        lector.GetBoolean(0).Should().BeTrue("ENABLE y FORCE ROW LEVEL SECURITY");
        lector.GetInt64(1).Should().Be(0, "ninguna política: nadie sometido a RLS ve ni escribe una fila");
    }

    [Theory]
    [InlineData("SolicitudesAlta")]
    [InlineData("AceptacionesSolicitudAlta")]
    public async Task Ningun_rol_de_la_aplicacion_tiene_privilegio_sobre_las_tablas(string tabla)
    {
        await using var conexion = await AbrirAsync();
        var concedidos = new List<string>();

        foreach (var rol in RolesDeLaAplicacion)
        {
            foreach (var privilegio in Privilegios)
            {
                await using var comando = conexion.CreateCommand();
                comando.CommandText = "SELECT has_table_privilege(@rol, format('public.%I', @tabla::text), @privilegio);";
                comando.Parameters.AddWithValue("rol", rol);
                comando.Parameters.AddWithValue("tabla", tabla);
                comando.Parameters.AddWithValue("privilegio", privilegio);
                if ((bool)(await comando.ExecuteScalarAsync())!)
                    concedidos.Add($"{rol} {privilegio}");
            }

            await using var columnas = conexion.CreateCommand();
            columnas.CommandText = """
                SELECT count(*) FROM information_schema.columns
                WHERE table_schema = 'public' AND table_name = @tabla
                  AND has_column_privilege(@rol, format('public.%I', @tabla::text), column_name, 'SELECT');
                """;
            columnas.Parameters.AddWithValue("rol", rol);
            columnas.Parameters.AddWithValue("tabla", tabla);
            if ((long)(await columnas.ExecuteScalarAsync())! > 0)
                concedidos.Add($"{rol} SELECT de columnas");
        }

        concedidos.Should().BeEmpty();
    }

    [Fact]
    public async Task El_runtime_no_lee_ni_escribe_las_solicitudes_de_alta()
    {
        await using var conexion = await AbrirAsync();
        await using var transaccion = await conexion.BeginTransactionAsync();
        await using (var rol = conexion.CreateCommand())
        {
            rol.Transaction = transaccion;
            rol.CommandText = "SET LOCAL ROLE cae_app_runtime;";
            await rol.ExecuteNonQueryAsync();
        }

        await using var leer = conexion.CreateCommand();
        leer.Transaction = transaccion;
        leer.CommandText = "SELECT count(*) FROM public.\"SolicitudesAlta\";";
        var lectura = () => leer.ExecuteScalarAsync();

        (await lectura.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    /// <summary>
    /// El camino real del registro: la Solicitud de alta ya está guardada en DatosCompletos, se carga,
    /// pasa a pago con sus aceptaciones y se guarda. Las aceptaciones nuevas tienen que entrar como INSERT
    /// (Id asignado por el dominio, ValueGeneratedNever), no como UPDATE de una fila inexistente.
    /// </summary>
    [Fact]
    public async Task Pasar_a_pago_una_solicitud_cargada_inserta_sus_aceptaciones()
    {
        var solicitud = SolicitudAlta.Iniciar("luis@ejemplo.es", "hash", new string('u', 43), "203.0.113.8", "Navegador", Inicio);
        solicitud.VerificarCorreo(new string('u', 43), Inicio.AddHours(1));
        solicitud.CompletarDatos("Org", "B12345674", "Calle 1", TramoTrabajadoresActivos.De11A40, true, false, Inicio.AddHours(1), "+34 977 000 000");

        await using (var contexto = CrearContexto())
        {
            contexto.SolicitudesAlta.Add(solicitud);
            await contexto.SaveChangesAsync();
        }

        await using (var contexto = CrearContexto())
        {
            var cargada = await contexto.SolicitudesAlta.Include(s => s.Aceptaciones).SingleAsync(s => s.Id == solicitud.Id);
            cargada.PasarAPendientePago(ModalidadSuscripcionAlta.Anual,
                [new AceptacionDeclarada("Condiciones generales", "v1", new string('b', 64), true, "203.0.113.8", Inicio.AddHours(2))],
                "cs_test_2", Inicio.AddHours(2));
            await contexto.SaveChangesAsync();
        }

        await using (var contexto = CrearContexto())
        {
            var leida = await contexto.SolicitudesAlta.Include(s => s.Aceptaciones).SingleAsync(s => s.Id == solicitud.Id);
            leida.Estado.Should().Be(EstadoSolicitudAlta.PendientePago);
            leida.Telefono.Should().Be("+34 977 000 000");
            leida.Aceptaciones.Should().ContainSingle().Which.DeclaraFacultades.Should().BeTrue();
        }
    }

    /// <summary>
    /// El trabajo de limpieza puede cargar la Solicitud de alta sin <c>Include</c>: AutoInclude trae las
    /// aceptaciones igualmente, y el borrado elimina sus filas en la base.
    /// </summary>
    [Fact]
    public async Task Borrar_datos_sin_Include_explicito_borra_tambien_las_filas_de_aceptaciones()
    {
        var solicitud = await GuardarPendientePagoAsync("marta@ejemplo.es", "cs_test_3");

        await using (var contexto = CrearContexto())
        {
            var cargada = await contexto.SolicitudesAlta.SingleAsync(s => s.Id == solicitud.Id);
            cargada.Aceptaciones.Should().ContainSingle("guarda del instrumento: AutoInclude las carga sin pedirlo");
            cargada.Caducar(cargada.CaducaEnUtc);
            cargada.BorrarDatosPersonales(cargada.CaducaEnUtc.AddDays(23));
            await contexto.SaveChangesAsync();
        }

        await using (var contexto = CrearContexto())
        {
            (await contexto.Set<AceptacionCondicionesAlta>().CountAsync(a => a.SolicitudAltaId == solicitud.Id)).Should().Be(0);
        }
    }

    /// <summary>
    /// Carrera entre el webhook de pago y el trabajo de caducidad: los dos cargan la misma Solicitud de
    /// alta en PendientePago. El segundo en guardar choca con <c>VersionFila</c> y no pisa al primero.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task El_pago_y_la_caducidad_no_se_pisan(bool primeroElPago)
    {
        var solicitud = await GuardarPendientePagoAsync("pago" + primeroElPago + "@ejemplo.es", "cs_test_4");

        await using var webhook = CrearContexto();
        await using var limpieza = CrearContexto();
        var paraPagar = await webhook.SolicitudesAlta.SingleAsync(s => s.Id == solicitud.Id);
        var paraCaducar = await limpieza.SolicitudesAlta.SingleAsync(s => s.Id == solicitud.Id);
        paraPagar.MarcarPagada("cs_test_4", "cus_4", "sub_4", paraPagar.CaducaEnUtc.AddMinutes(1));
        paraCaducar.Caducar(paraCaducar.CaducaEnUtc.AddMinutes(1));

        var (primero, segundo) = primeroElPago ? (webhook, limpieza) : (limpieza, webhook);
        await primero.SaveChangesAsync();
        var guardarSegundo = () => segundo.SaveChangesAsync();
        await guardarSegundo.Should().ThrowAsync<DbUpdateConcurrencyException>();

        await using var lectura = CrearContexto();
        (await lectura.SolicitudesAlta.SingleAsync(s => s.Id == solicitud.Id)).Estado.Should().Be(
            primeroElPago ? EstadoSolicitudAlta.Pagada : EstadoSolicitudAlta.Caducada);
    }

    private async Task<SolicitudAlta> GuardarPendientePagoAsync(string correo, string sesion)
    {
        var solicitud = SolicitudAlta.Iniciar(correo, "hash", new string('v', 43), "203.0.113.9", "Navegador", Inicio);
        solicitud.VerificarCorreo(new string('v', 43), Inicio.AddHours(1));
        solicitud.CompletarDatos("Org", "B12345674", "Calle 1", TramoTrabajadoresActivos.De1A10, true, false, Inicio.AddHours(1));
        solicitud.PasarAPendientePago(ModalidadSuscripcionAlta.Mensual,
            [new AceptacionDeclarada("Condiciones generales", "v1", new string('c', 64), true, "203.0.113.9", Inicio.AddHours(1))],
            sesion, Inicio.AddHours(1));

        await using var contexto = CrearContexto();
        contexto.SolicitudesAlta.Add(solicitud);
        await contexto.SaveChangesAsync();
        return solicitud;
    }

    [Fact]
    public async Task El_modelo_guarda_y_borra_una_solicitud_con_sus_aceptaciones()
    {
        var solicitud = SolicitudAlta.Iniciar("ana@ejemplo.es", "hash", new string('t', 43), "203.0.113.7", "Navegador", Inicio);
        solicitud.VerificarCorreo(new string('t', 43), Inicio.AddHours(1));
        solicitud.CompletarDatos("Org", "B12345674", "Calle 1", TramoTrabajadoresActivos.De1A10, true, false, Inicio.AddHours(1));
        solicitud.PasarAPendientePago(ModalidadSuscripcionAlta.Mensual,
            [new AceptacionDeclarada("Condiciones generales", "v1", new string('a', 64), true, "203.0.113.7", Inicio.AddHours(1))],
            "cs_test_1", Inicio.AddHours(1));

        await using (var contexto = CrearContexto())
        {
            contexto.SolicitudesAlta.Add(solicitud);
            await contexto.SaveChangesAsync();
        }

        await using (var contexto = CrearContexto())
        {
            var leida = await contexto.SolicitudesAlta.Include(s => s.Aceptaciones).SingleAsync(s => s.Id == solicitud.Id);
            leida.Estado.Should().Be(EstadoSolicitudAlta.PendientePago);
            leida.CifNif.Should().Be("B12345674");
            leida.Aceptaciones.Should().ContainSingle();

            leida.Caducar(leida.CaducaEnUtc);
            leida.BorrarDatosPersonales(leida.CaducaEnUtc.AddDays(23));
            await contexto.SaveChangesAsync();
        }

        await using (var contexto = CrearContexto())
        {
            var borrada = await contexto.SolicitudesAlta.Include(s => s.Aceptaciones).SingleAsync(s => s.Id == solicitud.Id);
            borrada.Correo.Should().BeNull();
            borrada.Aceptaciones.Should().BeEmpty();
            (await contexto.Set<AceptacionCondicionesAlta>().CountAsync(a => a.SolicitudAltaId == solicitud.Id)).Should().Be(0);
        }
    }

    private async Task<NpgsqlConnection> AbrirAsync()
    {
        var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        return conexion;
    }

    private CaeManagerDbContext CrearContexto()
    {
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), new TenantActualAmbiental { TenantId = Guid.NewGuid() });
    }
}
