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
/// <item>DM-5 en la base: el índice único de <c>CifsPropietarioTenant.Cif</c> rechaza un segundo Tenant
/// propietario con el mismo CIF. El CIF no está en <c>Tenants</c>, que leen los tres roles de la aplicación
/// sin restricción por Tenant (DA-11: el NIF de una persona física es un dato personal).</item>
/// <item><c>SolicitudesAlta</c>, <c>AceptacionesSolicitudAlta</c> y <c>CifsPropietarioTenant</c> están
/// cerradas: RLS con FORCE, sin políticas y sin ningún privilegio para los roles de la aplicación. Cada rol
/// intenta de verdad SELECT, INSERT, UPDATE y DELETE y la base se lo niega. Solo una identidad que evita la
/// RLS las lee y escribe: aquí la conexión de pruebas, que es superusuario (con FORCE, un propietario
/// ordinario sin BYPASSRLS tampoco vería filas).</item>
/// </list>
/// </summary>
public class SolicitudesAltaYCifPropietarioBajoRolesTests : IAsyncLifetime
{
    private static readonly DateTime Inicio = new(2026, 11, 4, 10, 0, 0, DateTimeKind.Utc);
    private static readonly string[] RolesDeLaAplicacion = ["cae_app_runtime", "cae_app_soporte", "cae_app_aprovisionamiento"];
    private static readonly string[] Privilegios = ["SELECT", "INSERT", "UPDATE", "DELETE"];

    /// <summary>Una columna de cada tabla cerrada, para el intento de UPDATE.</summary>
    private static readonly Dictionary<string, string> ColumnaDeCadaTabla = new()
    {
        ["SolicitudesAlta"] = "Estado",
        ["AceptacionesSolicitudAlta"] = "Documento",
        ["CifsPropietarioTenant"] = "Cif",
    };

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();

    public Task InitializeAsync() => BaseDatosPostgresDePruebas.MigrarAsync(_cadenaConexion);

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Un_segundo_Tenant_propietario_con_el_mismo_CIF_lo_rechaza_la_base()
    {
        var primero = new Tenant("Primero " + Guid.NewGuid());
        var segundo = new Tenant("Segundo " + Guid.NewGuid());
        await using (var contexto = CrearContexto())
        {
            contexto.Tenants.AddRange(primero, segundo);
            contexto.CifsPropietarioTenant.Add(CifPropietarioTenant.Declarar(primero.Id, "B12345674"));
            await contexto.SaveChangesAsync();
        }

        await using (var contexto = CrearContexto())
        {
            contexto.CifsPropietarioTenant.Add(CifPropietarioTenant.Declarar(segundo.Id, "b-1234567-4"));

            var guardar = () => contexto.SaveChangesAsync();

            (await guardar.Should().ThrowAsync<DbUpdateException>())
                .Which.InnerException.Should().BeOfType<PostgresException>()
                .Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
        }
    }

    [Fact]
    public async Task Borrar_un_Tenant_borra_su_CIF_propietario()
    {
        var tenant = new Tenant("Con CIF " + Guid.NewGuid());
        await using (var contexto = CrearContexto())
        {
            contexto.Tenants.Add(tenant);
            contexto.CifsPropietarioTenant.Add(CifPropietarioTenant.Declarar(tenant.Id, "12345678Z"));
            await contexto.SaveChangesAsync();
        }

        await using (var contexto = CrearContexto())
        {
            (await contexto.CifsPropietarioTenant.CountAsync(c => c.TenantId == tenant.Id)).Should().Be(1, "guarda del instrumento");
            await contexto.Tenants.Where(t => t.Id == tenant.Id).ExecuteDeleteAsync();
            (await contexto.CifsPropietarioTenant.CountAsync(c => c.TenantId == tenant.Id)).Should().Be(0,
                "el NIF de una persona física no queda huérfano en una tabla que nadie lee");
        }
    }

    [Fact]
    public async Task Los_Tenants_existentes_quedan_sin_tipo_ni_CIF()
    {
        await using var contexto = CrearContexto();

        (await contexto.Tenants.AnyAsync(t => t.TipoTenant != null)).Should().BeFalse("la migración no rellena las filas existentes");
        (await contexto.CifsPropietarioTenant.AnyAsync()).Should().BeFalse("la migración no crea CIF para los Tenants existentes");
        (await contexto.Tenants.AnyAsync()).Should().BeTrue("guarda del instrumento: la migración siembra el Tenant de plataforma");
    }

    [Fact]
    public async Task Tenants_no_tiene_columna_de_CIF()
    {
        await using var conexion = await AbrirAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = """
            SELECT count(*) FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = 'Tenants' AND column_name ILIKE '%cif%';
            """;

        ((long)(await comando.ExecuteScalarAsync())!).Should().Be(0, "Tenants la leen los tres roles de la aplicación (DA-11)");
    }

    [Theory]
    [InlineData("SolicitudesAlta")]
    [InlineData("AceptacionesSolicitudAlta")]
    [InlineData("CifsPropietarioTenant")]
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
    [InlineData("CifsPropietarioTenant")]
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
                  AND has_column_privilege(@rol, format('public.%I', @tabla::text), column_name, 'SELECT,INSERT,UPDATE');
                """;
            columnas.Parameters.AddWithValue("rol", rol);
            columnas.Parameters.AddWithValue("tabla", tabla);
            if ((long)(await columnas.ExecuteScalarAsync())! > 0)
                concedidos.Add($"{rol} privilegio de columnas");
        }

        concedidos.Should().BeEmpty();
    }

    /// <summary>
    /// No solo el catálogo: cada rol de la aplicación intenta de verdad leer, insertar, actualizar y borrar
    /// en cada tabla cerrada, con una fila ya presente, y la base se lo niega. Cada intento va en su propia
    /// transacción, porque el error aborta la que lo contiene.
    /// </summary>
    [Theory]
    [InlineData("cae_app_runtime", "SolicitudesAlta")]
    [InlineData("cae_app_runtime", "AceptacionesSolicitudAlta")]
    [InlineData("cae_app_runtime", "CifsPropietarioTenant")]
    [InlineData("cae_app_soporte", "SolicitudesAlta")]
    [InlineData("cae_app_soporte", "AceptacionesSolicitudAlta")]
    [InlineData("cae_app_soporte", "CifsPropietarioTenant")]
    [InlineData("cae_app_aprovisionamiento", "SolicitudesAlta")]
    [InlineData("cae_app_aprovisionamiento", "AceptacionesSolicitudAlta")]
    [InlineData("cae_app_aprovisionamiento", "CifsPropietarioTenant")]
    public async Task Ningun_rol_de_la_aplicacion_lee_ni_escribe_las_tablas_cerradas(string rol, string tabla)
    {
        await GuardarPendientePagoAsync($"{rol}.{tabla}@ejemplo.es", "cs_test_" + Guid.NewGuid().ToString("N"));
        var tenant = new Tenant("Cerrada " + Guid.NewGuid());
        await using (var contexto = CrearContexto())
        {
            contexto.Tenants.Add(tenant);
            contexto.CifsPropietarioTenant.Add(CifPropietarioTenant.Declarar(tenant.Id, "B12345674"));
            await contexto.SaveChangesAsync();
        }

        var columna = ColumnaDeCadaTabla[tabla];
        var sentencias = new Dictionary<string, string>
        {
            ["SELECT"] = $"SELECT count(*) FROM public.\"{tabla}\";",
            ["INSERT"] = $"INSERT INTO public.\"{tabla}\" DEFAULT VALUES;",
            ["UPDATE"] = $"UPDATE public.\"{tabla}\" SET \"{columna}\" = \"{columna}\";",
            ["DELETE"] = $"DELETE FROM public.\"{tabla}\";",
        };

        var permitidas = new List<string>();
        await using var conexion = await AbrirAsync();
        foreach (var (operacion, sql) in sentencias)
        {
            await using var transaccion = await conexion.BeginTransactionAsync();
            await using (var cambiarRol = conexion.CreateCommand())
            {
                cambiarRol.Transaction = transaccion;
                cambiarRol.CommandText = $"SET LOCAL ROLE {rol};";
                await cambiarRol.ExecuteNonQueryAsync();
            }

            await using var intento = conexion.CreateCommand();
            intento.Transaction = transaccion;
            intento.CommandText = sql;
            try
            {
                await intento.ExecuteNonQueryAsync();
                permitidas.Add(operacion);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege)
            {
                // Denegada: lo esperado.
            }

            await transaccion.RollbackAsync();
        }

        permitidas.Should().BeEmpty($"{rol} no tiene ninguna vía a {tabla} hasta que el alta abra la suya");

        await using var propietario = CrearContexto();
        (await propietario.CifsPropietarioTenant.CountAsync(c => c.TenantId == tenant.Id)).Should().Be(1,
            "guarda del instrumento: la fila seguía ahí y la conexión de pruebas, que evita la RLS, sí la ve");
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
