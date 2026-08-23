using FluentAssertions;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Migraciones;

/// <summary>
/// <b>El bootstrap de clúster converge lo que debe y respeta lo que no le
/// pertenece.</b> Ejecuta el guion real —el mismo fichero que usan CI, el VPS y
/// el ensayo de restauración— contra estados de partida deliberadamente rotos.
///
/// <para>
/// Existe por un defecto real: la primera versión del guion (2026-08-22)
/// convergía <c>cae_app_runtime</c> a <c>NOLOGIN</c>, mientras producción
/// llevaba desde el 2026-08-14 con <c>LOGIN</c> habilitado. Reejecutarlo allí
/// habría retirado la identidad de conexión de la aplicación. El ratchet de
/// arquitectura vigila la <i>forma</i> del guion; esto comprueba su
/// <i>efecto</i>.
/// </para>
///
/// <para>
/// <b>Por qué cada caso vive dentro de una transacción.</b> Los roles son
/// objetos de CLÚSTER, compartidos por las bases de los 6 hilos que el arnés
/// ejecuta en paralelo. Dejar a <c>cae_app_runtime</c> con <c>BYPASSRLS</c>,
/// aunque fuera un instante, haría que los tests de aislamiento que corren a la
/// vez vieran filas de otros tenants y fallaran sin relación aparente con su
/// causa. Un <c>ALTER ROLE</c> sin confirmar no es visible para otras sesiones,
/// así que la mutación nunca sale de aquí — y el primer caso <b>lo demuestra</b>
/// con una conexión testigo en vez de darlo por supuesto.
/// </para>
/// </summary>
public class ContratoDeRolesDeClusterTests
{
    private static string Guion() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "roles-de-cluster.sql"));

    /// <summary>
    /// El caso que motivó todo: producción tiene <c>LOGIN</c> porque el
    /// despliegue se lo concedió, y el bootstrap no puede retirárselo.
    ///
    /// <para>
    /// Comprueba además, con una conexión aparte, que la mutación de este test
    /// no es observable desde fuera de su transacción. Sin esa comprobación, la
    /// seguridad del test sería una suposición sobre el aislamiento de
    /// PostgreSQL, y aquí no se supone nada que se pueda medir.
    /// </para>
    /// </summary>
    [Fact]
    public async Task El_bootstrap_no_retira_el_LOGIN_que_el_despliegue_concedio()
    {
        await using var conexion = new NpgsqlConnection(BaseDatosPostgresDePruebas.CadenaDeMantenimientoSinPool());
        await conexion.OpenAsync();
        await using var transaccion = await conexion.BeginTransactionAsync();

        // Precondición EXPLÍCITA, no ambiental: el test no puede depender de que
        // el clúster llegue con LOGIN puesto o quitado. Desde que el arnés lo
        // habilita para poder conectar de verdad con ese rol, el baseline es
        // LOGIN — y un test que asumiera lo contrario sería vacío en vez de roto.
        await EjecutarAsync(conexion, transaccion, "ALTER ROLE cae_app_runtime WITH LOGIN;");
        (await PuedeConectarAsync(conexion, transaccion, "cae_app_runtime")).Should().BeTrue();

        await EjecutarAsync(conexion, transaccion, Guion());

        (await PuedeConectarAsync(conexion, transaccion, "cae_app_runtime")).Should().BeTrue(
            "LOGIN en cae_app_runtime lo concede el despliegue, que es quien tiene la contraseña; el " +
            "bootstrap no puede otorgarlo y por tanto tampoco debe destruirlo — producción conecta con ese rol");

        await transaccion.RollbackAsync();
    }

    /// <summary>
    /// La otra mitad de la asimetría: en <c>cae_app_soporte</c>, <c>NOLOGIN</c>
    /// sí es un atributo de seguridad, y el bootstrap tiene que restituirlo.
    /// </summary>
    [Fact]
    public async Task El_bootstrap_retira_el_LOGIN_de_soporte_porque_ahi_si_es_seguridad()
    {
        await using var conexion = new NpgsqlConnection(BaseDatosPostgresDePruebas.CadenaDeMantenimientoSinPool());
        await conexion.OpenAsync();
        await using var transaccion = await conexion.BeginTransactionAsync();

        await EjecutarAsync(conexion, transaccion, "ALTER ROLE cae_app_soporte WITH LOGIN;");
        (await PuedeConectarAsync(conexion, transaccion, "cae_app_soporte")).Should().BeTrue();

        await EjecutarAsync(conexion, transaccion, Guion());

        (await PuedeConectarAsync(conexion, transaccion, "cae_app_soporte")).Should().BeFalse(
            "cae_app_soporte solo se adopta con SET ROLE desde una sesión ya autenticada: si pudiera " +
            "conectarse, sería una identidad de acceso y no una restricción de capacidad");

        await transaccion.RollbackAsync();
    }

    /// <summary>
    /// Los invariantes que el bootstrap sí debe imponer, contra el estado de
    /// partida más peligroso posible: un rol que puede saltarse RLS.
    /// </summary>
    [Theory]
    [InlineData("cae_app_runtime")]
    [InlineData("cae_app_soporte")]
    public async Task El_bootstrap_corrige_un_rol_que_pudiera_saltarse_RLS(string rol)
    {
        await using var conexion = new NpgsqlConnection(BaseDatosPostgresDePruebas.CadenaDeMantenimientoSinPool());
        await conexion.OpenAsync();
        await using var transaccion = await conexion.BeginTransactionAsync();

        await EjecutarAsync(conexion, transaccion, $"ALTER ROLE {rol} WITH BYPASSRLS CREATEDB CREATEROLE;");
        (await AtributoAsync(conexion, transaccion, rol, "rolbypassrls")).Should().BeTrue(
            "el estado de partida tiene que ser realmente inseguro, o el test no observaría ninguna corrección");

        // Y aquí vive la prueba de aislamiento, porque aquí la mutación SÍ difiere
        // del estado de partida: BYPASSRLS está en false en el clúster, así que un
        // testigo que lo viera en true probaría que la transacción no aísla. Con
        // LOGIN esta comprobación dejó de discriminar en cuanto el arnés pasó a
        // habilitarlo — un testigo que ve lo que ya había no demuestra nada.
        await using (var testigo = new NpgsqlConnection(BaseDatosPostgresDePruebas.CadenaDeMantenimientoSinPool()))
        {
            await testigo.OpenAsync();
            (await AtributoAsync(testigo, null, rol, "rolbypassrls")).Should().BeFalse(
                "la mutación de este test no debe ser visible fuera de su transacción: de eso depende que " +
                "no rompa los tests de aislamiento que corren en paralelo sobre el mismo clúster");
        }

        await EjecutarAsync(conexion, transaccion, Guion());

        (await AtributoAsync(conexion, transaccion, rol, "rolbypassrls")).Should().BeFalse(
            $"{rol} con BYPASSRLS haría inútiles todas las políticas de aislamiento");
        (await AtributoAsync(conexion, transaccion, rol, "rolsuper")).Should().BeFalse();
        (await AtributoAsync(conexion, transaccion, rol, "rolcreatedb")).Should().BeFalse();
        (await AtributoAsync(conexion, transaccion, rol, "rolcreaterole")).Should().BeFalse();

        await transaccion.RollbackAsync();
    }

    /// <summary>
    /// Estado en reposo, sin mutar nada: así queda el clúster después del
    /// bootstrap que el arnés ejecuta al arrancar el proceso.
    /// </summary>
    [Fact]
    public async Task Tras_el_bootstrap_ninguno_de_los_dos_roles_puede_saltarse_RLS()
    {
        await using var conexion = new NpgsqlConnection(BaseDatosPostgresDePruebas.CadenaDeMantenimientoSinPool());
        await conexion.OpenAsync();

        foreach (var rol in new[] { "cae_app_runtime", "cae_app_soporte" })
        {
            (await AtributoAsync(conexion, null, rol, "rolbypassrls")).Should().BeFalse($"{rol} nunca debe saltarse RLS");
            (await AtributoAsync(conexion, null, rol, "rolsuper")).Should().BeFalse($"{rol} nunca debe ser superusuario");
        }
    }

    private static async Task EjecutarAsync(NpgsqlConnection conexion, NpgsqlTransaction? transaccion, string sql)
    {
        await using var comando = new NpgsqlCommand(sql, conexion, transaccion);
        await comando.ExecuteNonQueryAsync();
    }

    private static Task<bool> PuedeConectarAsync(NpgsqlConnection conexion, NpgsqlTransaction? transaccion, string rol) =>
        AtributoAsync(conexion, transaccion, rol, "rolcanlogin");

    private static async Task<bool> AtributoAsync(
        NpgsqlConnection conexion, NpgsqlTransaction? transaccion, string rol, string atributo)
    {
        await using var comando = new NpgsqlCommand(
            $"SELECT {atributo} FROM pg_roles WHERE rolname = @rol;", conexion, transaccion);
        comando.Parameters.AddWithValue("rol", rol);

        var valor = await comando.ExecuteScalarAsync();
        valor.Should().NotBeNull($"el rol {rol} tiene que existir para poder comprobar {atributo}");
        return (bool)valor!;
    }
}
