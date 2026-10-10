using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace CaeManager.IntegrationTests;

/// <summary>
/// Base plantilla ya migrada, de la que se clona la base de cada test con
/// <c>CREATE DATABASE … TEMPLATE</c> en vez de crearla vacía y aplicarle todas
/// las migraciones. La usa <see cref="BaseDatosPostgresDePruebas.CadenaConexionUnica"/>;
/// ningún test la llama directamente.
///
/// <para>
/// <b>Qué garantiza y dónde se demuestra.</b> Que un clon equivale a una base
/// migrada de verdad no se supone: lo exige
/// <c>EquivalenciaDelClonConLaBaseMigradaTests</c> comparando los catálogos de
/// las dos, incluido lo que <c>TEMPLATE</c> NO copia (ajustes y permisos a nivel
/// de base). Si una migración futura añade un <c>ALTER DATABASE … SET</c> o un
/// <c>GRANT … ON DATABASE</c>, ese test cae en rojo; no queda un clon distinto en
/// silencio.
/// </para>
///
/// <para>
/// <b>El nombre de la plantilla es su contenido.</b> Lleva un resumen del guion
/// SQL completo que generan las migraciones (el mismo que ejecuta
/// <c>MigrateAsync</c>), de la versión del proveedor que lo genera y del guion de
/// roles de clúster, más el mes UTC en curso. Cambiar una migración cambia el
/// nombre y, con él, la plantilla: no hay forma de clonar una plantilla vieja con
/// migraciones nuevas. El mes entra porque la línea base crea las particiones
/// mensuales de la auditoría a partir de <c>now()</c> (ver
/// <c>ParticionadoMensualEventos</c>): una base migrada en noviembre no tiene las
/// mismas tablas que una migrada en octubre.
/// </para>
///
/// <para>
/// <b>Una plantilla a medias no se usa nunca.</b> Se migra con un nombre
/// provisional y solo al terminar se cierra a conexiones y se renombra al nombre
/// definitivo, que es el único que se clona. El renombrado es una sola
/// actualización de catálogo: o existe la plantilla entera o no existe. Todo ello
/// ocurre con un cerrojo consultivo de sesión tomado en la base <c>postgres</c>,
/// así que entre procesos (varios worktrees comparten el clúster local, y en CI
/// hay varios hilos) la migración de la plantilla corre una sola vez; si el
/// proceso que la construía muere, PostgreSQL suelta el cerrojo al cerrarse su
/// conexión y el siguiente borra la provisional abandonada y empieza de cero.
/// </para>
///
/// <para>
/// <b>Nadie puede modificarla.</b> <c>ALLOW_CONNECTIONS false</c> impide cualquier
/// conexión a la plantilla, también de un superusuario; <c>CREATE DATABASE …
/// TEMPLATE</c> solo la lee. Es además condición del propio clonado, que falla si
/// hay alguien conectado al origen.
/// </para>
///
/// <para>
/// <b>Las plantillas persisten</b> entre ejecuciones locales a propósito (en CI
/// cada bloque estrena PostgreSQL). Quien construye una plantilla retira, con el
/// cerrojo tomado, las provisionales abandonadas y las plantillas de meses
/// anteriores, que ya nadie puede pedir. Las del mes en curso con otro contenido
/// (otra rama con otras migraciones) no se tocan: pueden estar en uso por otro
/// worktree. Se listan con
/// <c>SELECT datname FROM pg_database WHERE datname LIKE 'caemanager_plant%';</c>
/// y borrarlas a mano es seguro: quien la necesite la reconstruye.
/// </para>
/// </summary>
internal sealed class PlantillaMigradaDePruebas
{
    /// <summary>
    /// Prefijo propio, distinto del de las bases de test (<c>caemanager_tests_</c>):
    /// una limpieza de huérfanas por ese prefijo no debe llevarse la plantilla.
    /// </summary>
    internal const string Prefijo = "caemanager_plantilla_";

    /// <summary>Del mismo largo que <see cref="Prefijo"/>: el nombre provisional cabe donde quepa el definitivo.</summary>
    internal const string PrefijoProvisional = "caemanager_plantprov_";

    /// <summary>
    /// Se sube si cambia la forma de construir la plantilla (no su contenido),
    /// para que las construidas con el procedimiento anterior dejen de usarse.
    /// </summary>
    private const string VersionDelProcedimiento = "1";

    /// <summary>
    /// Clave del cerrojo consultivo, la misma para todas las plantillas: quien lo
    /// tiene sabe que nadie más está construyendo, y por eso puede dar por
    /// abandonada cualquier provisional que encuentre. Los cerrojos consultivos son
    /// locales a la base; todos se toman en <c>postgres</c>.
    /// </summary>
    private const string SqlClaveDelCerrojo = "hashtextextended('caemanager_tests:plantilla_migrada', 0)";

    /// <summary>SQLSTATE de «la base plantilla no existe» (<c>invalid_catalog_name</c>).</summary>
    private const string PlantillaInexistente = "3D000";

    /// <summary>SQLSTATE de «hay sesiones conectadas a la base origen» (<c>object_in_use</c>).</summary>
    private const string OrigenEnUso = "55006";

    private static readonly Lazy<string> ResumenDeLasMigraciones = new(CalcularResumenDeLasMigraciones);

    /// <summary>La plantilla de la suite: la que clona <c>CadenaConexionUnica</c>.</summary>
    internal static PlantillaMigradaDePruebas DeLaSuite { get; } = new(discriminador: "");

    private readonly object _cerrojoDelProceso = new();
    private readonly string _discriminador;
    private readonly Func<string, Task> _migrar;
    private string? _nombreAsegurado;

    /// <param name="discriminador">
    /// Vacío para la plantilla de la suite. Los tests del propio procedimiento
    /// pasan uno suyo para construir, abandonar y borrar plantillas privadas sin
    /// tocar la que están clonando las demás clases en paralelo.
    /// </param>
    /// <param name="migrar">
    /// Cómo se migra la base provisional. Por defecto, igual que un test:
    /// <see cref="BaseDatosPostgresDePruebas.MigrarAsync"/>.
    /// </param>
    internal PlantillaMigradaDePruebas(string discriminador, Func<string, Task>? migrar = null)
    {
        _discriminador = discriminador;
        _migrar = migrar ?? BaseDatosPostgresDePruebas.MigrarAsync;
    }

    /// <summary>Veces que ESTA instancia ha migrado una plantilla. Para los tests del procedimiento.</summary>
    internal int PlantillasConstruidas { get; private set; }

    /// <summary>
    /// Nombre de la plantilla para un contenido y un mes dados. Función pura: es lo
    /// que hace imposible reutilizar una plantilla con otras migraciones.
    /// </summary>
    internal static string NombreDePlantilla(string resumenDelContenido, string mesUtc, string discriminador = "") =>
        $"{Prefijo}{discriminador}{resumenDelContenido}_{mesUtc}";

    /// <summary>Nombre con el que se migra la plantilla antes de darla por completa.</summary>
    internal static string NombreProvisionalDe(string nombreDePlantilla) =>
        PrefijoProvisional + nombreDePlantilla[Prefijo.Length..];

    /// <summary>
    /// Resumen de todo lo que decide el contenido de una base recién migrada.
    /// </summary>
    internal static string Resumir(string guionDeMigraciones, string versionesDelProveedor, string guionDeRoles)
    {
        var entrada = string.Join(
            "\n--8<--\n", VersionDelProcedimiento, versionesDelProveedor, guionDeRoles, guionDeMigraciones);
        var resumen = SHA256.HashData(Encoding.UTF8.GetBytes(entrada));
        return Convert.ToHexString(resumen, 0, 10).ToLowerInvariant();
    }

    /// <summary>
    /// Crea la base <paramref name="nombreDeLaBase"/> como clon de la plantilla
    /// vigente, construyéndola antes si no existe. Síncrono a propósito: se llama
    /// desde inicializadores de campo de las clases de test.
    /// </summary>
    internal void Clonar(string cadenaDeLaBase)
    {
        var constructor = new NpgsqlConnectionStringBuilder(cadenaDeLaBase);
        var nombreDeLaBase = constructor.Database!;

        // La misma cadena que usa EliminarAsync para el DROP: comparten pool, así
        // que clonar no añade conexiones a las que el teardown ya mantenía.
        constructor.Database = "postgres";
        using var conexion = new NpgsqlConnection(constructor.ConnectionString);
        conexion.Open();

        var reconstruida = false;
        var esperasPorOrigenEnUso = 0;
        while (true)
        {
            var plantilla = Asegurar(conexion);
            try
            {
                // WAL_LOG (el valor por defecto desde PostgreSQL 15) copia bloque
                // a bloque sin forzar checkpoints; FILE_COPY forzaría dos por clon.
                Ejecutar(conexion, $"CREATE DATABASE \"{nombreDeLaBase}\" TEMPLATE \"{plantilla}\" STRATEGY = WAL_LOG;");
                return;
            }
            catch (PostgresException ex) when (ex.SqlState == PlantillaInexistente && !reconstruida)
            {
                // La borraron entre asegurarla y clonarla (limpieza manual, o el
                // cambio de mes retiró la del mes anterior): se reconstruye una vez.
                reconstruida = true;
                lock (_cerrojoDelProceso)
                    _nombreAsegurado = null;
            }
            catch (PostgresException ex) when (ex.SqlState == OrigenEnUso && esperasPorOrigenEnUso < 3)
            {
                // Solo puede ser un proceso de autovacuum dentro de la plantilla;
                // PostgreSQL lo cancela él mismo y basta con volver a pedirlo.
                esperasPorOrigenEnUso++;
                Thread.Sleep(200 * esperasPorOrigenEnUso);
            }
        }
    }

    /// <summary>
    /// Devuelve el nombre de la plantilla vigente, que existe y está completa.
    /// </summary>
    internal string Asegurar(NpgsqlConnection mantenimiento)
    {
        var (nombre, mesUtc) = NombreYMesVigentes(mantenimiento);

        lock (_cerrojoDelProceso)
        {
            if (_nombreAsegurado == nombre)
                return nombre;

            ConstruirSiFalta(nombre, mesUtc);
            _nombreAsegurado = nombre;
            return nombre;
        }
    }

    /// <summary>El nombre que tiene (o tendrá) la plantilla vigente, sin construir nada.</summary>
    internal string NombreVigente(NpgsqlConnection mantenimiento) => NombreYMesVigentes(mantenimiento).Nombre;

    private (string Nombre, string MesUtc) NombreYMesVigentes(NpgsqlConnection mantenimiento)
    {
        // El mes lo da el reloj del servidor, que es el que usa now() al migrar.
        var mesUtc = (string)Escalar(mantenimiento, "SELECT to_char(now() AT TIME ZONE 'UTC', 'YYYYMM');")!;
        var nombre = NombreDePlantilla(ResumenDeLasMigraciones.Value, mesUtc, _discriminador);
        if (nombre.Length > 63)
            throw new InvalidOperationException(
                $"El nombre de plantilla «{nombre}» pasa de 63 caracteres y PostgreSQL lo truncaría.");

        return (nombre, mesUtc);
    }

    private void ConstruirSiFalta(string nombre, string mesUtc)
    {
        // Conexión propia y sin pool: el cerrojo es de sesión y tiene que morir
        // con ella si este proceso muere a mitad.
        using var cerrojo = new NpgsqlConnection(BaseDatosPostgresDePruebas.CadenaDeMantenimientoSinPool());
        cerrojo.Open();
        Ejecutar(cerrojo, $"SELECT pg_advisory_lock({SqlClaveDelCerrojo});", esperaEnSegundos: 1800);

        if (Existe(cerrojo, nombre))
            return;

        RetirarLoQueYaNadiePuedeUsar(cerrojo, mesUtc);

        var provisional = NombreProvisionalDe(nombre);
        var cadenaProvisional =
            new NpgsqlConnectionStringBuilder(BaseDatosPostgresDePruebas.CadenaDeMantenimientoSinPool())
            {
                Database = provisional,
            }.ConnectionString;

        // Fuera del contexto de sincronización de xUnit: este hilo se queda
        // bloqueado esperando, y con los demás hilos del test esperando a su vez el
        // cerrojo del proceso no quedaría ninguno para ejecutar las continuaciones.
        Task.Run(() => _migrar(cadenaProvisional)).GetAwaiter().GetResult();
        PlantillasConstruidas++;

        // Cerrada a conexiones, lo único que entra después en la plantilla es el
        // autovacuum, y lo único que escribe son las estadísticas del planificador
        // de los catálogos compartidos que le toque analizar estando dentro
        // (pg_statistic): ni el esquema ni los datos.
        Ejecutar(cerrojo, $"ALTER DATABASE \"{provisional}\" ALLOW_CONNECTIONS false;");
        Ejecutar(cerrojo, $"ALTER DATABASE \"{provisional}\" RENAME TO \"{nombre}\";");
    }

    /// <summary>
    /// Con el cerrojo tomado: toda provisional está abandonada (quien construye
    /// tiene el cerrojo) y toda plantilla de un mes anterior es inalcanzable (el
    /// nombre que se pide lleva el mes en curso). Lo demás no se toca.
    /// </summary>
    private static void RetirarLoQueYaNadiePuedeUsar(NpgsqlConnection cerrojo, string mesUtc)
    {
        var candidatas = new List<string>();
        using (var comando = new NpgsqlCommand(
                   "SELECT datname FROM pg_database WHERE datname LIKE @provisionales OR datname LIKE @plantillas;",
                   cerrojo))
        {
            comando.Parameters.AddWithValue("provisionales", EscaparLike(PrefijoProvisional) + "%");
            comando.Parameters.AddWithValue("plantillas", EscaparLike(Prefijo) + "%");
            using var lector = comando.ExecuteReader();
            while (lector.Read())
                candidatas.Add(lector.GetString(0));
        }

        foreach (var candidata in candidatas)
        {
            if (!candidata.StartsWith(PrefijoProvisional, StringComparison.Ordinal) && !EsDeUnMesAnterior(candidata, mesUtc))
                continue;

            try
            {
                Ejecutar(cerrojo, $"DROP DATABASE IF EXISTS \"{candidata}\" WITH (FORCE);", esperaEnSegundos: 60);
            }
            catch (NpgsqlException)
            {
                // La retirada es de mejor esfuerzo: si alguien la está clonando
                // en este instante, la retirará el siguiente que construya.
            }
        }
    }

    internal static bool EsDeUnMesAnterior(string nombreDePlantilla, string mesUtc)
    {
        var separador = nombreDePlantilla.LastIndexOf('_');
        if (separador < 0)
            return false;

        var mes = nombreDePlantilla[(separador + 1)..];
        return mes.Length == 6
            && mes.All(char.IsAsciiDigit)
            && string.CompareOrdinal(mes, mesUtc) < 0;
    }

    private static string CalcularResumenDeLasMigraciones()
    {
        // GenerateScript no abre ninguna conexión: la cadena solo configura el proveedor.
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(
                "Host=no-se-conecta;Database=no-se-conecta",
                npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .Options;
        using var contexto = new CaeManagerDbContext(
            opciones, new EphemeralDataProtectionProvider(), new TenantActualAmbiental());

        var guion = contexto.GetService<IMigrator>().GenerateScript();
        var versiones = string.Join(
            ";",
            new[] { typeof(DbContext), typeof(NpgsqlDbContextOptionsBuilderExtensions), typeof(NpgsqlConnection) }
                .Select(tipo => tipo.Assembly.GetName())
                .Select(nombre => string.Create(CultureInfo.InvariantCulture, $"{nombre.Name}={nombre.Version}")));
        var roles = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "roles-de-cluster.sql"));

        return Resumir(guion, versiones, roles);
    }

    private static bool Existe(NpgsqlConnection conexion, string baseDeDatos)
    {
        using var comando = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @nombre;", conexion);
        comando.Parameters.AddWithValue("nombre", baseDeDatos);
        return comando.ExecuteScalar() is not null;
    }

    private static string EscaparLike(string literal) => literal.Replace("_", "\\_", StringComparison.Ordinal);

    private static object? Escalar(NpgsqlConnection conexion, string sql)
    {
        using var comando = new NpgsqlCommand(sql, conexion);
        return comando.ExecuteScalar();
    }

    private static void Ejecutar(NpgsqlConnection conexion, string sql, int esperaEnSegundos = 120)
    {
        using var comando = new NpgsqlCommand(sql, conexion) { CommandTimeout = esperaEnSegundos };
        comando.ExecuteNonQuery();
    }
}
