using System.Security.Claims;
using CaeManager.Application.Common;
using CaeManager.Domain.Seguridad;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Infrastructure.Seguridad;
using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Plataforma;

/// <summary>
/// La frontera de autenticación de <c>cae_app_soporte</c> (OWASP ASVS 4.0.3 V4.1.3), en la base migrada y con la
/// identidad efectiva real: la revalidación de un circuito en Sesión Privilegiada pregunta a
/// <c>app_cuenta_firmada_vigente</c>, que solo devuelve un booleano sobre la cuenta que firma el contexto, y lee
/// el estado de la cuenta por proyección.
///
/// <para>
/// El principal pasa por el <see cref="CurrentUserService"/> real y por <see cref="TenantRlsConnectionInterceptor"/>,
/// que son quienes firman el contexto: así se mide qué cuenta llega a la función, no qué cuenta dice un doble.
/// </para>
///
/// <para>
/// Aquí <c>SignInManager</c> es <c>null</c> a propósito: si el camino de la Sesión Privilegiada lo tocara, cargaría
/// la cuenta entera y el test fallaría. El camino sin selección, que sí lo usa, lo recorre
/// <c>SesionDeCuentaDesactivadaTests</c>.
/// </para>
/// </summary>
public class FronteraCuentaFirmadaTests : IAsyncLifetime
{
    private const string Funcion = "public.app_cuenta_firmada_vigente(text)";
    private const string FuncionLoginExterno = "public.app_cuenta_tiene_login_externo(uuid)";
    private const string SelloActorReal = "SELLO-ACTOR-REAL";
    private const string SelloUsuarioSimulado = "SELLO-USUARIO-SIMULADO";

    private readonly string _cadena = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _actorReal = Guid.NewGuid();
    private readonly Guid _usuarioSimulado = Guid.NewGuid();
    private readonly Guid _cuentaDeOtroTenant = Guid.NewGuid();
    private readonly Guid _sid = Guid.NewGuid();
    private Guid _tenantOrigen;
    private Guid _tenantObjetivo;
    private Guid _sesion;
    private Seleccion _seleccion = null!;

    public async Task InitializeAsync()
    {
        await BaseDatosPostgresDePruebas.MigrarAsync(_cadena);

        var origen = new Tenant("Tenant de origen del Actor real");
        var objetivo = new Tenant("Tenant objetivo");
        var otro = new Tenant("Otro Tenant");
        _tenantOrigen = origen.Id;
        _tenantObjetivo = objetivo.Id;
        _seleccion = new Seleccion(_tenantObjetivo, Guid.NewGuid());

        await using var contexto = ContextoPropietario();
        contexto.Tenants.AddRange(origen, objetivo, otro);
        await contexto.SaveChangesAsync();

        Sembrar(contexto, _actorReal, "actor-real", _tenantOrigen, SelloActorReal, debeCambiarContrasena: true, dobleFactor: true);
        Sembrar(contexto, _usuarioSimulado, "usuario-simulado", _tenantObjetivo, SelloUsuarioSimulado, debeCambiarContrasena: false, dobleFactor: false);
        Sembrar(contexto, _cuentaDeOtroTenant, "otra-cuenta", otro.Id, "SELLO-OTRA-CUENTA", debeCambiarContrasena: false, dobleFactor: false);
        var rol = await contexto.Roles.SingleAsync(r => r.Name == Roles.GestorCae);
        contexto.UserRoles.Add(new IdentityUserRole<Guid> { UserId = _actorReal, RoleId = rol.Id });
        var sesion = new SesionUsuario(_actorReal, _sid, DateTime.UtcNow);
        contexto.SesionesUsuario.Add(sesion);
        await contexto.SaveChangesAsync();
        _sesion = sesion.Id;
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadena);

    [Fact]
    public async Task Soporte_solo_consulta_existencia_de_login_en_el_Tenant_firmado()
    {
        await using (var contexto = ContextoPropietario())
        {
            contexto.UserLogins.AddRange(
                new IdentityUserLogin<Guid> { LoginProvider = "Microsoft", ProviderKey = "objetivo", UserId = _usuarioSimulado },
                new IdentityUserLogin<Guid> { LoginProvider = "Microsoft", ProviderKey = "ajeno", UserId = _cuentaDeOtroTenant });
            await contexto.SaveChangesAsync();
        }

        await using var conexion = new NpgsqlConnection(_cadena);
        await conexion.OpenAsync();
        await using (var rol = new NpgsqlCommand("SET ROLE cae_app_soporte", conexion))
            await rol.ExecuteNonQueryAsync();
        await using (var identidad = new NpgsqlCommand("SELECT current_user", conexion))
            ((string)(await identidad.ExecuteScalarAsync())!).Should().Be("cae_app_soporte");

        await using (var directa = new NpgsqlCommand("SELECT \"ProviderKey\", \"UserId\" FROM public.\"AspNetUserLogins\"", conexion))
        {
            var leer = async () => await directa.ExecuteReaderAsync();
            (await leer.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        }

        async Task<bool> TieneLogin(Guid usuario)
        {
            await using var comando = new NpgsqlCommand("SELECT public.app_cuenta_tiene_login_externo(@usuario)", conexion);
            comando.Parameters.AddWithValue("usuario", usuario);
            return (bool)(await comando.ExecuteScalarAsync())!;
        }

        (await TieneLogin(_usuarioSimulado)).Should().BeFalse("sin contexto firmado falla cerrado");
        await ContextoRlsDePruebas.FijarAsync(conexion, tenantId: _tenantObjetivo);
        (await TieneLogin(_usuarioSimulado)).Should().BeTrue();
        (await TieneLogin(_cuentaDeOtroTenant)).Should().BeFalse();
        (await TieneLogin(_actorReal)).Should().BeFalse();
        (await TieneLogin(Guid.NewGuid())).Should().BeFalse();

        await using var contextoSoporte = ContextoDeSoporte(PrincipalDe(_actorReal, SelloActorReal));
        var tenant = new TenantActualAmbiental { TenantId = _tenantObjetivo };
        var directorio = new DirectorioUsuariosTenant(
            new UserManager<ApplicationUser>(new AlmacenUsuarios(contextoSoporte, tenant), null!, null!, null!, null!, null!, null!, null!, null!),
            contextoSoporte, tenant, new PuertaAccesoDatos(), contextoSoporte, null!);
        (await directorio.ObtenerIdsConLoginExternoAsync([_usuarioSimulado, _cuentaDeOtroTenant, _actorReal]))
            .Should().BeEquivalentTo(new[] { _usuarioSimulado });
    }

    /// <summary>
    /// La activación pendiente de una cuenta por SSO también se responde bajo Sesión Privilegiada sin tocar la
    /// tabla: <c>_usuarioSimulado</c> no tiene contraseña, y solo el vínculo la distingue de una invitación.
    /// </summary>
    [Fact]
    public async Task Bajo_soporte_una_cuenta_con_login_externo_no_queda_pendiente_de_activar()
    {
        await using (var contexto = ContextoPropietario())
        {
            contexto.UserLogins.Add(new IdentityUserLogin<Guid> { LoginProvider = "Microsoft", ProviderKey = "objetivo", UserId = _usuarioSimulado });
            await contexto.SaveChangesAsync();
        }

        await using var contextoSoporte = ContextoDeSoporte(PrincipalDe(_actorReal, SelloActorReal));
        var tenant = new TenantActualAmbiental { TenantId = _tenantObjetivo };
        var usuarios = new UserManager<ApplicationUser>(new AlmacenUsuarios(contextoSoporte, tenant), null!, null!, null!, null!, null!, null!, null!, null!);
        var puerta = new PuertaAccesoDatos();
        var directorio = new DirectorioUsuariosTenant(usuarios, contextoSoporte, tenant, puerta, contextoSoporte, null!);
        var cuentas = new GestionCuentasUsuarioIdentity(usuarios, puerta, directorio, contextoSoporte);

        var cuenta = await cuentas.ObtenerAsync(_usuarioSimulado);

        cuenta.Should().NotBeNull();
        cuenta!.PendienteActivacion.Should().BeFalse();
    }

    [Theory]
    [InlineData("cae_app_runtime")]
    [InlineData("cae_app_aprovisionamiento")]
    public async Task Solo_soporte_ejecuta_la_lectura_de_login_externo(string rol)
    {
        await using var conexion = new NpgsqlConnection(_cadena);
        await conexion.OpenAsync();
        await using (var cambiar = new NpgsqlCommand($"SET ROLE {rol};", conexion))
            await cambiar.ExecuteNonQueryAsync();

        await using var comando = new NpgsqlCommand("SELECT public.app_cuenta_tiene_login_externo(gen_random_uuid())", conexion);
        var ejecutar = async () => await comando.ExecuteScalarAsync();

        (await ejecutar.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    /// <summary>
    /// La forma es el contrato: una cuenta como único argumento (el Tenant no se puede pedir), un booleano como
    /// respuesta (<c>ProviderKey</c> no sale), <c>search_path</c> fijado y el mismo propietario que las tablas que
    /// lee. Soporte TALVEG se queda sin la tabla, ni entera ni por columnas, y <c>cae_app_runtime</c> la conserva
    /// porque el inicio de sesión la lee antes de conocer el Tenant.
    /// </summary>
    [Fact]
    public async Task La_lectura_de_login_externo_solo_devuelve_un_booleano_y_retira_la_tabla_a_soporte()
    {
        await using var conexion = new NpgsqlConnection(_cadena);
        await conexion.OpenAsync();
        await using var comando = new NpgsqlCommand($"""
            SELECT p.prosecdef, pg_get_function_identity_arguments(p.oid), p.prorettype::regtype::text, p.proconfig, p.provolatile::text,
                   p.proowner = (SELECT c.relowner FROM pg_class c WHERE c.oid = 'public."AspNetUsers"'::regclass),
                   has_any_column_privilege('cae_app_soporte', 'public."AspNetUserLogins"', 'SELECT'),
                   has_table_privilege('cae_app_runtime', 'public."AspNetUserLogins"', 'SELECT')
            FROM pg_proc p WHERE p.oid = '{FuncionLoginExterno}'::regprocedure;
            """, conexion);

        await using var lector = await comando.ExecuteReaderAsync();
        (await lector.ReadAsync()).Should().BeTrue();
        lector.GetBoolean(0).Should().BeTrue("sin SECURITY DEFINER no leería la tabla que se retira a soporte");
        lector.GetString(1).Should().Be("p_usuario uuid");
        lector.GetString(2).Should().Be("boolean");
        lector.GetFieldValue<string[]>(3).Should().ContainSingle(c => c.StartsWith("search_path=", StringComparison.Ordinal));
        lector.GetString(4).Should().Be("s");
        lector.GetBoolean(5).Should().BeTrue("la función lee con el propietario de las tablas");
        lector.GetBoolean(6).Should().BeFalse();
        lector.GetBoolean(7).Should().BeTrue();
    }

    [Fact]
    public async Task El_Actor_real_firma_el_contexto_de_la_Sesion_Privilegiada()
    {
        var principal = PrincipalDe(_actorReal, SelloActorReal);
        await using var contexto = ContextoDeSoporte(principal);

        (await contexto.Database.SqlQuery<string>($"SELECT current_user::text AS \"Value\"").SingleAsync())
            .Should().Be("cae_app_soporte", "sin esta identidad el test mediría otro camino");
        (await contexto.Database.SqlQuery<Guid>($"SELECT public.app_ctx_usuario_id() AS \"Value\"").SingleAsync())
            .Should().Be(_actorReal, "el contexto lo firma la cuenta del principal, nunca una cuenta del Tenant objetivo");
        (await ServicioReal(principal).ObtenerUsuarioActualIdAsync()).Should().Be(_actorReal);
    }

    [Fact]
    public async Task Con_su_sello_y_su_sesion_el_Actor_real_sigue_vigente_bajo_soporte()
    {
        var principal = PrincipalDe(_actorReal, SelloActorReal);
        await using var contexto = ContextoDeSoporte(principal);

        (await Vigencia(contexto).SigueVigenteAsync(principal)).Should().BeTrue();
    }

    /// <summary>
    /// Actor real y Usuario simulado nunca se mezclan. Con el contexto firmado por uno, el sello del otro no
    /// valida en ninguna de las dos direcciones, y la función no tiene forma de preguntar por una cuenta
    /// que no sea la firmada.
    /// </summary>
    [Fact]
    public async Task El_sello_del_Usuario_simulado_no_valida_al_Actor_real()
    {
        var principal = PrincipalDe(_actorReal, SelloUsuarioSimulado);
        await using var contexto = ContextoDeSoporte(principal);

        (await Vigencia(contexto).SigueVigenteAsync(principal)).Should().BeFalse();
    }

    /// <summary>
    /// El ámbito nuevo que abre cada ciclo del circuito, con las piezas reales: su proveedor no tiene estado,
    /// así que <see cref="CurrentUserService"/> y <see cref="ClienteActivoSeleccionado"/> leen el mismo
    /// <c>HttpContext.User</c>, que es el Actor real. Sin <c>HttpContext</c> no habría selección, y por eso ese
    /// caso va por el camino antiguo.
    /// </summary>
    [Fact]
    public async Task En_el_ambito_nuevo_del_circuito_seleccion_y_firma_salen_del_Actor_real()
    {
        var principal = PrincipalDe(_actorReal, SelloActorReal);
        var protector = new EphemeralDataProtectionProvider();
        var token = ClienteActivoSeleccionado.Proteger(protector, _actorReal, _tenantObjetivo, null, Guid.NewGuid());
        var http = new DefaultHttpContext { User = principal };
        http.Request.Headers.Cookie = $"{ClienteActivoSeleccionado.NombreCookie}={Uri.EscapeDataString(token)}";
        var acceso = new HttpContextAccessor { HttpContext = http };
        var seleccion = new ClienteActivoSeleccionado(acceso, protector);
        using var sinEstado = new ProveedorAutenticacionRevalidada(
            NullLoggerFactory.Instance, null!, Options.Create(new SecurityStampValidatorOptions()));
        var usuario = new CurrentUserService(sinEstado, acceso, seleccion, new ServiceCollection().BuildServiceProvider());
        var tenantActual = new TenantActualAmbiental { TenantId = _tenantObjetivo };
        await using var contexto = Contexto(tenantActual, new TenantRlsConnectionInterceptor(
            tenantActual, seleccion, usuario, BaseDatosPostgresDePruebas.FirmanteContextoRls));

        seleccion.SesionPrivilegiadaIdSeleccionada.Should().NotBeNull("sin selección este test no recorrería el camino nuevo");
        (await contexto.Database.SqlQuery<string>($"SELECT current_user::text AS \"Value\"").SingleAsync())
            .Should().Be("cae_app_soporte");
        (await contexto.Database.SqlQuery<Guid>($"SELECT public.app_ctx_usuario_id() AS \"Value\"").SingleAsync())
            .Should().Be(_actorReal);
        (await Vigencia(contexto, seleccion).SigueVigenteAsync(principal)).Should().BeTrue();
        (await Vigencia(contexto, seleccion).PrincipalVigenteAsync(principal)).Should().BeSameAs(principal,
            "rehacerlo con la fábrica cargaría la cuenta bajo cae_app_soporte");
    }

    [Fact]
    public async Task El_sello_del_Actor_real_no_valida_un_contexto_firmado_por_otra_cuenta()
    {
        var principal = PrincipalDe(_usuarioSimulado, SelloActorReal);
        await using var contexto = ContextoDeSoporte(principal);

        (await Vigencia(contexto).SigueVigenteAsync(principal)).Should().BeFalse();
    }

    [Fact]
    public async Task Un_sello_rotado_deja_de_validar()
    {
        var principal = PrincipalDe(_actorReal, SelloActorReal);
        await using var contexto = ContextoDeSoporte(principal);
        (await Vigencia(contexto).SigueVigenteAsync(principal)).Should().BeTrue("control: antes de rotar, valida");

        await ComoPropietarioAsync(
            "UPDATE \"AspNetUsers\" SET \"SecurityStamp\" = 'SELLO-NUEVO' WHERE \"Id\" = @id",
            new NpgsqlParameter("id", _actorReal));

        (await Vigencia(contexto).SigueVigenteAsync(principal)).Should().BeFalse();
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(1, false)]
    public async Task El_umbral_de_cuenta_desactivada_es_el_de_ApplicationUser(int minutosRespectoDelUmbral, bool vigente)
    {
        var ahora = DateTimeOffset.UtcNow;
        var hasta = ahora + ApplicationUser.UmbralDeCuentaDesactivada + TimeSpan.FromMinutes(minutosRespectoDelUmbral);
        ApplicationUser.BloqueoDesactivaLaCuenta(hasta, ahora).Should().Be(!vigente, "SQL y C# aplican el mismo criterio");
        await FijarBloqueoAsync(hasta);
        var principal = PrincipalDe(_actorReal, SelloActorReal);
        await using var contexto = ContextoDeSoporte(principal);

        (await Vigencia(contexto).SigueVigenteAsync(principal)).Should().Be(vigente);
    }

    [Fact]
    public async Task Un_bloqueo_temporal_no_corta_la_sesion_y_una_cuenta_desactivada_si()
    {
        var principal = PrincipalDe(_actorReal, SelloActorReal);
        await using var contexto = ContextoDeSoporte(principal);

        await FijarBloqueoAsync(DateTimeOffset.UtcNow.AddMinutes(15));
        (await Vigencia(contexto).SigueVigenteAsync(principal)).Should().BeTrue("un bloqueo por intentos fallidos no desactiva");

        await FijarBloqueoAsync(DateTimeOffset.MaxValue);
        (await Vigencia(contexto).SigueVigenteAsync(principal)).Should().BeFalse();
    }

    [Fact]
    public async Task Un_bloqueo_infinito_tambien_es_una_cuenta_desactivada()
    {
        await ComoPropietarioAsync(
            "UPDATE \"AspNetUsers\" SET \"LockoutEnd\" = 'infinity' WHERE \"Id\" = @id",
            new NpgsqlParameter("id", _actorReal));
        var principal = PrincipalDe(_actorReal, SelloActorReal);
        await using var contexto = ContextoDeSoporte(principal);

        (await Vigencia(contexto).SigueVigenteAsync(principal)).Should().BeFalse();
    }

    /// <summary>La comparación es exacta, como la de Identity: el sello no se compara sin distinguir mayúsculas.</summary>
    [Fact]
    public async Task Un_sello_con_otras_mayusculas_no_es_el_mismo_sello()
    {
        var principal = PrincipalDe(_actorReal, SelloActorReal.ToLowerInvariant());
        await using var contexto = ContextoDeSoporte(principal);

        (await Vigencia(contexto).SigueVigenteAsync(principal)).Should().BeFalse();
    }

    [Fact]
    public async Task Una_sesion_revocada_no_vale_aunque_el_sello_coincida()
    {
        var principal = PrincipalDe(_actorReal, SelloActorReal);
        await using var contexto = ContextoDeSoporte(principal);
        (await Vigencia(contexto).SigueVigenteAsync(principal)).Should().BeTrue("control: antes de revocar, vale");

        await ComoPropietarioAsync(
            "UPDATE \"SesionesUsuario\" SET \"RevocadaEnUtc\" = now() WHERE \"Id\" = @id",
            new NpgsqlParameter("id", _sesion));

        (await Vigencia(contexto).SigueVigenteAsync(principal)).Should().BeFalse();
    }

    [Fact]
    public async Task Sin_identificador_de_sesion_o_sin_sello_no_se_puede_afirmar_nada()
    {
        var sinSid = PrincipalDe(_actorReal, SelloActorReal, conSid: false);
        var sinSello = PrincipalDe(_actorReal, sello: null);
        await using var contexto = ContextoDeSoporte(sinSid);

        (await Vigencia(contexto).SigueVigenteAsync(sinSid)).Should().BeFalse();
        (await Vigencia(contexto).SigueVigenteAsync(sinSello)).Should().BeFalse();
    }

    [Fact]
    public async Task Sin_sello_la_funcion_responde_false_aunque_el_contexto_sea_valido()
    {
        await using var contexto = ContextoDeSoporte(PrincipalDe(_actorReal, SelloActorReal));

        (await contexto.Database.SqlQueryRaw<bool>("SELECT public.app_cuenta_firmada_vigente(NULL) AS \"Value\"").SingleAsync())
            .Should().BeFalse();
    }

    [Fact]
    public async Task Sin_contexto_firmado_la_funcion_responde_false_incluso_con_un_sello_real()
    {
        await using var conexion = new NpgsqlConnection(_cadena);
        await conexion.OpenAsync();
        await using (var rol = new NpgsqlCommand("SET ROLE cae_app_soporte;", conexion))
            await rol.ExecuteNonQueryAsync();

        await using var comando = new NpgsqlCommand("SELECT public.app_cuenta_firmada_vigente(@sello)", conexion);
        comando.Parameters.AddWithValue("sello", SelloActorReal);

        ((bool)(await comando.ExecuteScalarAsync())!).Should().BeFalse();
    }

    [Theory]
    [InlineData("cae_app_runtime")]
    [InlineData("cae_app_aprovisionamiento")]
    public async Task Solo_soporte_ejecuta_la_funcion(string rol)
    {
        await using var conexion = new NpgsqlConnection(_cadena);
        await conexion.OpenAsync();
        await using (var cambiar = new NpgsqlCommand($"SET ROLE {rol};", conexion))
            await cambiar.ExecuteNonQueryAsync();

        await using var comando = new NpgsqlCommand("SELECT public.app_cuenta_firmada_vigente('x')", conexion);
        var ejecutar = async () => await comando.ExecuteScalarAsync();

        (await ejecutar.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Theory]
    [InlineData(Funcion)]
    [InlineData(FuncionLoginExterno)]
    public async Task EXECUTE_solo_para_soporte_y_nunca_para_PUBLIC(string funcion)
    {
        await using var conexion = new NpgsqlConnection(_cadena);
        await conexion.OpenAsync();
        await using var comando = new NpgsqlCommand($"""
            SELECT COALESCE(r.rolname, 'PUBLIC')
            FROM pg_proc p
            CROSS JOIN LATERAL aclexplode(p.proacl) a
            LEFT JOIN pg_roles r ON r.oid = a.grantee
            WHERE p.oid = '{funcion}'::regprocedure AND a.grantee <> p.proowner;
            """, conexion);

        var concedidos = new List<string>();
        await using var lector = await comando.ExecuteReaderAsync();
        while (await lector.ReadAsync())
            concedidos.Add(lector.GetString(0));

        concedidos.Should().BeEquivalentTo(["cae_app_soporte"]);
    }

    /// <summary>
    /// La forma es el contrato: un único argumento de texto (la cuenta no se puede pedir), un booleano como
    /// respuesta (el sello no sale) y <c>search_path</c> fijado en una función <c>SECURITY DEFINER</c> y
    /// <c>STABLE</c>.
    /// </summary>
    [Fact]
    public async Task La_funcion_solo_admite_un_sello_y_solo_devuelve_un_booleano()
    {
        await using var conexion = new NpgsqlConnection(_cadena);
        await conexion.OpenAsync();
        await using var comando = new NpgsqlCommand($"""
            SELECT p.prosecdef, pg_get_function_identity_arguments(p.oid), p.prorettype::regtype::text, p.proconfig, p.provolatile::text
            FROM pg_proc p WHERE p.oid = '{Funcion}'::regprocedure;
            """, conexion);

        await using var lector = await comando.ExecuteReaderAsync();
        (await lector.ReadAsync()).Should().BeTrue();
        lector.GetBoolean(0).Should().BeTrue("sin SECURITY DEFINER no leería la columna cuando se retire a soporte");
        lector.GetString(1).Should().Be("p_sello text");
        lector.GetString(2).Should().Be("boolean");
        lector.GetFieldValue<string[]>(3).Should().ContainSingle(c => c.StartsWith("search_path=", StringComparison.Ordinal));
        lector.GetString(4).Should().Be("s");
    }

    [Fact]
    public async Task La_lectura_de_la_cuenta_propia_proyecta_lo_que_necesita_bajo_soporte()
    {
        await using var contexto = ContextoDeSoporte(PrincipalDe(_actorReal, SelloActorReal));
        var lectura = new LecturaCuentaPropia(contexto);

        var estado = await lectura.ObtenerAsync(_actorReal);

        estado.Should().NotBeNull();
        estado!.DebeCambiarContrasena.Should().BeTrue();
        estado.DobleFactorActivo.Should().BeTrue();
        estado.Roles.Should().Equal(Roles.GestorCae);
        (await lectura.LeerRolAsync(_actorReal)).Should().Be(Roles.GestorCae);
        (await lectura.LeerRolAsync(_cuentaDeOtroTenant)).Should().BeNull();
        (await lectura.ObtenerAsync(_cuentaDeOtroTenant)).Should().BeNull("la proyección no amplía lo que la RLS deja ver");
    }

    /// <summary>
    /// La lectura del rol responde lo mismo que el camino al que sustituye: el primero en orden ordinal de los roles
    /// de <see cref="LecturaCuentaPropia.ObtenerAsync"/>, y <c>null</c> sin rol o sin cuenta. El test unitario de
    /// <c>CurrentUserService</c> usa un doble, así que el orden solo se observa aquí.
    /// </summary>
    [Fact]
    public async Task El_rol_de_origen_conserva_su_conducta_con_varios_roles_sin_rol_y_sin_cuenta()
    {
        await using (var propietario = ContextoPropietario())
        {
            var administrador = await propietario.Roles.SingleAsync(r => r.Name == Roles.Administrador);
            propietario.UserRoles.Add(new IdentityUserRole<Guid> { UserId = _actorReal, RoleId = administrador.Id });
            await propietario.SaveChangesAsync();
        }

        await using var contexto = ContextoDeSoporte(PrincipalDe(_actorReal, SelloActorReal));
        var lectura = new LecturaCuentaPropia(contexto);

        (await lectura.ObtenerAsync(_actorReal))!.Roles.Should().BeEquivalentTo([Roles.GestorCae, Roles.Administrador]);
        (await lectura.LeerRolAsync(_actorReal)).Should().Be(Roles.Administrador, "gana el primero en orden ordinal");

        await ComoPropietarioAsync(
            "DELETE FROM \"AspNetUserRoles\" WHERE \"UserId\" = @id", new NpgsqlParameter("id", _actorReal));

        (await lectura.ObtenerAsync(_actorReal))!.Roles.Should().BeEmpty();
        (await lectura.LeerRolAsync(_actorReal)).Should().BeNull("una cuenta sin rol no tiene rol de origen");
        (await lectura.LeerRolAsync(Guid.NewGuid())).Should().BeNull("lo mismo que ObtenerAsync sin cuenta");
    }

    /// <summary>
    /// La lista de un aviso de seguridad: Administradores del Tenant propietario, sin los desactivados y sin los de
    /// otro Tenant, con el bloqueo temporal dentro porque no es una desactivación. El <c>null</c> de la cuenta excluida
    /// tiene que traducirse como «no excluir a nadie»: es el aviso de «no he sido yo», que también llega a la propia
    /// cuenta si es Administradora.
    /// </summary>
    [Fact]
    public async Task Los_avisos_de_seguridad_van_a_los_Administradores_activos_del_Tenant_propietario()
    {
        var activa = Guid.NewGuid();
        var sinCorreo = Guid.NewGuid();
        var conBloqueoTemporal = Guid.NewGuid();
        var desactivada = Guid.NewGuid();
        var deOtroTenant = Guid.NewGuid();
        await using var contexto = ContextoPropietario();
        Sembrar(contexto, activa, "administradora-activa", _tenantOrigen, "SELLO-1", false, false);
        Sembrar(contexto, sinCorreo, "administradora-sin-correo", _tenantOrigen, "SELLO-2", false, false);
        Sembrar(contexto, conBloqueoTemporal, "administradora-bloqueada", _tenantOrigen, "SELLO-3", false, false);
        Sembrar(contexto, desactivada, "administradora-desactivada", _tenantOrigen, "SELLO-4", false, false);
        Sembrar(contexto, deOtroTenant, "administradora-de-otro-tenant", _tenantObjetivo, "SELLO-5", false, false);
        contexto.Users.Local.Single(u => u.Id == sinCorreo).Email = null;
        contexto.Users.Local.Single(u => u.Id == conBloqueoTemporal).LockoutEnd = DateTimeOffset.UtcNow.AddMinutes(15);
        contexto.Users.Local.Single(u => u.Id == desactivada).LockoutEnd = DateTimeOffset.MaxValue;
        var rol = await contexto.Roles.SingleAsync(r => r.Name == Roles.Administrador);
        foreach (var cuenta in new[] { activa, sinCorreo, conBloqueoTemporal, desactivada, deOtroTenant })
            contexto.UserRoles.Add(new IdentityUserRole<Guid> { UserId = cuenta, RoleId = rol.Id });
        await contexto.SaveChangesAsync();

        var todos = await VigilanciaInicioSesionService.AdministradoresAsync(contexto, rol.NormalizedName!, _tenantOrigen, null, default);
        var salvoLaAvisada = await VigilanciaInicioSesionService.AdministradoresAsync(contexto, rol.NormalizedName!, _tenantOrigen, activa, default);

        todos.Select(a => a.Id).Should().BeEquivalentTo([activa, sinCorreo, conBloqueoTemporal]);
        todos.Single(a => a.Id == activa).Email.Should().Be("administradora-activa@frontera-cuenta.test");
        todos.Single(a => a.Id == sinCorreo).Email.Should().BeNull();
        salvoLaAvisada.Select(a => a.Id).Should().BeEquivalentTo([sinCorreo, conBloqueoTemporal]);
    }

    /// <summary>
    /// La misma lista bajo soporte y sin las columnas de credenciales: responde sin pedirlas, y la RLS sigue decidiendo.
    /// La política de <c>AspNetUserRoles</c> deriva su visibilidad de la cuenta en <c>AspNetUsers</c>.
    /// </summary>
    [Fact]
    public async Task Bajo_soporte_la_lista_de_avisos_no_amplia_lo_que_la_RLS_deja_ver()
    {
        var administradoraDelObjetivo = Guid.NewGuid();
        string rolNormalizado;
        Guid otroTenant;
        await using (var propietario = ContextoPropietario())
        {
            Sembrar(propietario, administradoraDelObjetivo, "administradora-del-objetivo", _tenantObjetivo, "SELLO-6", false, false);
            var rol = await propietario.Roles.SingleAsync(r => r.Name == Roles.Administrador);
            rolNormalizado = rol.NormalizedName!;
            otroTenant = await propietario.Users.Where(u => u.Id == _cuentaDeOtroTenant).Select(u => u.TenantId).SingleAsync();
            propietario.UserRoles.AddRange(
                new IdentityUserRole<Guid> { UserId = administradoraDelObjetivo, RoleId = rol.Id },
                new IdentityUserRole<Guid> { UserId = _cuentaDeOtroTenant, RoleId = rol.Id });
            await propietario.SaveChangesAsync();
        }

        await QuitarLasCredencialesASoporteAsync();
        await using var contexto = ContextoDeSoporte(PrincipalDe(_actorReal, SelloActorReal));

        (await VigilanciaInicioSesionService.AdministradoresAsync(contexto, rolNormalizado, _tenantObjetivo, null, default))
            .Select(a => a.Id).Should().Equal(administradoraDelObjetivo);
        (await VigilanciaInicioSesionService.AdministradoresAsync(contexto, rolNormalizado, otroTenant, null, default))
            .Should().BeEmpty("la consulta no amplía lo que la RLS deja ver");
    }

    /// <summary>
    /// Lo que hará la PR del <c>REVOKE</c>, solo en esta base de prueba: soporte pierde <c>PasswordHash</c> y
    /// <c>SecurityStamp</c>. La frontera sigue funcionando sin ellas, y materializar la cuenta falla, que es lo que
    /// demuestra que ninguno de los dos caminos de la frontera las estaba leyendo.
    /// </summary>
    [Fact]
    public async Task La_frontera_no_necesita_las_columnas_de_credenciales()
    {
        await QuitarLasCredencialesASoporteAsync();

        var principal = PrincipalDe(_actorReal, SelloActorReal);
        await using var contexto = ContextoDeSoporte(principal);

        (await Vigencia(contexto).SigueVigenteAsync(principal)).Should().BeTrue();
        (await new LecturaCuentaPropia(contexto).ObtenerAsync(_actorReal)).Should().NotBeNull();
        (await new LecturaCuentaPropia(contexto).LeerRolAsync(_actorReal)).Should().Be(Roles.GestorCae);

        var materializar = async () => await contexto.Users.AsNoTracking().SingleAsync(u => u.Id == _actorReal);
        (await materializar.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    /// <summary>
    /// Lo mismo para <c>/delegaciones</c> y las tres consultas escalares del directorio: bajo <c>cae_app_soporte</c>
    /// y sin las columnas siguen respondiendo, con la RLS de la conexión. <c>ObtenerVisiblesAsync</c> y
    /// <c>ObtenerEquipoAsync</c> no entran: siguen materializando hasta el contrato de activación.
    /// </summary>
    [Fact]
    public async Task Las_personas_delegadas_y_el_directorio_no_necesitan_las_columnas_de_credenciales()
    {
        await QuitarLasCredencialesASoporteAsync();

        await using var contexto = ContextoDeSoporte(PrincipalDe(_actorReal, SelloActorReal));

        var personas = await new LecturaPersonasDelegadas(contexto).ObtenerAsync(
            new[] { _actorReal, _usuarioSimulado, _cuentaDeOtroTenant }, default);

        personas.Should().BeEquivalentTo(new[]
        {
            new PersonaDelegadaVisible(_actorReal, "actor-real", "actor-real@frontera-cuenta.test", Roles.GestorCae),
            new PersonaDelegadaVisible(_usuarioSimulado, "usuario-simulado", "usuario-simulado@frontera-cuenta.test", null),
        }, "la proyección no amplía lo que la RLS deja ver");

        var tenant = new TenantActualAmbiental { TenantId = _tenantObjetivo };
        var directorio = new DirectorioUsuariosTenant(
            new UserManager<ApplicationUser>(new AlmacenUsuarios(contexto, tenant), null!, null!, null!, null!, null!, null!, null!, null!),
            contexto, tenant, new PuertaAccesoDatos(), contexto, null!);

        (await directorio.EsCuentaActivaConRolAsync(_actorReal, _tenantOrigen, Roles.GestorCae)).Should().BeTrue();
        (await directorio.ObtenerCuentasActivasConRolAsync(_tenantOrigen, Roles.GestorCae)).Should().Equal(_actorReal);
        (await directorio.ObtenerAsync(_usuarioSimulado)).Should().NotBeNull();
        (await directorio.ObtenerAsync(_cuentaDeOtroTenant)).Should().BeNull("una cuenta que la RLS no deja ver no existe aquí");
    }

    /// <summary>
    /// Tema, idioma y actividad se siguen leyendo bajo Sesión Privilegiada sin las columnas de credenciales. La
    /// escritura no cambia: <c>cae_app_soporte</c> no tiene <c>UPDATE</c>, la base la rechaza y el contexto queda
    /// limpio para lo siguiente del circuito.
    /// </summary>
    [Fact]
    public async Task Bajo_soporte_las_preferencias_se_leen_sin_credenciales_y_escribirlas_sigue_rechazado()
    {
        await QuitarLasCredencialesASoporteAsync();

        await using var contexto = ContextoDeSoporte(PrincipalDe(_actorReal, SelloActorReal));
        var preferencias = new PreferenciasCuentaPropia(contexto, new PuertaAccesoDatos());

        (await preferencias.ObtenerAsync(_actorReal)).Should().NotBeNull();

        var escribir = () => preferencias.RegistrarActividadAsync(_actorReal, DateTime.UtcNow);
        (await escribir.Should().ThrowAsync<DbUpdateException>())
            .WithInnerException<PostgresException>()
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        contexto.ChangeTracker.Entries().Should().BeEmpty();
    }

    private async Task QuitarLasCredencialesASoporteAsync()
    {
        await ComoPropietarioAsync("""
            REVOKE SELECT ON public."AspNetUsers" FROM cae_app_soporte;
            DO $$
            DECLARE columnas text;
            BEGIN
                SELECT string_agg(quote_ident(attname), ', ') INTO columnas
                FROM pg_attribute
                WHERE attrelid = 'public."AspNetUsers"'::regclass AND attnum > 0 AND NOT attisdropped
                  AND attname NOT IN ('PasswordHash', 'SecurityStamp');
                EXECUTE format('GRANT SELECT (%s) ON public."AspNetUsers" TO cae_app_soporte', columnas);
            END $$;
            """);
        await using (var conexion = new NpgsqlConnection(_cadena))
        {
            await conexion.OpenAsync();
            await using var comando = new NpgsqlCommand("""
                SELECT has_column_privilege('cae_app_soporte', 'public."AspNetUsers"', 'PasswordHash', 'SELECT')
                    OR has_column_privilege('cae_app_soporte', 'public."AspNetUsers"', 'SecurityStamp', 'SELECT');
                """, conexion);
            ((bool)(await comando.ExecuteScalarAsync())!).Should().BeFalse("si no, este test no estaría quitando nada");
        }
    }

    private static void Sembrar(
        CaeManagerDbContext contexto, Guid id, string nombre, Guid tenant, string sello, bool debeCambiarContrasena, bool dobleFactor)
    {
        var correo = $"{nombre}@frontera-cuenta.test";
        contexto.Users.Add(new ApplicationUser
        {
            Id = id,
            UserName = correo,
            NormalizedUserName = correo.ToUpperInvariant(),
            Email = correo,
            NormalizedEmail = correo.ToUpperInvariant(),
            NombreCompleto = nombre,
            TenantId = tenant,
            SecurityStamp = sello,
            DebeCambiarContrasena = debeCambiarContrasena,
            TwoFactorEnabled = dobleFactor,
        });
    }

    private Task FijarBloqueoAsync(DateTimeOffset hasta) => ComoPropietarioAsync(
        "UPDATE \"AspNetUsers\" SET \"LockoutEnd\" = @hasta WHERE \"Id\" = @id",
        new NpgsqlParameter("hasta", hasta), new NpgsqlParameter("id", _actorReal));

    private async Task ComoPropietarioAsync(string sql, params NpgsqlParameter[] parametros)
    {
        await using var conexion = new NpgsqlConnection(_cadena);
        await conexion.OpenAsync();
        await using var comando = new NpgsqlCommand(sql, conexion);
        comando.Parameters.AddRange(parametros);
        await comando.ExecuteNonQueryAsync();
    }

    private ClaimsPrincipal PrincipalDe(Guid cuenta, string? sello, bool conSid = true)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, cuenta.ToString()),
            new(TenantClaimsPrincipalFactory.TipoClaimTenantId, _tenantOrigen.ToString()),
        };
        if (sello is not null) claims.Add(new Claim(new IdentityOptions().ClaimsIdentity.SecurityStampClaimType, sello));
        if (conSid) claims.Add(new Claim(SignInManagerCuentaDesactivada.ClaimSid, _sid.ToString("N")));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "prueba"));
    }

    private CurrentUserService ServicioReal(ClaimsPrincipal principal) => new(
        new EstadoDeAutenticacion(principal), new HttpContextAccessor(), _seleccion,
        new ServiceCollection().BuildServiceProvider());

    private VigenciaSesionCuenta Vigencia(CaeManagerDbContext contexto, IClienteActivoSeleccionado? seleccion = null) => new(
        seleccion ?? _seleccion, null!, contexto,
        new RegistroSesionesUsuario(contexto, null!, null!, NullLogger<RegistroSesionesUsuario>.Instance),
        Options.Create(new IdentityOptions()), NullLogger<VigenciaSesionCuenta>.Instance);

    private CaeManagerDbContext ContextoDeSoporte(ClaimsPrincipal principal)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenantObjetivo };
        return Contexto(tenantActual, new TenantRlsConnectionInterceptor(
            tenantActual, _seleccion, ServicioReal(principal), BaseDatosPostgresDePruebas.FirmanteContextoRls));
    }

    private CaeManagerDbContext ContextoPropietario()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenantOrigen };
        return Contexto(tenantActual, new TenantSelladoInterceptor(tenantActual));
    }

    private CaeManagerDbContext Contexto(TenantActualAmbiental tenantActual, params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptores)
    {
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadena, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(interceptores)
            .Options;
        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private sealed class Seleccion(Guid tenantId, Guid? sesionId) : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => tenantId;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => sesionId;
    }

    private sealed class EstadoDeAutenticacion(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(principal));
    }
}
