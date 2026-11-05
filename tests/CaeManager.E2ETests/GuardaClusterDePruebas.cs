using System.Net;

namespace CaeManager.E2ETests;

/// <summary>
/// Guarda que decide si <c>WebAppFixture</c> puede modificar el rol de clúster <c>cae_app_runtime</c>
/// (darle LOGIN con la contraseña fija de pruebas). Un rol es un objeto de clúster: el cambio alcanza a todas las
/// bases del servidor, así que antes de hacerlo hay que saber que el servidor es un clúster de pruebas desechable.
///
/// <para>
/// <b>Por qué no basta con el estado del rol</b> (revisión de Codex en la PR #2032). <c>NOLOGIN</c> y sin contraseña
/// es el estado que deja <c>deploy/bootstrap/roles-de-cluster.sql</c> en CUALQUIER clúster recién preparado, también en
/// staging o producción. Si <c>CAEMANAGER_TESTS_PG</c> apuntara por error a uno de ellos, la fixture le abriría el rol
/// de runtime con una contraseña pública.
/// </para>
///
/// <para>
/// <b>Identificación explícita y verificable.</b> Quien prepara el clúster declara que es desechable poniendo en
/// <see cref="VariableDeEntorno"/> el <c>system_identifier</c> de ese clúster (<c>pg_control_system()</c>, un número de
/// 64 bits que <c>initdb</c> genera al azar y que no cambia mientras el clúster exista). La fixture lo vuelve a leer del
/// clúster al que de verdad está conectada y solo sigue si coinciden: la declaración vale para ESE clúster y no para
/// cualquiera al que apunte la cadena. En CI lo pone el paso «Identificar el clúster de pruebas desechable» del job de
/// E2E sobre el contenedor de servicio que el propio job crea. Además, el servidor tiene que ser local (bucle local):
/// un anfitrión remoto se rechaza aunque la declaración coincida.
/// </para>
///
/// <para>
/// Sin declaración, con una que no coincide o con un anfitrión que no es local, se lanza antes de tocar el rol. La
/// variable no admite comodines, y la fixture solo entrega la conexión sobre la que va a ejecutar el <c>ALTER ROLE</c>:
/// la declaración, los anfitriones y el identificador los obtiene la guarda, no quien la llama.
/// </para>
///
/// <para>
/// <b>Lo que NO garantiza.</b> El <c>system_identifier</c> identifica el linaje del clúster, no la instancia: lo
/// comparten sus copias físicas (<c>pg_basebackup</c>, réplicas, restauración de un volumen). Una copia local de un
/// clúster real tiene su mismo identificador, y declararla desechable no la convierte en una de pruebas; la guarda
/// protege del error (una cadena que apunta donde no debía), no de una declaración deliberada sobre esa copia.
/// </para>
/// </summary>
internal static class GuardaClusterDePruebas
{
    /// <summary>Variable de entorno con el <c>system_identifier</c> del clúster declarado desechable.</summary>
    internal const string VariableDeEntorno = "CAEMANAGER_TESTS_CLUSTER_DESECHABLE";

    /// <summary>Consulta que devuelve el identificador del clúster conectado (necesita superusuario o <c>pg_monitor</c>).</summary>
    internal const string ConsultaIdentificador = "SELECT system_identifier::text FROM pg_control_system();";

    /// <summary>
    /// Lanza salvo que la conexión vaya a un clúster local declarado desechable en <see cref="VariableDeEntorno"/>. Lee el
    /// identificador por la misma <paramref name="conexion"/> (y <paramref name="transaccion"/>) sobre la que se va a
    /// ejecutar el <c>ALTER ROLE</c>, y los anfitriones de su cadena.
    /// </summary>
    internal static async Task ExigirClusterDesechableAsync(Npgsql.NpgsqlConnection conexion, Npgsql.NpgsqlTransaction transaccion)
    {
        string? identificadorDelCluster;
        await using (var consulta = new Npgsql.NpgsqlCommand(ConsultaIdentificador, conexion, transaccion))
            identificadorDelCluster = await consulta.ExecuteScalarAsync() as string;

        ExigirClusterDesechable(
            Environment.GetEnvironmentVariable(VariableDeEntorno), conexion.ConnectionString, identificadorDelCluster);
    }

    /// <summary>
    /// Lanza <see cref="InvalidOperationException"/> salvo que <paramref name="declaracion"/> sea exactamente
    /// <paramref name="identificadorDelCluster"/> y todos los anfitriones de <paramref name="cadenaConexion"/> sean de
    /// bucle local. Es la decisión pura, separada para poder probarla sin servidor.
    /// </summary>
    internal static void ExigirClusterDesechable(string? declaracion, string cadenaConexion, string? identificadorDelCluster)
    {
        const string ComoPreparar =
            "Prepara un clúster de pruebas local con deploy/bootstrap/roles-de-cluster.sql y declara que es desechable con " +
            VariableDeEntorno + "=<salida de «" + ConsultaIdentificador + "» en ese clúster> (ci.yml lo hace en «Identificar " +
            "el clúster de pruebas desechable»). La fixture no ha modificado ningún rol.";

        if (string.IsNullOrWhiteSpace(declaracion))
            throw new InvalidOperationException(
                $"No hay declaración de clúster de pruebas desechable ({VariableDeEntorno} vacía): la fixture no da LOGIN " +
                $"a cae_app_runtime en un clúster que no está identificado como de pruebas. {ComoPreparar}");

        var anfitriones = AnfitrionesDe(cadenaConexion);
        var remotos = anfitriones.Where(a => !EsBucleLocal(a)).ToList();
        if (anfitriones.Count == 0 || remotos.Count > 0)
            throw new InvalidOperationException(
                $"El servidor PostgreSQL de los E2E no es local (anfitriones: «{string.Join(", ", anfitriones)}»): la fixture " +
                $"solo modifica roles en un clúster de pruebas en localhost, 127.0.0.1 o [::1]. {ComoPreparar}");

        if (string.IsNullOrWhiteSpace(identificadorDelCluster)
            || !string.Equals(declaracion.Trim(), identificadorDelCluster.Trim(), StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"{VariableDeEntorno} declara desechable el clúster «{declaracion.Trim()}», pero la fixture está conectada al " +
                $"clúster «{identificadorDelCluster}»: CAEMANAGER_TESTS_PG apunta a otro clúster. {ComoPreparar}");
    }

    /// <summary>Anfitriones de la cadena (Npgsql admite varios separados por comas, cada uno con puerto opcional).</summary>
    internal static IReadOnlyList<string> AnfitrionesDe(string cadenaConexion)
    {
        var host = new Npgsql.NpgsqlConnectionStringBuilder(cadenaConexion).Host;
        return string.IsNullOrWhiteSpace(host)
            ? []
            : host.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// <c>localhost</c> o una dirección IP de bucle local, con o sin puerto. Una ruta de socket Unix o cualquier
    /// nombre de máquina distinto de <c>localhost</c> no lo es.
    /// </summary>
    internal static bool EsBucleLocal(string anfitrion)
    {
        var nombre = anfitrion.Trim();
        if (nombre.StartsWith('['))
        {
            var cierre = nombre.IndexOf(']');
            if (cierre < 0) return false;
            var resto = nombre[(cierre + 1)..];
            if (resto.Length > 0 && !(resto[0] == ':' && resto.Length > 1 && resto[1..].All(char.IsAsciiDigit))) return false;
            nombre = nombre[1..cierre];
        }
        else if (nombre.Count(c => c == ':') == 1)
        {
            var puerto = nombre[(nombre.IndexOf(':') + 1)..];
            if (puerto.Length == 0 || !puerto.All(char.IsAsciiDigit)) return false;
            nombre = nombre[..nombre.IndexOf(':')];
        }

        if (string.Equals(nombre, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(nombre, out var ip) && IPAddress.IsLoopback(ip);
    }
}
