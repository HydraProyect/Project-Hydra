using CaeManager.Application.Common;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace CaeManager.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Segunda línea de aislamiento por tenant, bajo el filtro global de EF Core
/// (ver docs/MULTITENANCY.md § 4.2 y RUNBOOK-RLS.md). En cada apertura de
/// conexión Npgsql fija la variable de sesión <c>app.tenant_id</c>, que las
/// políticas de Row-Level Security creadas por la migración
/// <c>HabilitarRlsPostgres</c> usan para filtrar filas incluso si una
/// consulta se saltara el <c>HasQueryFilter</c> (<c>IgnoreQueryFilters</c>
/// mal revisado, SQL crudo, un bug del propio EF).
///
/// Sin tenant resuelto (login, operaciones sobre tablas de Identity sin
/// filtro) se fija una cadena vacía a propósito:
/// <c>NULLIF(current_setting(...), '')::uuid</c> da <c>NULL</c>, y
/// <c>NULL</c> nunca iguala ningún <c>TenantId</c> real en la política —
/// fallo cerrado, el mismo principio que <see cref="ITenantActual.TenantId"/>
/// devolviendo <c>null</c>.
///
/// RLS solo restringe roles que no son propietarios de la tabla ni
/// superusuario. Mientras la conexión de la aplicación siga usando el rol
/// propietario (el caso de hoy, ver RUNBOOK-RLS.md), esta variable se fija
/// igual pero Postgres no la usa para nada — queda inerte hasta que se rota
/// la conexión de runtime al rol restringido <c>cae_app_runtime</c>.
///
/// <c>set_config</c> se invoca como función parametrizada (no
/// <c>SET app.tenant_id = '...'</c> interpolado) para no construir SQL a
/// partir de un valor variable, aunque hoy ese valor sea siempre un
/// <see cref="Guid"/> generado por el propio sistema.
///
/// <para>
/// <b>Este interceptor aplica DOS garantías distintas, y conviene no
/// confundirlas.</b> Comparten el sitio —la apertura de la conexión— y nada
/// más:
/// </para>
/// <list type="table">
/// <item>
/// <term>A — aislamiento entre tenants</term>
/// <description>
/// <c>app.tenant_id</c> + las políticas RLS. Responde "¿de quién son estas
/// filas?". Aplica a <b>todas</b> las conexiones, siempre, con sesión
/// privilegiada o sin ella.
/// </description>
/// </item>
/// <item>
/// <term>B — restricción de capacidad de la sesión de soporte</term>
/// <description>
/// <c>SET ROLE cae_app_soporte</c>. Responde "¿esta conexión puede escribir?".
/// Aplica solo cuando la petición viene por una sesión privilegiada de
/// plataforma.
/// </description>
/// </list>
/// <para>
/// <b>Ninguna sustituye a la otra, en ninguna dirección.</b> RLS no distingue
/// leer de escribir: un operador legítimo del tenant escribe en sus propias
/// filas y eso es correcto, así que RLS no puede ser el solo-lectura del
/// soporte. Y el rol de soporte no aísla nada por sí mismo: si se hubiera
/// creado sin <c>NOBYPASSRLS</c>, sería un rol de solo lectura capaz de leer
/// <i>todos</i> los tenants — la garantía B habría comprado una fuga de la
/// garantía A mucho peor que la escritura que impide. Por eso
/// <c>NOBYPASSRLS</c> no es una opción de endurecimiento entre otras: es lo que
/// mantiene la segunda garantía subordinada a la primera en vez de enfrentada
/// a ella.
/// </para>
/// </summary>
public class TenantRlsConnectionInterceptor(
    ITenantActual tenantActual,
    IClienteActivoSeleccionado clienteActivoSeleccionado,
    ICurrentUserService currentUserService) : DbConnectionInterceptor
{
    /// <summary>
    /// Rol de solo lectura del plano 3 (ver la migración
    /// <c>RolSoporteSoloLectura</c>). Literal y no configurable: es el nombre
    /// que crea esa migración, y un valor configurable solo añadiría una forma
    /// de desactivar el control por despiste.
    /// </summary>
    private const string RolSoporte = "cae_app_soporte";

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await PrepararSesionAsync((NpgsqlConnection)connection, cancellationToken);
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        PrepararSesionAsync((NpgsqlConnection)connection, CancellationToken.None)
            .GetAwaiter().GetResult();
        base.ConnectionOpened(connection, eventData);
    }

    /// <summary>
    /// El orden importa: primero el tenant, después el rol. <c>SET ROLE</c>
    /// cambia a un rol sin permisos de escritura, y <c>set_config</c> con
    /// <c>false</c> escribe en la sesión — hacerlo al revés funcionaría hoy
    /// (fijar una variable de sesión no requiere privilegios sobre tablas),
    /// pero deja el orden dependiendo de un detalle de Postgres en vez de de
    /// una decisión. Primero se establece el contexto, luego se cierra la
    /// puerta.
    /// </summary>
    private async Task PrepararSesionAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await FijarVariablesDeSesionAsync(connection, cancellationToken);

        if (clienteActivoSeleccionado.SesionPrivilegiadaIdSeleccionada is not null)
            await AdoptarRolDeSoporteAsync(connection, cancellationToken);
    }

    /// <summary>
    /// Las tres coordenadas de sesión (mejora 🟠 #1 de la auditoría del Módulo 1,
    /// 2026-08-30: rendimiento en el camino más crítico de seguridad) en un único
    /// <c>set_config</c> por variable pero un único viaje de ida y vuelta a
    /// Postgres, en vez de tres comandos separados. Antes eran tres
    /// <c>ExecuteNonQueryAsync</c> independientes — uno por variable— en cada
    /// apertura de conexión; ahora es uno solo con tres parámetros. El único
    /// comando que queda fuera es <c>SET ROLE</c> (<see cref="AdoptarRolDeSoporteAsync"/>):
    /// no admite parámetro y tiene que ir después de estas tres por el orden que
    /// documenta <see cref="PrepararSesionAsync"/>, así que combinarlo aquí no
    /// ahorraría un viaje — seguiría siendo un segundo comando — y sí complicaría
    /// esta consulta con una rama condicional dentro de una sola cadena SQL.
    ///
    /// <para>
    /// <b><c>app.tenant_id</c></b>: el tenant activo. Sin tenant resuelto (login,
    /// operaciones sobre tablas de Identity sin filtro) se fija una cadena vacía
    /// a propósito — <c>NULLIF(current_setting(...), '')::uuid</c> da <c>NULL</c>,
    /// y <c>NULL</c> nunca iguala ningún <c>TenantId</c> real en la política:
    /// fallo cerrado, el mismo principio que <see cref="ITenantActual.TenantId"/>
    /// devolviendo <c>null</c>.
    /// </para>
    ///
    /// <para>
    /// <b><c>app.tenant_origen_id</c></b>: segunda variable de sesión, para los
    /// catálogos globales de asignación. <c>app.tenant_id</c> no sirve para
    /// ellos: refleja el workspace <b>activo</b>, que dentro de un workspace
    /// delegado es el del propietario. Un operador que quisiera ver "mis
    /// workspaces" —las asignaciones donde él es el operador— no encontraría
    /// ninguna, porque su propio tenant no es el que está fijado. Por eso hacen
    /// falta las dos: una dice desde dónde se opera, la otra qué se está
    /// operando. Sale del claim de sesión (<c>ObtenerTenantOrigenIdAsync</c>),
    /// que es el tenant al que el usuario pertenece y lo único que la selección
    /// de workspace no puede cambiar. Leer el claim no toca la base de datos, así
    /// que no hay reentrancia con la conexión que se está abriendo. Cadena vacía
    /// cuando no hay usuario, igual que la anterior — fallo cerrado.
    /// </para>
    ///
    /// <para>
    /// <b><c>app.usuario_id</c></b>: tercera coordenada de sesión, quién. La usan
    /// las políticas del plano de privilegio de plataforma para acotar cada fila
    /// a quien la nombra. Es una coordenada, no una capacidad: la identidad
    /// autenticada, igual que <c>app.tenant_id</c> es el tenant activo. Cadena
    /// vacía sin usuario (jobs de fondo, siembra al arrancar): las políticas del
    /// plano 3 no encuentran ninguna fila, que es lo correcto.
    /// </para>
    ///
    /// <c>set_config</c> se invoca como función parametrizada (no
    /// <c>SET app.x = '...'</c> interpolado) para no construir SQL a partir de un
    /// valor variable, aunque hoy ese valor sea siempre un <see cref="Guid"/>
    /// generado por el propio sistema.
    /// </summary>
    private async Task FijarVariablesDeSesionAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var tenantId = tenantActual.TenantId;
        var tenantOrigenId = await currentUserService.ObtenerTenantOrigenIdAsync();
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();

        await using var comando = connection.CreateCommand();
        comando.CommandText =
            "SELECT set_config('app.tenant_id', @tenantId, false), " +
            "set_config('app.tenant_origen_id', @tenantOrigenId, false), " +
            "set_config('app.usuario_id', @usuarioId, false);";
        comando.Parameters.AddWithValue("tenantId", tenantId?.ToString() ?? string.Empty);
        comando.Parameters.AddWithValue("tenantOrigenId", tenantOrigenId?.ToString() ?? string.Empty);
        comando.Parameters.AddWithValue("usuarioId", usuarioId?.ToString() ?? string.Empty);
        await comando.ExecuteNonQueryAsync(cancellationToken);
    }

    public override async ValueTask<InterceptionResult> ConnectionClosingAsync(
        DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        await DevolverRolAsync((NpgsqlConnection)connection, CancellationToken.None);
        return await base.ConnectionClosingAsync(connection, eventData, result);
    }

    public override InterceptionResult ConnectionClosing(
        DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        DevolverRolAsync((NpgsqlConnection)connection, CancellationToken.None).GetAwaiter().GetResult();
        return base.ConnectionClosing(connection, eventData, result);
    }

    /// <summary>
    /// Deshace el <c>SET ROLE</c> antes de que la conexión vuelva al pool.
    ///
    /// Npgsql ya descarta el estado de sesión al devolverla —es lo mismo que
    /// obliga a fijar <c>app.tenant_id</c> en cada apertura—, así que esto es
    /// redundante <b>mientras nadie ponga <c>No Reset On Close=true</c> en la
    /// cadena de conexión</b>. Ese ajuste existe, es legítimo como optimización,
    /// y quien lo activara no tendría por qué saber que además está desarmando
    /// un control de seguridad. Una conexión que volviera al pool con el rol de
    /// soporte puesto rompería la escritura del siguiente que la tomara.
    ///
    /// Se paga solo cuando se puso: la condición es la misma que la de
    /// <c>SET ROLE</c>, así que ninguna petición normal gasta un viaje extra.
    /// </summary>
    private async Task DevolverRolAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        if (clienteActivoSeleccionado.SesionPrivilegiadaIdSeleccionada is null) return;
        if (connection.State != System.Data.ConnectionState.Open) return;

        await using var comando = connection.CreateCommand();
        comando.CommandText = "RESET ROLE;";
        await comando.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Enforcement de solo lectura del plano 3 en la capa de datos (ADR-011
    /// § 4bis.7.4): mientras la petición venga por una sesión privilegiada, la
    /// conexión adopta un rol que no tiene <c>INSERT</c>, <c>UPDATE</c> ni
    /// <c>DELETE</c> sobre nada. Deja de importar por dónde intente escribir el
    /// código — MediatR, un repositorio suelto, SQL crudo: falla en Postgres.
    ///
    /// <b>Se decide con el token, sin consultar la base</b>, y aquí no es una
    /// concesión sino la única opción correcta. Consultar la sesión
    /// privilegiada exigiría una consulta... sobre la conexión que se está
    /// abriendo en este mismo momento, que es reentrante. Y no hace falta: esta
    /// decisión solo <i>quita</i> capacidad. Un token que mienta al declarar
    /// sesión no gana nada — se queda con una conexión de solo lectura. Quien
    /// decide si la sesión vale de verdad sigue siendo
    /// <c>ISesionPrivilegiadaActual</c>, que revalida contra la base; si no
    /// vale, la revalidación por petición tumba la selección entera y el
    /// contexto cae al tenant propio del usuario.
    ///
    /// <b>Fallo cerrado si el rol no existe o no se puede adoptar.</b> Se deja
    /// propagar la excepción de Postgres a propósito. Un entorno que no haya
    /// concedido la membresía (<c>GRANT cae_app_soporte TO &lt;rol de
    /// login&gt;</c>) verá fallar las peticiones de soporte, que es ruidoso y
    /// arreglable; tragarse el error dejaría la sesión de soporte corriendo con
    /// permisos de escritura completos y sin que nada lo dijera. De las dos
    /// formas de equivocarse, solo una es reversible.
    ///
    /// El rol se devuelve al cerrar (ver <see cref="ConnectionClosingAsync"/>).
    /// </summary>
    private static async Task AdoptarRolDeSoporteAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var comando = connection.CreateCommand();
        // Identificador fijo del código, no un valor de entrada: no hay
        // parámetro que valga para SET ROLE y tampoco hace falta ninguno.
        comando.CommandText = $"SET ROLE {RolSoporte};";
        await comando.ExecuteNonQueryAsync(cancellationToken);
    }
}
