using System.Runtime.CompilerServices;
using Npgsql;

namespace CaeManager.IntegrationTests;

/// <summary>
/// Adaptador del bootstrap de clúster para el arnés de tests.
///
/// <para>
/// <b>No define nada.</b> La especificación normativa de qué roles debe tener
/// un clúster y con qué atributos vive en un único sitio,
/// <c>deploy/bootstrap/roles-de-cluster.sql</c>, y los cinco entornos —CI,
/// desarrollo, VPS, ensayo de restauración y este arnés— son adaptadores que lo
/// ejecutan. Aquí no hay ni un nombre de rol ni un atributo escrito: si los
/// hubiera, acabarían divergiendo del fichero.
/// </para>
///
/// <para>
/// <b>Por qué un inicializador de módulo.</b> Corre una vez por proceso, antes
/// de cualquier fixture, que es exactamente la garantía que el contrato pide:
/// un escritor único termina el bootstrap antes de que arranque el primer
/// migrador. Los 89 ficheros que llaman a <c>MigrateAsync</c> no se enteran, y
/// no hay forma de que un test se salte el paso por olvido.
/// </para>
///
/// <para>
/// <b>Y por qué esto elimina la carrera en vez de mitigarla.</b> Antes, seis
/// migradores competían por crear un objeto de clúster desde la migración de su
/// propia base; tres de seis fallaban con <c>42704</c>. Ahora los roles ya
/// existen cuando el primero arranca, así que no hay nada que disputar. La
/// diferencia no es de probabilidad: es que la operación conflictiva ya no está
/// en el camino de migración.
/// </para>
///
/// <para>
/// Si el bootstrap falla, se lanza. Un arnés que arrancara sin los principales
/// del clúster produciría exactamente el fallo intermitente y difícil de leer
/// que este incremento existe para eliminar.
/// </para>
/// </summary>
internal static class BootstrapDeClusterEnTests
{
    [ModuleInitializer]
    internal static void Ejecutar()
    {
        var guion = Path.Combine(AppContext.BaseDirectory, "roles-de-cluster.sql");
        if (!File.Exists(guion))
            throw new InvalidOperationException(
                $"No se encontró el guion de bootstrap en {guion}. Debe copiarse a la salida " +
                "(ver CaeManager.IntegrationTests.csproj): sin él, los tests migrarían contra un " +
                "clúster sin los roles del contrato.");

        // Conexión sin pool y a la base de mantenimiento: esto ocurre una sola
        // vez y no debe dejar conexiones vivas compitiendo con las de la suite,
        // que ya vive al límite de max_connections.
        var cadena = BaseDatosPostgresDePruebas.CadenaDeMantenimientoSinPool();

        using var conexion = new NpgsqlConnection(cadena);
        conexion.Open();
        using var comando = new NpgsqlCommand(File.ReadAllText(guion), conexion);
        comando.ExecuteNonQuery();

        // Y, SOLO en el clúster de pruebas, se habilita el LOGIN de
        // cae_app_runtime. Sirve para que los tests puedan conectar realmente
        // como ese rol en vez de adoptarlo con SET ROLE desde el propietario:
        // SET ROLE demuestra que las políticas se aplican, pero parte de una
        // sesión que ya entró como superusuario; una conexión de login reproduce
        // además la autenticación y los privilegios efectivos, que es lo que
        // hace producción.
        //
        // Va DESPUÉS del guion a propósito. Antes de #256 daba igual el orden y
        // el resultado era el mismo: el bootstrap convergía ese rol a NOLOGIN en
        // cada ejecución y le habría retirado el LOGIN de todas formas. Que esto
        // funcione hoy es una consecuencia directa de aquella corrección.
        //
        // La contraseña es fija y está en claro porque no protege nada: este
        // clúster ya usa postgres/postgres y no contiene datos reales.
        using var habilitarLogin = new NpgsqlCommand(
            $"ALTER ROLE cae_app_runtime LOGIN PASSWORD '{BaseDatosPostgresDePruebas.ContrasenaRuntimeDePruebas}';",
            conexion);
        habilitarLogin.ExecuteNonQuery();
    }
}
