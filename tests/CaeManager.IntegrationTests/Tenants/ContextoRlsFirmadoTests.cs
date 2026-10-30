using CaeManager.Application.Common;
using CaeManager.Domain.Plataforma;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.ContextoRls;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Infrastructure.Persistence.Seed;
using CaeManager.Migrations.PostgreSQL;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Tenants;

/// <summary>
/// P6 — contexto de sesión RLS firmado, contra PostgreSQL real y autenticando
/// como <c>cae_app_runtime</c> (no con <c>SET ROLE</c> desde el propietario).
///
/// <para>
/// La base del test se migra hasta la última migración, así que ya está en la
/// fase «contraer»: <c>ContraeContextoRlsFirmado</c> aplicó
/// <see cref="ReescrituraPoliticasContextoRls"/> a todas las políticas y
/// funciones. Aquí se demuestra que en ese estado las cuatro propiedades del
/// encargo se cumplen y que la reescritura cubre todo lo que leía GUC sueltas.
/// Que su <c>Down</c> devuelve el catálogo al estado anterior lo prueba
/// <c>Migraciones/ContraeContextoRlsFirmadoTests</c>, que maneja el migrador y
/// por eso parte de una base sin crear en vez de un clon ya migrado.
/// </para>
///
/// <para>
/// Las cuatro propiedades: (1) una GUC falsificada sin token no devuelve
/// datos; (2) un token válido sí, y solo los de su Tenant; (3) un cruce de
/// Tenant falla, por GUC suelta o manipulando el token; (4) el contexto se
/// limpia al cerrar la transacción o la conexión.
/// </para>
/// </summary>
public class ContextoRlsFirmadoTests : IAsyncLifetime
{
    private readonly string _cadenaPropietario = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly List<Guid> _tenants = [];
    private readonly FirmanteContextoRls _firmante = BaseDatosPostgresDePruebas.FirmanteContextoRls;
    private readonly Guid _actorPlataforma = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using (var contexto = CrearContextoPropietario())
        {
            await contexto.Database.MigrateAsync();

            for (var i = 0; i < 3; i++)
            {
                var tenant = new Tenant($"Tenant de prueba {i}");
                contexto.Tenants.Add(tenant);
                _tenants.Add(tenant.Id);
            }

            // Actor de Plataforma TALVEG con concesión global vigente: la
            // coordenada que un atacante con la credencial runtime querría
            // suplantar en el plano 3 (diseño § 6, propiedad 6).
            contexto.ConcesionesPrivilegio.Add(ConcesionPrivilegio.Global(
                _actorPlataforma, vigenciaDesde: DateTime.UtcNow.AddMinutes(-10), vigenciaHasta: null));

            await contexto.SaveChangesAsync();
            await AsignacionesOperativasBackfillSeeder.SeedAsync(contexto, NullLogger.Instance);
        }
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaPropietario);

    // ── (1) GUC falsificada sin token ─────────────────────────────────────

    [Fact]
    public async Task Una_GUC_falsificada_sin_token_no_devuelve_datos()
    {
        // Una raíz por Tenant: los tres del test más el Tenant de plataforma
        // que siembra la migración LineaBase (HasData, EsPlataforma = true),
        // al que el backfill también le da raíz. Se cuenta contra la tabla y
        // no con un número fijo, para que una raíz de más o de menos se vea.
        (await ContarRaicesComoPropietarioAsync()).Should().Be(await ContarTenantsComoPropietarioAsync(),
            "control negativo: hay exactamente una raíz por Tenant, así que un 0 abajo es RLS y no una tabla vacía");
        (await ContarTenantsComoPropietarioAsync()).Should().Be(_tenants.Count + 1,
            "los tres Tenants del test y el de plataforma sembrado por la migración, ninguno más");

        await using var conexion = await AbrirComoRuntimeAsync();
        await FijarGucAsync(conexion, "app.tenant_id", _tenants[0].ToString());
        await FijarGucAsync(conexion, "app.tenant_origen_id", _tenants[0].ToString());
        await FijarGucAsync(conexion, "app.usuario_id", Guid.NewGuid().ToString());

        (await ContarRaicesAsync(conexion)).Should().Be(0,
            "tras la reescritura las políticas solo leen el contexto validado; las GUC sueltas que cualquier " +
            "sesión puede fijar ya no deciden nada");
    }

    // ── (2) Token válido ──────────────────────────────────────────────────

    [Fact]
    public async Task Un_token_valido_devuelve_solo_las_filas_de_su_Tenant()
    {
        await using var conexion = await AbrirComoRuntimeAsync();
        await FijarContextoAsync(conexion, Contexto(_tenants[0]));

        (await ContarRaicesAsync(conexion)).Should().Be(1);
        (await PropietarioDeLaUnicaRaizAsync(conexion)).Should().Be(_tenants[0],
            "el HMAC que calcula C# lo valida la función de PostgreSQL, y el Tenant que ve es el firmado");
    }

    [Fact]
    public async Task Sin_token_el_contexto_es_nulo_en_silencio()
    {
        await using var conexion = await AbrirComoRuntimeAsync();

        (await ContextoValidadoAsync(conexion)).Should().Be((null, null, null, false),
            "una conexión sin app.contexto (propietario, arranque, herramienta) no es un error: no ve nada");
    }

    // ── (3) Cruce de Tenant ───────────────────────────────────────────────

    [Fact]
    public async Task Con_token_valido_una_GUC_de_otro_Tenant_no_cruza()
    {
        await using var conexion = await AbrirComoRuntimeAsync();
        await FijarContextoAsync(conexion, Contexto(_tenants[0]));
        await FijarGucAsync(conexion, "app.tenant_id", _tenants[1].ToString());

        (await PropietarioDeLaUnicaRaizAsync(conexion)).Should().Be(_tenants[0],
            "la GUC suelta ya no es una coordenada de autoridad: sigue viendo el Tenant firmado");
    }

    [Fact]
    public async Task Manipular_el_Tenant_del_token_falla_con_42501()
    {
        await using var conexion = await AbrirComoRuntimeAsync();
        var token = (await _firmante.FirmarAsync(conexion, Contexto(_tenants[0]), CancellationToken.None)).Token;
        var manipulado = token.Replace(_tenants[0].ToString("D"), _tenants[1].ToString("D"), StringComparison.Ordinal);
        manipulado.Should().NotBe(token);

        await FijarGucAsync(conexion, "app.contexto", manipulado);

        await DebeFallarCon42501Async(() => ContarRaicesAsync(conexion), "firma inválida");
    }

    [Fact]
    public async Task Un_token_firmado_para_otra_conexion_falla_con_42501()
    {
        await using var original = await AbrirComoRuntimeAsync();
        var token = (await _firmante.FirmarAsync(original, Contexto(_tenants[0]), CancellationToken.None)).Token;

        await using var otra = await AbrirComoRuntimeAsync();
        otra.ProcessID.Should().NotBe(original.ProcessID);
        await FijarGucAsync(otra, "app.contexto", token);

        await DebeFallarCon42501Async(() => ContarRaicesAsync(otra), "otra conexión");
    }

    [Fact]
    public async Task Un_token_caducado_falla_con_42501()
    {
        // Reloj del firmante dos horas atrás: con TTL de 60 min el token nace
        // caducado respecto al now() de PostgreSQL.
        var firmanteAtrasado = new FirmanteContextoRls(
            BaseDatosPostgresDePruebas.CadenaDeMantenimientoSinPool(), BaseDatosPostgresDePruebas.ProteccionDePruebas,
            new RelojManual(DateTimeOffset.UtcNow.AddHours(-2)),
            FirmanteContextoRls.TtlPorDefecto, FirmanteContextoRls.RotacionPorDefecto);

        await using var conexion = await AbrirComoRuntimeAsync();
        var token = (await firmanteAtrasado.FirmarAsync(conexion, Contexto(_tenants[0]), CancellationToken.None)).Token;
        await FijarGucAsync(conexion, "app.contexto", token);

        await DebeFallarCon42501Async(() => ContarRaicesAsync(conexion), "caducado");
    }

    [Fact]
    public async Task Basura_en_el_token_falla_con_42501()
    {
        await using var conexion = await AbrirComoRuntimeAsync();
        await FijarGucAsync(conexion, "app.contexto", "v1|lo-que-sea");

        await DebeFallarCon42501Async(() => ContarRaicesAsync(conexion), "formato inválido");
    }

    private const string Uuid = "01a18ea4-e641-7463-a9b4-041eacc008a4";
    private const string Nonce = "0123456789abcdef0123456789abcdef";
    private static readonly string Firma = new('a', 64);

    /// <summary>
    /// La gramática del token campo a campo: cada caso se aparta de un token bien formado en un solo punto, y ninguno
    /// puede llegar a buscar la clave ni convertir un campo a uuid (eso daría otro error, no «formato inválido»).
    /// </summary>
    [Theory]
    [InlineData($"v2|{Uuid}|||{Uuid}|peticion|1|2|{Nonce}")] // versión
    [InlineData($"v1|{Uuid}|||{Uuid}|peticion|1|2")] // faltan campos
    [InlineData($"v1|{Uuid}|||{Uuid}|peticion|1|2|{Nonce}|x")] // sobran campos
    [InlineData($"v1||||{Uuid}|peticion|1|2|{Nonce}")] // la clave es obligatoria
    [InlineData($"v1|01A18EA4-E641-7463-A9B4-041EACC008A4|||{Uuid}|peticion|1|2|{Nonce}")] // uuid en mayúsculas
    [InlineData($"v1|01a18ea4e-641-7463-a9b4-041eacc008a4|||{Uuid}|peticion|1|2|{Nonce}")] // guiones desplazados
    [InlineData($"v1|{Uuid}|no-es-uuid||{Uuid}|peticion|1|2|{Nonce}")] // Tenant
    [InlineData($"v1|{Uuid}||no-es-uuid|{Uuid}|peticion|1|2|{Nonce}")] // Tenant de origen
    [InlineData($"v1|{Uuid}|||no-es-uuid|peticion|1|2|{Nonce}")] // usuario
    [InlineData($"v1|{Uuid}|||{Uuid}||1|2|{Nonce}")] // origen vacío
    [InlineData($"v1|{Uuid}|||{Uuid}|abcdefghijklmnopq|1|2|{Nonce}")] // origen de 17
    [InlineData($"v1|{Uuid}|||{Uuid}|Peticion|1|2|{Nonce}")] // origen fuera de a-z
    [InlineData($"v1|{Uuid}|||{Uuid}|peticion|12345678901|2|{Nonce}")] // pid de 11 dígitos
    [InlineData($"v1|{Uuid}|||{Uuid}|peticion|x|2|{Nonce}")] // pid no numérico
    [InlineData($"v1|{Uuid}|||{Uuid}|peticion|1|1234567890123|{Nonce}")] // caducidad de 13 dígitos
    [InlineData($"v1|{Uuid}|||{Uuid}|peticion|1||{Nonce}")] // caducidad vacía
    [InlineData($"v1|{Uuid}|||{Uuid}|peticion|1|2|abc")] // nonce corto
    [InlineData($"v1|{Uuid}|||{Uuid}|peticion|1|2|0123456789abcdef0123456789abcdeg")] // nonce no hexadecimal
    public async Task Un_campo_fuera_de_la_gramatica_falla_con_formato_invalido(string carga)
    {
        await using var conexion = await AbrirComoRuntimeAsync();
        await FijarGucAsync(conexion, "app.contexto", $"{carga}.{Firma}");

        await DebeFallarCon42501Async(() => ContarRaicesAsync(conexion), "formato inválido");
    }

    [Theory]
    [InlineData("#", 64, "a")] // separador
    [InlineData(".", 63, "g")] // no hexadecimal
    [InlineData(".", 63, "")] // corta
    public async Task Una_firma_fuera_de_la_gramatica_falla_con_formato_invalido(string separador, int largo, string final)
    {
        await using var conexion = await AbrirComoRuntimeAsync();
        var firma = separador + Firma[..largo] + final;
        await FijarGucAsync(conexion, "app.contexto", $"v1|{Uuid}|||{Uuid}|peticion|1|2|{Nonce}{firma}");

        await DebeFallarCon42501Async(() => ContarRaicesAsync(conexion), "formato inválido");
    }

    /// <summary>
    /// Bien formado con Tenant, Tenant de origen y usuario vacíos: pasa de la gramática y cae después, en la clave.
    /// Es la forma del contexto de una conexión sin Tenant ni usuario, que no puede rechazarse por formato.
    /// </summary>
    [Fact]
    public async Task Los_campos_opcionales_vacios_son_formato_valido()
    {
        await using var conexion = await AbrirComoRuntimeAsync();
        await FijarGucAsync(conexion, "app.contexto", $"v1|{Guid.NewGuid():D}||||ambito|1|2|{Nonce}.{Firma}");

        await DebeFallarCon42501Async(() => ContarRaicesAsync(conexion), "clave desconocida");
    }

    /// <summary>
    /// La validación corre una vez por tabla de cada consulta: una expresión regular con repeticiones acotadas
    /// costaba más de 1 ms por llamada y se comía el 60 % de Mi trabajo con una cartera de 10 Tenant (M-13).
    /// </summary>
    [Fact]
    public async Task La_validacion_del_contexto_no_usa_expresiones_regulares()
    {
        await using var conexion = await AbrirComoRuntimeAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = "SELECT prosrc FROM pg_proc WHERE proname = 'app_contexto_validado'";
        var cuerpo = (string?)await comando.ExecuteScalarAsync();

        cuerpo.Should().NotBeNull().And.NotContain("regexp_").And.NotContain(" ~ ");
    }

    // ── (4) Limpieza ──────────────────────────────────────────────────────

    [Fact]
    public async Task Un_contexto_local_desaparece_al_cerrar_la_transaccion()
    {
        await using var conexion = await AbrirComoRuntimeAsync();
        var token = (await _firmante.FirmarAsync(conexion, Contexto(_tenants[0]), CancellationToken.None)).Token;

        await using (var transaccion = await conexion.BeginTransactionAsync())
        {
            await using var fijar = conexion.CreateCommand();
            fijar.Transaction = transaccion;
            fijar.CommandText = "SELECT set_config('app.contexto', @t, true);";
            fijar.Parameters.AddWithValue("t", token);
            await fijar.ExecuteNonQueryAsync();

            (await ContarRaicesAsync(conexion, transaccion)).Should().Be(1, "control positivo dentro de la transacción");
            await transaccion.CommitAsync();
        }

        (await ContarRaicesAsync(conexion)).Should().Be(0, "set_config local muere con la transacción");
    }

    /// <summary>Cuerpo de <see cref="ContextoRlsFirmadoPoolTests"/>, que lo ejecuta aislado.</summary>
    internal async Task UnaConexionDevueltaAlPoolNoConservaElContextoAsync()
    {
        // Pool propio (Application Name distinto = cadena distinta = pool
        // distinto) con un solo hueco: la segunda apertura tiene que ser el
        // mismo backend físico, no otro que estuviera ocioso.
        var cadenaRuntimeConPool = new NpgsqlConnectionStringBuilder(
            BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaPropietario))
        {
            ApplicationName = "p6-pool-reutilizado",
            MaxPoolSize = 1,
        }.ConnectionString;
        int pid;

        await using (var conexion = new NpgsqlConnection(cadenaRuntimeConPool))
        {
            await conexion.OpenAsync();
            pid = conexion.ProcessID;
            await FijarContextoAsync(conexion, Contexto(_tenants[0]));
            (await ContarRaicesAsync(conexion)).Should().Be(1, "control positivo antes de devolverla");
        }

        await using var reutilizada = new NpgsqlConnection(cadenaRuntimeConPool);
        await reutilizada.OpenAsync();
        reutilizada.ProcessID.Should().Be(pid,
            "el test solo demuestra algo si es el mismo backend físico sacado del pool");

        (await ContarRaicesAsync(reutilizada)).Should().Be(0,
            "Npgsql hace DISCARD ALL al reutilizar: el token de la petición anterior no llega a la siguiente");
    }

    // ── La clave: fuera del alcance del rol de tráfico ────────────────────

    [Theory]
    [InlineData("SELECT count(*) FROM app_privado.claves_contexto")]
    [InlineData("INSERT INTO app_privado.claves_contexto (id, ipad, opad, clave_protegida, valida_hasta) VALUES (gen_random_uuid(), decode(repeat('00', 64), 'hex'), decode(repeat('00', 64), 'hex'), decode('00', 'hex'), now() + interval '1 hour')")]
    [InlineData("DELETE FROM app_privado.claves_contexto")]
    public async Task El_rol_de_trafico_no_puede_leer_ni_escribir_las_claves(string sql)
    {
        await using var conexion = await AbrirComoRuntimeAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;

        var accion = () => comando.ExecuteNonQueryAsync();
        (await accion.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501");
    }

    /// <summary>
    /// Lo único que el tráfico obtiene de la tabla es la clave cifrada con
    /// DataProtection: ni los rellenos HMAC con los que valida la base ni
    /// ninguna otra columna. Se comprueba contra lo que ve el propietario, y el
    /// control positivo es que ese cifrado, descifrado con el anillo del
    /// proceso, reproduce exactamente los rellenos registrados.
    /// </summary>
    [Fact]
    public async Task El_rol_de_trafico_solo_obtiene_la_clave_cifrada()
    {
        await RegistrarClaveAsync();
        var (idPropietario, ipad, opad) = await ClaveMasRecienteComoPropietarioAsync();

        await using var conexion = await AbrirComoRuntimeAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = "SELECT * FROM app_claves_contexto_protegidas();";
        await using var lector = await comando.ExecuteReaderAsync();

        Enumerable.Range(0, lector.FieldCount).Select(lector.GetName)
            .Should().Equal(["id", "clave_protegida", "valida_hasta"], "la función no expone ninguna otra columna");
        (await lector.ReadAsync()).Should().BeTrue("control: hay una clave vigente registrada");
        lector.GetGuid(0).Should().Be(idPropietario, "la más reciente va primero");
        var devuelta = lector.GetFieldValue<byte[]>(1);

        devuelta.Should().NotEqual(ipad, "el tráfico nunca recibe los rellenos con los que valida la base");
        devuelta.Should().NotEqual(opad, "el tráfico nunca recibe los rellenos con los que valida la base");
        var clave = BaseDatosPostgresDePruebas.ProteccionDePruebas.CreateProtector(ClaveContextoRls.Proposito)
            .Unprotect(devuelta);
        var (ipadDescifrado, opadDescifrado) = TokenContextoRls.Rellenos(clave);
        ipadDescifrado.Should().Equal(ipad, "control positivo: lo devuelto es la clave registrada, cifrada");
        opadDescifrado.Should().Equal(opad);
    }

    /// <summary>
    /// Un proceso con otro anillo de DataProtection (otro entorno, anillo
    /// perdido) no puede usar la clave: no firma, deja <c>app.contexto</c>
    /// vacío y, con las políticas ya reescritas, no ve ninguna fila. Falla
    /// cerrado. El control, con el anillo del proceso, sí ve su Tenant.
    /// </summary>
    [Fact]
    public async Task Con_otro_anillo_de_DataProtection_no_se_firma_y_no_hay_filas()
    {
        await RegistrarClaveAsync();
        var otroAnillo = new FirmanteContextoRls(
            cadenaPropietaria: null, new EphemeralDataProtectionProvider(), TimeProvider.System,
            FirmanteContextoRls.TtlPorDefecto, FirmanteContextoRls.RotacionPorDefecto);

        await using (var control = await AbrirComoRuntimeAsync())
        {
            await FijarContextoAsync(control, Contexto(_tenants[0]));
            (await ContarRaicesAsync(control)).Should().Be(1, "control: con el anillo del proceso se firma y se ve");
        }

        await using var conexion = await AbrirComoRuntimeAsync();
        (await ClaveContextoRls.LeerVigenteAsync(conexion, new EphemeralDataProtectionProvider(), CancellationToken.None))
            .Should().BeNull("ninguna clave registrada se deja descifrar con otro anillo");

        var pendiente = await otroAnillo.FirmarAsync(conexion, Contexto(_tenants[0]), CancellationToken.None);
        pendiente.Token.Should().BeEmpty("sin clave descifrable no se firma");
        await FijarGucAsync(conexion, "app.contexto", pendiente.Token);
        pendiente.Confirmar();

        (await ContextoValidadoAsync(conexion)).Should().Be(((Guid?)null, (Guid?)null, (Guid?)null, false));
        (await ContarRaicesAsync(conexion)).Should().Be(0, "sin contexto firmado, ninguna fila");
        (await FirmanteContextoRls.FirmarConTenantAsync(conexion, _tenants[1], CancellationToken.None))
            .Should().BeNull("la conexión no queda con un contexto en memoria que la base no tiene");
    }

    [Fact]
    public async Task Sin_cadena_propietaria_el_proceso_no_registra_clave()
    {
        // Como el contenedor app de staging y producción (#882): si el
        // migrador no registró clave, el proceso web no puede crear una.
        var sinPropietario = new FirmanteContextoRls(
            cadenaPropietaria: null, BaseDatosPostgresDePruebas.ProteccionDePruebas, TimeProvider.System,
            FirmanteContextoRls.TtlPorDefecto, FirmanteContextoRls.RotacionPorDefecto);
        await EjecutarComoPropietarioAsync("DELETE FROM app_privado.claves_contexto;");

        await using var conexion = await AbrirComoRuntimeAsync();
        (await sinPropietario.FirmarAsync(conexion, Contexto(_tenants[0]), CancellationToken.None))
            .Token.Should().BeEmpty();
        (await ContarClavesComoPropietarioAsync()).Should().Be(0, "el proceso web no registra claves");

        await RegistrarClaveAsync();
        var otraConexion = await AbrirComoRuntimeAsync();
        await using (otraConexion)
        {
            // Pasada la espera tras el fallo, la misma instancia recoge la que
            // registró el migrador; aquí, una instancia nueva basta de control.
            var nueva = new FirmanteContextoRls(
                cadenaPropietaria: null, BaseDatosPostgresDePruebas.ProteccionDePruebas, TimeProvider.System,
                FirmanteContextoRls.TtlPorDefecto, FirmanteContextoRls.RotacionPorDefecto);
            (await nueva.FirmarAsync(otraConexion, Contexto(_tenants[0]), CancellationToken.None))
                .Token.Should().NotBeEmpty("control: con la clave del migrador sí firma");
        }
    }

    /// <summary>
    /// <c>/salud</c> de la clave, con la identidad de tráfico: Healthy con una
    /// clave vigente de 30 días; Degraded si le quedan 7 días o menos o si falla la
    /// lectura; Unhealthy, desde «contraer», si ninguna se deja descifrar.
    /// </summary>
    [Fact]
    public async Task El_health_check_de_la_clave_es_Unhealthy_sin_clave_descifrable_y_Degraded_si_caduca()
    {
        var cadenaRuntime = BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaPropietario);
        ClaveContextoRlsHealthCheck Chequeo(IDataProtectionProvider anillo) => new(
            () => cadenaRuntime, anillo, TimeProvider.System, NullLogger<ClaveContextoRlsHealthCheck>.Instance);
        async Task<HealthStatus> EstadoAsync(IDataProtectionProvider anillo) =>
            (await Chequeo(anillo).CheckHealthAsync(new HealthCheckContext())).Status;

        await RegistrarClaveAsync();
        (await EstadoAsync(BaseDatosPostgresDePruebas.ProteccionDePruebas)).Should().Be(HealthStatus.Healthy,
            "control: clave vigente de 30 días que el proceso sabe descifrar");
        (await EstadoAsync(new EphemeralDataProtectionProvider())).Should().Be(HealthStatus.Unhealthy,
            "con otro anillo no hay clave descifrable y, desde «contraer», RLS no deja ver ninguna fila");

        await EjecutarComoPropietarioAsync("UPDATE app_privado.claves_contexto SET valida_hasta = now() + interval '5 days';");
        (await EstadoAsync(BaseDatosPostgresDePruebas.ProteccionDePruebas)).Should().Be(HealthStatus.Degraded,
            "le quedan 7 días o menos");

        // Un error de lectura (aquí, 42501 al llamar a la función) tampoco
        // puede subir a Unhealthy: HealthCheckService lo haría con una excepción.
        await EjecutarComoPropietarioAsync(SinPermisoSobreLaFuncion);
        (await EstadoAsync(BaseDatosPostgresDePruebas.ProteccionDePruebas)).Should().Be(HealthStatus.Degraded,
            "un error al leer la clave avisa sin cortar el tráfico");
    }

    /// <summary>
    /// Si el rol de tráfico no puede obtener la clave (error de PostgreSQL, no
    /// ausencia), el firmante no firma ni lanza: la conexión se abre sin
    /// contexto firmado y sigue siendo usable.
    /// </summary>
    [Fact]
    public async Task Un_error_al_leer_la_clave_no_firma_ni_corta_la_conexion()
    {
        await RegistrarClaveAsync();
        await EjecutarComoPropietarioAsync(SinPermisoSobreLaFuncion);
        var firmante = new FirmanteContextoRls(
            cadenaPropietaria: null, BaseDatosPostgresDePruebas.ProteccionDePruebas, TimeProvider.System,
            FirmanteContextoRls.TtlPorDefecto, FirmanteContextoRls.RotacionPorDefecto);

        await using var conexion = await AbrirComoRuntimeAsync();
        (await firmante.FirmarAsync(conexion, Contexto(_tenants[0]), CancellationToken.None))
            .Token.Should().BeEmpty();

        await using var comando = conexion.CreateCommand();
        comando.CommandText = "SELECT 1";
        (await comando.ExecuteScalarAsync()).Should().Be(1, "la conexión sigue usable tras el fallo");
    }

    // Local a la base de este test: el ACL de la función vive en ella, no en el clúster.
    private const string SinPermisoSobreLaFuncion =
        "REVOKE EXECUTE ON FUNCTION public.app_claves_contexto_protegidas() FROM cae_app_runtime;";

    // ── La reescritura: cubre todas las políticas que existen hoy ────────

    [Fact]
    public async Task Tras_contraer_ninguna_politica_ni_funcion_lee_las_GUC_sueltas()
    {
        var expresiones = await ExpresionesDePoliticasAsync();

        expresiones.Should().NotBeEmpty("control del instrumento: la consulta tiene que ver las políticas");
        expresiones.Where(e => e.Expresion.Contains("current_setting('app.", StringComparison.Ordinal))
            .Select(e => $"{e.Tabla}.{e.Politica}").Should().BeEmpty();
        expresiones.Count(e => e.Expresion.Contains("app_ctx_", StringComparison.Ordinal))
            .Should().BeGreaterThan(100, "control positivo: las políticas leen el contexto validado");

        var funciones = await DefinicionesDeFuncionesAsync();
        funciones.Where(f => f.Value.Contains("current_setting('app.tenant", StringComparison.Ordinal)
                             || f.Value.Contains("current_setting('app.usuario", StringComparison.Ordinal))
            .Select(f => f.Key).Should().BeEmpty("una función SECURITY DEFINER que leyera la GUC suelta la creería");
        funciones["app_enmascarar_auditoria_de_trabajador_anonimizado(uuid)"].Should().Contain(
            "public.app_ctx_tenant_id() " + ReescrituraPoliticasContextoRls.MarcaFuncion);
    }

    [Fact]
    public async Task El_enmascarado_de_auditoria_no_cree_una_GUC_de_Tenant_falsificada()
    {
        await using var conexion = await AbrirComoRuntimeAsync();
        await FijarGucAsync(conexion, "app.tenant_id", _tenants[0].ToString());

        var falsificada = () => EnmascararAsync(conexion);
        (await falsificada.Should().ThrowAsync<PostgresException>()).Which.MessageText
            .Should().Contain("Sin Tenant en la sesión", "sin token, la función no ve ningún Tenant");

        await FijarContextoAsync(conexion, Contexto(_tenants[0]));
        (await EnmascararAsync(conexion)).Should().Be(0,
            "control positivo: con el contexto firmado la función sí ve el Tenant (y no hay Trabajador que enmascarar)");
    }

    /// <summary>
    /// Requisito P2 de la revisión de #887: si la base deja de tener la clave con
    /// que firma el proceso, el primer comando falla con 42501 «clave
    /// desconocida» y el siguiente ya firma con la clave nueva, en vez de seguir
    /// fallando hasta la siguiente relectura.
    /// </summary>
    [Fact]
    public async Task Tras_retirar_la_clave_el_siguiente_comando_firma_con_la_nueva()
    {
        var firmante = new FirmanteContextoRls(
            BaseDatosPostgresDePruebas.CadenaDeMantenimientoSinPool(), BaseDatosPostgresDePruebas.ProteccionDePruebas,
            TimeProvider.System, FirmanteContextoRls.TtlPorDefecto, FirmanteContextoRls.RotacionPorDefecto);
        await using var contexto = CrearContextoRuntime(_tenants[0], firmante);
        await contexto.Database.OpenConnectionAsync();
        try
        {
            (await contexto.AsignacionesOperacion.CountAsync(a => a.EsRaiz)).Should().Be(1, "control positivo");

            await EjecutarComoPropietarioAsync("DELETE FROM app_privado.claves_contexto;");
            await RegistrarClaveAsync();

            var conClaveRetirada = () => contexto.AsignacionesOperacion.CountAsync(a => a.EsRaiz);
            await DebeFallarCon42501Async(conClaveRetirada, "clave desconocida");

            (await contexto.AsignacionesOperacion.CountAsync(a => a.EsRaiz)).Should().Be(1,
                "el fallo descartó la clave en memoria y el token se volvió a firmar con la vigente");
        }
        finally
        {
            await contexto.Database.CloseConnectionAsync();
        }
    }

    private static async Task<int> EnmascararAsync(NpgsqlConnection conexion)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = "SELECT public.app_enmascarar_auditoria_de_trabajador_anonimizado(@id);";
        comando.Parameters.AddWithValue("id", Guid.NewGuid());
        return Convert.ToInt32(await comando.ExecuteScalarAsync());
    }

    private async Task<Dictionary<string, string>> DefinicionesDeFuncionesAsync()
    {
        await using var conexion = new NpgsqlConnection(_cadenaPropietario);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = @"
SELECT p.oid::regprocedure::text, pg_get_functiondef(p.oid)
  FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
 WHERE n.nspname = 'public' AND p.prokind IN ('f', 'p');";
        var resultado = new Dictionary<string, string>();
        await using var lector = await comando.ExecuteReaderAsync();
        while (await lector.ReadAsync())
            resultado[lector.GetString(0)] = lector.GetString(1);
        return resultado;
    }

    [Theory]
    [InlineData("ConcesionesPrivilegio")]
    [InlineData("EstadoBootstrapPlataforma")]
    [InlineData("OrdenMenuLateral")]
    public async Task Las_tablas_del_plano_de_plataforma_quedan_en_el_contexto_validado(string tabla)
    {
        var expresiones = (await ExpresionesDePoliticasAsync()).Where(e => e.Tabla == tabla).ToList();

        expresiones.Should().NotBeEmpty($"{tabla} tiene políticas");
        expresiones.Where(e => e.Expresion.Contains("app_ctx_", StringComparison.Ordinal)).Should().NotBeEmpty(
            $"las políticas de {tabla} que leían el usuario tienen que leerlo validado");
    }

    [Fact]
    public async Task La_excepcion_de_bootstrap_exige_contexto_firmado()
    {
        var expresiones = (await ExpresionesDePoliticasAsync())
            .Where(e => e.Tabla == "EstadoBootstrapPlataforma").Select(e => e.Expresion);

        expresiones.Should().Contain(e => e.Contains("app_ctx_valido()", StringComparison.Ordinal),
            "«nadie autenticado» pasa a «contexto firmado válido y sin usuario»: sin token no hay excepción");
    }

    [Fact]
    public async Task Con_token_la_escritura_en_otro_Tenant_la_rechaza_el_WITH_CHECK()
    {
        await using var conexion = await AbrirComoRuntimeAsync();
        await FijarContextoAsync(conexion, Contexto(_tenants[0]));

        await using var propio = conexion.CreateCommand();
        propio.CommandText = @"UPDATE ""AsignacionesOperacion"" SET ""PropietarioTenantId"" = ""PropietarioTenantId"" WHERE ""EsRaiz"";";
        (await propio.ExecuteNonQueryAsync()).Should().Be(1, "control positivo: su propia fila sí la actualiza");

        await using var ajeno = conexion.CreateCommand();
        ajeno.CommandText = @"UPDATE ""AsignacionesOperacion"" SET ""PropietarioTenantId"" = @otro WHERE ""EsRaiz"";";
        ajeno.Parameters.AddWithValue("otro", _tenants[1]);
        var accion = () => ajeno.ExecuteNonQueryAsync();
        (await accion.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501",
            "mover la fila a otro Tenant lo impide el WITH CHECK reescrito");
    }

    // ── Plano 3: suplantar a un Actor de Plataforma TALVEG ───────────────

    [Fact]
    public async Task Una_GUC_con_el_id_de_un_Actor_de_Plataforma_no_da_sus_privilegios()
    {
        await using (var propietario = new NpgsqlConnection(_cadenaPropietario))
        {
            await propietario.OpenAsync();
            (await ContarConcesionesAsync(propietario)).Should().Be(1,
                "control negativo: la concesión existe, así que un 0 abajo es RLS y no una tabla vacía");
        }

        await using var conexion = await AbrirComoRuntimeAsync();
        await FijarGucAsync(conexion, "app.usuario_id", _actorPlataforma.ToString());

        (await ContarConcesionesAsync(conexion)).Should().Be(0,
            "la GUC suelta con el id del Actor de Plataforma TALVEG no le presta sus concesiones");
        var insertar = () => InsertarOrdenMenuAsync(conexion, _actorPlataforma);
        (await insertar.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501",
            "el WITH CHECK de OrdenMenuLateral no ve un administrador de plataforma sin contexto firmado");
    }

    [Fact]
    public async Task Un_token_firmado_del_Actor_de_Plataforma_si_da_sus_privilegios()
    {
        await using var conexion = await AbrirComoRuntimeAsync();
        await FijarContextoAsync(conexion, new ContextoSesionRls(null, null, _actorPlataforma, OrigenContextoRls.Peticion));

        (await ContarConcesionesAsync(conexion)).Should().Be(1,
            "control positivo: la misma coordenada, firmada, sí es el Actor de Plataforma TALVEG");
        (await InsertarOrdenMenuAsync(conexion, _actorPlataforma)).Should().Be(1);
    }

    private static async Task<int> ContarConcesionesAsync(NpgsqlConnection conexion)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = @"SELECT count(*) FROM ""ConcesionesPrivilegio"";";
        return Convert.ToInt32(await comando.ExecuteScalarAsync());
    }

    private static async Task<int> InsertarOrdenMenuAsync(NpgsqlConnection conexion, Guid actor)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = @"
INSERT INTO ""OrdenMenuLateral"" (""Id"", ""OrdenGrupos"", ""OrdenEnlaces"", ""ActualizadoPorUsuarioId"", ""ActualizadoEnUtc"", ""Version"")
VALUES (@id, ARRAY['control'], ARRAY[]::text[], @actor, now(), gen_random_uuid());";
        comando.Parameters.AddWithValue("id", OrdenMenuLateral.ClaveCanonica);
        comando.Parameters.AddWithValue("actor", actor);
        return await comando.ExecuteNonQueryAsync();
    }

    // ── El interceptor: firma de verdad, y renueva ────────────────────────

    [Fact]
    public async Task El_interceptor_envia_un_token_que_la_base_acepta()
    {
        await using var contexto = CrearContextoRuntime(_tenants[1], _firmante);

        var propietarios = await contexto.AsignacionesOperacion
            .Where(a => a.EsRaiz).Select(a => a.PropietarioTenantId).ToListAsync();

        propietarios.Should().Equal([_tenants[1]],
            "sin token la reescritura no dejaría ver nada; con uno ajeno, vería otra raíz");
    }

    /// <summary>
    /// Reloj del firmante 100 min atrás con TTL de 60: el token de apertura
    /// nace caducado para PostgreSQL. Adelantar el reloj más de TTL/2 obliga
    /// a renovar; <paramref name="enTransaccion"/> elige dónde: antes de un
    /// comando en autocommit, o antes del BEGIN (dentro de la transacción no
    /// se renueva, así que si el BEGIN no renovara, el comando fallaría).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task El_interceptor_renueva_el_token_antes_de_que_la_base_lo_de_por_caducado(bool enTransaccion)
    {
        var reloj = new RelojManual(DateTimeOffset.UtcNow.AddMinutes(-100));
        var firmante = new FirmanteContextoRls(
            BaseDatosPostgresDePruebas.CadenaDeMantenimientoSinPool(), BaseDatosPostgresDePruebas.ProteccionDePruebas, reloj,
            FirmanteContextoRls.TtlPorDefecto, FirmanteContextoRls.RotacionPorDefecto);

        await using var contexto = CrearContextoRuntime(_tenants[2], firmante);
        await contexto.Database.OpenConnectionAsync();
        try
        {
            var antes = () => contexto.AsignacionesOperacion.CountAsync(a => a.EsRaiz);
            (await antes.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501",
                "control del instrumento: sin renovar, el token de apertura está caducado");

            reloj.Adelantar(TimeSpan.FromMinutes(100));

            if (enTransaccion)
            {
                await using var transaccion = await contexto.Database.BeginTransactionAsync();
                (await contexto.AsignacionesOperacion.CountAsync(a => a.EsRaiz)).Should().Be(1,
                    "el interceptor renovó el token antes del BEGIN, que es cuando la base fija su now()");
            }
            else
            {
                (await contexto.AsignacionesOperacion.CountAsync(a => a.EsRaiz)).Should().Be(1,
                    "el interceptor renovó el token antes del comando");
            }
        }
        finally
        {
            await contexto.Database.CloseConnectionAsync();
        }
    }

    [Fact]
    public async Task Un_token_firmado_sin_confirmar_no_deja_contexto_en_memoria()
    {
        // Hallazgo P2 de Codex (ronda 2): si el set_config inicial falla o se
        // cancela, la memoria no puede describir un contexto que la base no
        // tiene. Sin Confirmar, sellar no encuentra contexto que tocar.
        await using var conexion = await AbrirComoRuntimeAsync();
        var pendiente = await _firmante.FirmarAsync(conexion, Contexto(_tenants[0]), CancellationToken.None);

        (await FirmanteContextoRls.FirmarConTenantAsync(conexion, _tenants[1], CancellationToken.None))
            .Should().BeNull("el token no llegó a la base, así que la conexión no tiene contexto firmado");

        pendiente.Confirmar();

        (await FirmanteContextoRls.FirmarConTenantAsync(conexion, _tenants[1], CancellationToken.None))
            .Should().NotBeNull("tras confirmar, la conexión sí tiene contexto de base");
    }

    // ── Andamiaje ─────────────────────────────────────────────────────────

    private Task RegistrarClaveAsync() =>
        ClaveContextoRls.RegistrarAsync(
            _cadenaPropietario, BaseDatosPostgresDePruebas.ProteccionDePruebas,
            ClaveContextoRls.VigenciaPorDefecto, CancellationToken.None);

    private async Task<(Guid Id, byte[] Ipad, byte[] Opad)> ClaveMasRecienteComoPropietarioAsync()
    {
        await using var conexion = new NpgsqlConnection(_cadenaPropietario);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText =
            "SELECT id, ipad, opad FROM app_privado.claves_contexto ORDER BY registrada DESC, id LIMIT 1;";
        await using var lector = await comando.ExecuteReaderAsync();
        await lector.ReadAsync();
        return (lector.GetGuid(0), lector.GetFieldValue<byte[]>(1), lector.GetFieldValue<byte[]>(2));
    }

    private async Task<int> ContarClavesComoPropietarioAsync()
    {
        await using var conexion = new NpgsqlConnection(_cadenaPropietario);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = "SELECT count(*) FROM app_privado.claves_contexto;";
        return Convert.ToInt32(await comando.ExecuteScalarAsync());
    }

    private static ContextoSesionRls Contexto(Guid tenant) =>
        new(tenant, tenant, Guid.NewGuid(), OrigenContextoRls.Peticion);

    private async Task FijarContextoAsync(NpgsqlConnection conexion, ContextoSesionRls contexto)
    {
        var token = (await _firmante.FirmarAsync(conexion, contexto, CancellationToken.None)).Token;
        await FijarGucAsync(conexion, "app.contexto", token);
    }

    private static async Task FijarGucAsync(NpgsqlConnection conexion, string guc, string valor)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = "SELECT set_config(@guc, @valor, false);";
        comando.Parameters.AddWithValue("guc", guc);
        comando.Parameters.AddWithValue("valor", valor);
        await comando.ExecuteNonQueryAsync();
    }

    private static async Task DebeFallarCon42501Async(Func<Task> accion, string motivo)
    {
        var error = (await accion.Should().ThrowAsync<PostgresException>()).Which;
        error.SqlState.Should().Be("42501");
        error.MessageText.Should().Contain(motivo, "el rechazo tiene que ser por la comprobación esperada, no por otra");
    }

    private static async Task<int> ContarRaicesAsync(NpgsqlConnection conexion, NpgsqlTransaction? transaccion = null)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText = @"SELECT count(*) FROM ""AsignacionesOperacion"" WHERE ""EsRaiz"";";
        return Convert.ToInt32(await comando.ExecuteScalarAsync());
    }

    private static async Task<Guid> PropietarioDeLaUnicaRaizAsync(NpgsqlConnection conexion)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = @"SELECT ""PropietarioTenantId"" FROM ""AsignacionesOperacion"" WHERE ""EsRaiz"";";
        return (Guid)(await comando.ExecuteScalarAsync())!;
    }

    private static async Task<(Guid?, Guid?, Guid?, bool)> ContextoValidadoAsync(NpgsqlConnection conexion)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = "SELECT tenant_id, tenant_origen_id, usuario_id, valido FROM app_contexto_validado();";
        await using var lector = await comando.ExecuteReaderAsync();
        await lector.ReadAsync();
        Guid? Leer(int i) => lector.IsDBNull(i) ? null : lector.GetGuid(i);
        return (Leer(0), Leer(1), Leer(2), lector.GetBoolean(3));
    }

    private async Task<int> ContarRaicesComoPropietarioAsync()
    {
        await using var conexion = new NpgsqlConnection(_cadenaPropietario);
        await conexion.OpenAsync();
        return await ContarRaicesAsync(conexion);
    }

    private async Task<int> ContarTenantsComoPropietarioAsync()
    {
        await using var conexion = new NpgsqlConnection(_cadenaPropietario);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = @"SELECT count(*) FROM ""Tenants"";";
        return Convert.ToInt32(await comando.ExecuteScalarAsync());
    }

    private sealed record ExpresionPolitica(string Tabla, string Politica, string Expresion);

    private async Task<List<ExpresionPolitica>> ExpresionesDePoliticasAsync()
    {
        await using var conexion = new NpgsqlConnection(_cadenaPropietario);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = @"
SELECT cl.relname, pol.polname,
       coalesce(pg_get_expr(pol.polqual, pol.polrelid), '') || ' ' || coalesce(pg_get_expr(pol.polwithcheck, pol.polrelid), '')
  FROM pg_policy pol JOIN pg_class cl ON cl.oid = pol.polrelid;";
        var resultado = new List<ExpresionPolitica>();
        await using var lector = await comando.ExecuteReaderAsync();
        while (await lector.ReadAsync())
            resultado.Add(new ExpresionPolitica(lector.GetString(0), lector.GetString(1), lector.GetString(2)));
        return resultado;
    }

    private async Task EjecutarComoPropietarioAsync(string sql)
    {
        await using var conexion = new NpgsqlConnection(_cadenaPropietario);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        await comando.ExecuteNonQueryAsync();
    }

    private async Task<NpgsqlConnection> AbrirComoRuntimeAsync()
    {
        // Sin pool: cada test quiere un backend propio (el pid va en el token).
        var cadena = new NpgsqlConnectionStringBuilder(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaPropietario))
        {
            Pooling = false,
        };
        var conexion = new NpgsqlConnection(cadena.ConnectionString);
        await conexion.OpenAsync();
        return conexion;
    }

    private CaeManagerDbContext CrearContextoPropietario(string? cadena = null)
    {
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(cadena ?? _cadenaPropietario, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .Options;

        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), new TenantActualAmbiental());
    }

    private CaeManagerDbContext CrearContextoRuntime(Guid tenant, FirmanteContextoRls firmante)
    {
        var tenantActual = new TenantFijo(tenant);
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaPropietario))
            .AddInterceptors(new TenantRlsConnectionInterceptor(
                tenantActual, new SinClienteActivo(), new CurrentUserServiceFalso(Guid.NewGuid(), tenantOrigenId: tenant),
                firmante))
            .Options;

        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private sealed class TenantFijo(Guid tenant) : ITenantActual
    {
        public Guid? TenantId => tenant;
    }

    private sealed class SinClienteActivo : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }

    private sealed class RelojManual(DateTimeOffset inicio) : TimeProvider
    {
        private DateTimeOffset _ahora = inicio;
        public void Adelantar(TimeSpan cuanto) => _ahora += cuanto;
        public override DateTimeOffset GetUtcNow() => _ahora;
    }
}

/// <summary>
/// Solo este test necesita que el pool le devuelva el mismo backend físico, y
/// por eso corre aislado de los vaciados de pool de otras clases (ver
/// <see cref="ColeccionPoolNpgsqlSinVaciadosConcurrentes"/>) sin arrastrar al
/// resto de <see cref="ContextoRlsFirmadoTests"/>: reutiliza su base y sus
/// ayudantes por composición. Es una clase de primer nivel y no anidada porque
/// <c>scripts/repartir-clases-de-test.sh</c> no reconoce el <c>+</c> del nombre
/// de una clase anidada y la confundía con su espacio de nombres entero.
/// </summary>
[Collection(ColeccionPoolNpgsqlSinVaciadosConcurrentes.Nombre)]
public sealed class ContextoRlsFirmadoPoolTests : IAsyncLifetime
{
    private readonly ContextoRlsFirmadoTests _clase = new();

    public Task InitializeAsync() => _clase.InitializeAsync();

    public Task DisposeAsync() => _clase.DisposeAsync();

    [Fact]
    public Task Una_conexion_devuelta_al_pool_no_conserva_el_contexto() =>
        _clase.UnaConexionDevueltaAlPoolNoConservaElContextoAsync();
}

/// <summary>
/// Colección que xUnit ejecuta sola, después de las paralelas.
/// <see cref="ContextoRlsFirmadoPoolTests.Una_conexion_devuelta_al_pool_no_conserva_el_contexto"/>
/// necesita que el pool le devuelva el mismo backend físico, y un pool propio
/// no basta: <c>NpgsqlConnection.ClearAllPools()</c> vacía todos los pools del
/// proceso, y lo llama <c>EnsureDeletedAsync</c> de EF (<c>NpgsqlDatabaseCreator</c>)
/// en el teardown de otras clases. En paralelo con ellas el hueco salía con otro
/// backend y la guarda del test fallaba (run 36253365056: 1314 → 1317), sin que
/// la aserción de contexto llegara a evaluarse. Un conector en uso durante el
/// vaciado también se cierra al devolverlo, así que la ventana es todo el test y
/// reintentar solo bajaría la probabilidad; aislarlo la quita. Solo lleva ese
/// test: la clase entera ocupa minutos de reloj en CI y aislarla los sumaría.
/// </summary>
[CollectionDefinition(Nombre, DisableParallelization = true)]
public sealed class ColeccionPoolNpgsqlSinVaciadosConcurrentes
{
    public const string Nombre = "Pool de Npgsql sin vaciados concurrentes";
}
