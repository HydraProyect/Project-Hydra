using CaeManager.Application.Common;
using CaeManager.Infrastructure.Persistence.ContextoRls;
using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace CaeManager.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Segunda línea de aislamiento por tenant, bajo el filtro global de EF Core
/// (ver Project-Hydra-Negocio/tecnico/docs/MULTITENANCY.md § 4.2 y Project-Hydra-Negocio/tecnico/RUNBOOK-RLS.md). En cada apertura de
/// conexión Npgsql, y en cada realineación de una conexión que sigue abierta
/// (retenida por la operación de <c>PuertaAccesoDatos</c>), fija la variable de sesión <c>app.tenant_id</c>, que las
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
/// superusuario. La conexión de tráfico de la aplicación ya usa el rol
/// restringido <c>cae_app_runtime</c> (<c>InfrastructureServiceCollectionExtensions.ResolverCadenaDeTrafico</c>,
/// que aborta el arranque fuera de Development si falta
/// <c>ConnectionStrings:CaeManagerDbRuntime</c>), así que esta variable no
/// queda inerte: RLS la usa para filtrar en cada consulta. Solo la identidad
/// administrativa del arranque (<c>FabricaContextoDeBootstrap</c>, dos
/// seeders cross-tenant) sigue conectando como propietario a propósito, y
/// para ella RLS no aplica — es la excepción declarada, no el caso general.
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
/// <term>B — restricción de capacidad de la sesión privilegiada</term>
/// <description>
/// <c>SET ROLE cae_app_soporte</c> (solo lectura) o, dentro de un ámbito de
/// elevación de Aprovisionamiento (PD-A3), <c>SET ROLE cae_app_aprovisionamiento</c>
/// (escritura acotada por GRANT explícito). Responde "¿esta conexión puede
/// escribir, y qué?". Aplica solo cuando la petición viene por una sesión
/// privilegiada de plataforma.
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
/// <para>
/// <b>Una conexión y un contexto firmado por operación de la puerta.</b> Con
/// <see cref="PuertaAccesoDatos"/> (decisión D3 del plan de rendimiento,
/// 2026-11-09), el primer comando de una operación retiene la conexión física
/// abierta hasta que la operación suelta la puerta
/// (<see cref="PuertaAccesoDatos.RetenerHastaSalir"/>): el <c>set_config</c>
/// firmado se paga una vez por operación y no una vez por comando. Las
/// garantías no cambian, solo su mecanismo:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Nunca un comando con un contexto viejo.</b> Lo que se aplicó a cada
/// conexión (las cuatro coordenadas y el rol) se anota en
/// <see cref="SesionPorConexion"/>. Antes de cada comando fuera de transacción,
/// antes de cada <c>BEGIN</c> y al empezar cada <c>SaveChanges</c> fuera de
/// transacción se recalcula lo que fijaría una apertura nueva en ese instante y,
/// si difiere, se vuelve a aplicar entero —contexto firmado nuevo y rol— antes de
/// dejar pasar el comando (<see cref="AlinearSiCambioAsync"/>). Es lo que pasaba
/// antes por construcción, al abrir una conexión nueva en cada comando. Dentro
/// de una transacción no se realinea, igual que antes: la transacción ya retenía
/// la conexión con el contexto de su <c>BEGIN</c>. La excepción es el
/// <c>ROLLBACK TO SAVEPOINT</c>, que devuelve la sesión a la del punto de guardado
/// con la transacción aún viva: ahí se realinea el rol (el contexto firmado no se
/// vuelve a firmar dentro de la transacción) y la sesión queda incierta para el
/// primer uso fuera de ella. Esa realineación es síncrona y completa en el propio
/// gancho: si no se puede hacer, la transacción queda abortada en PostgreSQL y
/// ningún comando posterior corre con el rol del punto de guardado, sea de EF o
/// directo de Npgsql (<see cref="RealinearRolTrasPuntoDeGuardadoAsync"/>). El
/// contexto firmado no necesita realinearse dentro: solo lo mueve el sellado, y
/// lo mueve dentro del propio lote (<c>TenantSelladoInterceptor</c>), así que el
/// de cualquier punto de guardado del llamador es el del <c>BEGIN</c>.
/// Una sesión cuyo contexto se aplicó sin firmar (sin clave) no cuenta como
/// alineada, y el comando siguiente vuelve a intentar firmarlo, como hacía antes
/// cada apertura.
/// </description></item>
/// <item><description>
/// <b>Caducidad.</b> La renovación de <see cref="FirmanteContextoRls.RenovarSiHaceFaltaAsync"/>
/// sigue antes de cada comando fuera de transacción y antes de cada <c>BEGIN</c>.
/// </description></item>
/// <item><description>
/// <b>Devolución al pool.</b> La conexión se cierra al salir de la operación,
/// también si lanzó, y antes de soltar la puerta; el cierre sigue pasando por
/// <see cref="ConnectionClosingAsync"/> (<c>RESET ROLE</c>) y por el
/// <c>DISCARD ALL</c> de Npgsql. Si la conexión la tenía abierta otro (una
/// transacción, una apertura explícita), el contador de EF la deja abierta.
/// </description></item>
/// </list>
/// </summary>
public class TenantRlsConnectionInterceptor(
    ITenantActual tenantActual,
    IClienteActivoSeleccionado clienteActivoSeleccionado,
    ICurrentUserService currentUserService,
    FirmanteContextoRls firmante,
    PuertaAccesoDatos? puertaAccesoDatos = null,
    ILogger<TenantRlsConnectionInterceptor>? log = null)
    : DbConnectionInterceptor, IDbCommandInterceptor, IDbTransactionInterceptor, ISaveChangesInterceptor
{
    /// <summary>
    /// Lo que este interceptor aplicó a cada conexión física abierta: el
    /// contexto que firmó y el rol que adoptó. Estático y débil, como el estado
    /// de <see cref="FirmanteContextoRls"/>: muere con la conexión y lo consulta
    /// también <c>ElevacionEscrituraPrivilegiada</c>. Sin entrada, la sesión de
    /// la conexión es incierta (una aplicación a medias o una apertura que no
    /// pasó por aquí) y el siguiente comando la vuelve a aplicar entera.
    /// </summary>
    private static readonly ConditionalWeakTable<DbConnection, SesionRls> SesionPorConexion = new();

    /// <summary>
    /// Las coordenadas de sesión y el rol que una apertura de conexión fijaría.
    /// Igualdad por valor: dos iguales significan que la conexión ya está como
    /// la dejaría una apertura nueva.
    /// </summary>
    private sealed record SesionRls(ContextoSesionRls Contexto, string? Rol);

    /// <summary>
    /// Rol de solo lectura del plano 3 (ver la migración
    /// <c>RolSoporteSoloLectura</c>). Literal y no configurable: es el nombre
    /// que crea esa migración, y un valor configurable solo añadiría una forma
    /// de desactivar el control por despiste.
    /// </summary>
    private const string RolSoporte = "cae_app_soporte";

    /// <summary>
    /// Rol de escritura acotada al tenant objetivo del plano 3 (PD-A3, ver la
    /// migración <c>RolAprovisionamientoEscrituraAcotada</c>). Solo se adopta
    /// dentro de un <see cref="AmbitoEscrituraPrivilegiada"/> abierto — nunca
    /// por la sola presencia del token.
    /// </summary>
    private const string RolAprovisionamiento = "cae_app_aprovisionamiento";

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
    private async Task PrepararSesionAsync(NpgsqlConnection connection, CancellationToken cancellationToken) =>
        await AplicarAsync(connection, await SesionDeseadaAsync(), deshacerRolPrevio: false, cancellationToken);

    /// <summary>
    /// Lo que fijaría una apertura de conexión en este instante: las cuatro
    /// coordenadas del contexto firmado y el rol (ninguno, soporte o
    /// Aprovisionamiento). Leer el tenant, el claim y los <c>AsyncLocal</c> no
    /// toca la base, así que es seguro llamarlo antes de cada comando.
    /// </summary>
    private async Task<SesionRls> SesionDeseadaAsync()
    {
        var tenantId = tenantActual.TenantId;
        var tenantOrigenId = await currentUserService.ObtenerTenantOrigenIdAsync();
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();

        var origen = usuarioId is not null ? OrigenContextoRls.Peticion
            : AmbitoTenantExplicito.TenantIdActual is not null ? OrigenContextoRls.Ambito
            : OrigenContextoRls.Anonimo;

        var rol = DebeAdoptarRolDeAprovisionamiento() ? RolAprovisionamiento
            : clienteActivoSeleccionado.SesionPrivilegiadaIdSeleccionada is not null ? RolSoporte
            : null;

        return new SesionRls(new ContextoSesionRls(tenantId, tenantOrigenId, usuarioId, origen), rol);
    }

    /// <summary>
    /// Aplica <paramref name="sesion"/> entera, en el mismo orden que una
    /// apertura: primero el contexto, después el rol (ver
    /// <see cref="PrepararSesionAsync"/>). Mientras se aplica, la conexión no
    /// tiene sesión anotada ni contexto firmado en memoria: si un paso falla o
    /// se cancela, la sesión queda incierta, ninguna renovación ni sellado
    /// reescribe el contexto anterior, y el siguiente comando vuelve a
    /// aplicarla entera.
    ///
    /// <para>
    /// <paramref name="deshacerRolPrevio"/>: al realinear una conexión que ya
    /// adoptó un rol, o cuyo rol se desconoce, se empieza con <c>RESET ROLE</c>
    /// para partir del mismo estado que una apertura nueva (el rol de login).
    /// Entre ese <c>RESET ROLE</c> y el <c>SET ROLE</c> del final solo corre el
    /// <c>set_config</c> de este método, ningún comando de la aplicación; y si
    /// algo falla en medio, la sesión queda incierta y ningún comando pasa sin
    /// volver a aplicarla. No es el <c>RESET ROLE</c> que
    /// <c>IElevacionEscrituraPrivilegiada</c> prohíbe para devolver el rol: aquel
    /// dejaría correr la aplicación con el rol de login; este no.
    /// </para>
    /// <para>
    /// <b>Sin clave la sesión no cuenta como aplicada</b> (cuarta revisión de
    /// Codex): si el firmante devuelve el token vacío, la conexión queda con
    /// <c>app.contexto</c> vacío —falla cerrado, ninguna fila bajo RLS— y el
    /// firmante no guarda contexto para ella, así que
    /// <see cref="AlinearSiCambioAsync"/> no la da por alineada
    /// (<see cref="FirmanteContextoRls.TieneContextoFirmado"/>) y el siguiente
    /// comando vuelve a intentar firmar. Antes de D3 cada comando abría una
    /// conexión y lo intentaba; con la conexión retenida, darla por aplicada
    /// dejaría el resto de la operación sin contexto aunque la clave apareciera a
    /// mitad. Lo anotado conserva el rol, que sí es el deseado: el contexto vacío
    /// no concede nada y el rol solo quita capacidad.
    /// </para>
    /// </summary>
    private async Task AplicarAsync(
        NpgsqlConnection connection, SesionRls sesion, bool deshacerRolPrevio, CancellationToken cancellationToken)
    {
        SesionPorConexion.Remove(connection);
        FirmanteContextoRls.Olvidar(connection);

        if (deshacerRolPrevio)
            await DeshacerRolAsync(connection, cancellationToken);

        await FijarVariablesDeSesionAsync(connection, sesion.Contexto, cancellationToken);

        if (sesion.Rol is { } rol)
            await AdoptarRolAsync(connection, rol, cancellationToken);

        SesionPorConexion.AddOrUpdate(connection, sesion);
    }

    /// <summary>
    /// Vuelve a aplicar la sesión si lo que fijaría una apertura nueva ya no es
    /// lo que tiene esta conexión abierta: cambio de Tenant activo, de usuario,
    /// de <see cref="AmbitoTenantExplicito"/>, de sesión privilegiada o de ámbito
    /// de elevación desde que se abrió. No hace nada con la conexión cerrada (la
    /// apertura la preparará) ni si coincide, que es el caso de cada comando de
    /// una operación sin cambios de contexto.
    /// </summary>
    private async Task AlinearSiCambioAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        if (connection is not NpgsqlConnection npgsql || npgsql.State != System.Data.ConnectionState.Open)
            return;

        // Una transacción terminó sin COMMIT (ROLLBACK, fallo o Dispose sin
        // confirmar): lo que se fijó dentro se deshizo y lo anotado no lo sabe.
        if (TransaccionSinConfirmar.TryGetValue(npgsql, out _))
            Olvidar(npgsql);

        var deseada = await SesionDeseadaAsync();
        // Sin contexto firmado en memoria la sesión no está alineada aunque esté
        // anotada: una renovación o un sellado sin clave dejaron app.contexto
        // vacío, y el siguiente comando tiene que volver a intentar firmarlo.
        // También la que se aplicó sin clave: AplicarAsync la anota (el rol sí es
        // el deseado) pero el firmante no guarda contexto que renovar.
        var anotada = SesionPorConexion.TryGetValue(npgsql, out var aplicada);
        if (anotada && aplicada == deseada && FirmanteContextoRls.TieneContextoFirmado(npgsql))
            return;

        await AplicarAsync(npgsql, deseada, deshacerRolPrevio: !anotada || aplicada!.Rol is not null, cancellationToken);
    }

    /// <summary>
    /// Para <c>ElevacionEscrituraPrivilegiada</c>, que adelanta el
    /// <c>SET ROLE</c> en la conexión ya abierta: anota el rol que adoptó para
    /// que el siguiente comando no lo tome por un cambio y vuelva a aplicar la
    /// sesión entera. Solo si la conexión tiene sesión anotada; si no, sigue
    /// incierta y el siguiente comando la aplica.
    /// </summary>
    internal static void AnotarRolAdoptado(DbConnection connection, string? rol)
    {
        if (SesionPorConexion.TryGetValue(connection, out var aplicada))
            SesionPorConexion.AddOrUpdate(connection, aplicada with { Rol = rol });
    }

    /// <summary>
    /// PD-A3: la ÚNICA condición que puede elevar el rol de escritura, y las
    /// tres partes tienen que coincidir a la vez. El <c>AsyncLocal</c> por sí
    /// solo no basta —invariante central del diseño, la cookie nunca decide el
    /// rol de escritura— así que se compara TANTO contra la sesión que el
    /// token nombra COMO contra el tenant que <see cref="ITenantActual"/>
    /// resuelve en este instante: un ámbito abierto que sobreviviera a un
    /// cambio de selección de tenant a mitad de circuito, o que se filtrara a
    /// una conexión de otra sesión del mismo proceso, no eleva nada.
    /// </summary>
    private bool DebeAdoptarRolDeAprovisionamiento() =>
        CaeManager.Application.Plataforma.AmbitoEscrituraPrivilegiada.Actual is { } ambito
        && clienteActivoSeleccionado.SesionPrivilegiadaIdSeleccionada == ambito.SesionId
        && tenantActual.TenantId == ambito.TenantObjetivoId;

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
    ///
    /// <para>
    /// <b>Desde P6 «contraer» (migración <c>ContraeContextoRlsFirmado</c>) ninguna
    /// política ni función lee esas tres GUC sueltas</b>: leen
    /// <c>app_ctx_tenant_id()</c>, <c>app_ctx_tenant_origen_id()</c> y
    /// <c>app_ctx_usuario_id()</c>, que solo creen el token firmado de
    /// <c>app.contexto</c>. Las tres siguen fijándose con el mismo valor que el
    /// token por dos motivos: el <c>Down</c> de esa migración devuelve las
    /// políticas a las GUC y una base revertida tiene que seguir funcionando con
    /// esta versión, y el estado materializado aún lee <c>app.tenant_id</c> como
    /// coordenada (no como autoridad: lo que escribe lo filtra el WITH CHECK
    /// firmado). Quien las cambie a mano no gana nada. Retirarlas es el paso
    /// siguiente, cuando nada las lea.
    /// </para>
    /// </summary>
    private async Task FijarVariablesDeSesionAsync(
        NpgsqlConnection connection, ContextoSesionRls contexto, CancellationToken cancellationToken)
    {
        var (tenantId, tenantOrigenId, usuarioId) = (contexto.TenantId, contexto.TenantOrigenId, contexto.UsuarioId);
        var token = await firmante.FirmarAsync(connection, contexto, cancellationToken);

        await using var comando = connection.CreateCommand();
        comando.CommandText =
            "SELECT set_config('app.tenant_id', @tenantId, false), " +
            "set_config('app.tenant_origen_id', @tenantOrigenId, false), " +
            "set_config('app.usuario_id', @usuarioId, false), " +
            "set_config('app.contexto', @contexto, false);";
        comando.Parameters.AddWithValue("tenantId", tenantId?.ToString() ?? string.Empty);
        comando.Parameters.AddWithValue("tenantOrigenId", tenantOrigenId?.ToString() ?? string.Empty);
        comando.Parameters.AddWithValue("usuarioId", usuarioId?.ToString() ?? string.Empty);
        comando.Parameters.AddWithValue("contexto", token.Token);
        await comando.ExecuteNonQueryAsync(cancellationToken);
        token.Confirmar();
    }

    // ── Renovación del contexto firmado (P6, diseño § 7) ─────────────────
    //
    // La caducidad del token la compara app_contexto_validado() contra now(),
    // que es el INICIO de la transacción en curso. Contrato: el token tiene
    // que estar vigente cuando empieza cada transacción; dentro de ella no
    // caduca. Por eso se renueva (si pasó la mitad del TTL) justo antes de
    // abrir una transacción explícita y antes de cada comando en
    // autocommit, y nunca dentro de una transacción: un set_config de sesión
    // hecho dentro de una transacción que después se deshace vuelve al valor
    // anterior, y el estado en memoria diría que está renovado sin estarlo.
    //
    // La realineación (AlinearSiCambioAsync) va en los mismos puntos y por el
    // mismo motivo: dentro de una transacción no se toca la sesión.

    public override void ConnectionClosed(DbConnection connection, ConnectionEndEventData eventData)
    {
        Olvidar(connection);
        base.ConnectionClosed(connection, eventData);
    }

    public override Task ConnectionClosedAsync(DbConnection connection, ConnectionEndEventData eventData)
    {
        Olvidar(connection);
        return base.ConnectionClosedAsync(connection, eventData);
    }

    private static void Olvidar(DbConnection connection)
    {
        FirmanteContextoRls.Olvidar(connection);
        SesionPorConexion.Remove(connection);
        TransaccionSinConfirmar.Remove(connection);
    }

    /// <summary>
    /// Para <c>TenantSelladoInterceptor</c>, cuando no pudo devolver el contexto
    /// de base a una conexión que sigue abierta FUERA de una transacción: se
    /// olvida lo anotado y se cierra la conexión física. Ningún comando, de EF ni
    /// directo, corre ya sobre esa sesión: uno directo falla porque la conexión
    /// está cerrada, y uno de EF la reabre y la prepara entera
    /// (<see cref="ConnectionOpenedAsync"/>), como antes de retener la conexión.
    /// Al devolverla al pool, Npgsql descarta la sesión (<c>DISCARD ALL</c>).
    /// Dentro de una transacción no se usa: cerrar la conexión la desharía por
    /// debajo de EF.
    /// </summary>
    internal static async Task DescartarSesionAsync(DbConnection connection)
    {
        Olvidar(connection);
        await connection.CloseAsync();
    }

    /// <summary>
    /// Conexiones con una transacción empezada y aún no confirmada. EF no avisa
    /// cuando una transacción se deshace por <c>Dispose</c> sin confirmar (no hay
    /// gancho de rollback para eso), así que se marca al empezar y se desmarca
    /// solo con el <c>COMMIT</c>: cualquier otro final deja la marca y el
    /// siguiente uso fuera de transacción olvida lo anotado.
    /// </summary>
    private static readonly ConditionalWeakTable<DbConnection, object> TransaccionSinConfirmar = new();

    InterceptionResult<DbDataReader> IDbCommandInterceptor.ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        AntesDelComandoAsync(command, eventData, CancellationToken.None).GetAwaiter().GetResult();
        return result;
    }

    async ValueTask<InterceptionResult<DbDataReader>> IDbCommandInterceptor.ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken)
    {
        await AntesDelComandoAsync(command, eventData, cancellationToken);
        return result;
    }

    InterceptionResult<int> IDbCommandInterceptor.NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        AntesDelComandoAsync(command, eventData, CancellationToken.None).GetAwaiter().GetResult();
        return result;
    }

    async ValueTask<InterceptionResult<int>> IDbCommandInterceptor.NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken)
    {
        await AntesDelComandoAsync(command, eventData, cancellationToken);
        return result;
    }

    InterceptionResult<object> IDbCommandInterceptor.ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        AntesDelComandoAsync(command, eventData, CancellationToken.None).GetAwaiter().GetResult();
        return result;
    }

    async ValueTask<InterceptionResult<object>> IDbCommandInterceptor.ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken)
    {
        await AntesDelComandoAsync(command, eventData, cancellationToken);
        return result;
    }

    void IDbCommandInterceptor.CommandFailed(DbCommand command, CommandErrorEventData eventData)
    {
        if (FirmanteContextoRls.EsClaveDesconocida(eventData.Exception) && command.Connection is { } conexion)
            firmante.InvalidarClave(conexion);
    }

    Task IDbCommandInterceptor.CommandFailedAsync(
        DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken)
    {
        if (FirmanteContextoRls.EsClaveDesconocida(eventData.Exception) && command.Connection is { } conexion)
            firmante.InvalidarClave(conexion);
        return Task.CompletedTask;
    }

    InterceptionResult<DbTransaction> IDbTransactionInterceptor.TransactionStarting(
        DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result)
    {
        AntesDeTransaccionAsync(connection, CancellationToken.None).GetAwaiter().GetResult();
        return result;
    }

    async ValueTask<InterceptionResult<DbTransaction>> IDbTransactionInterceptor.TransactionStartingAsync(
        DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result,
        CancellationToken cancellationToken)
    {
        await AntesDeTransaccionAsync(connection, cancellationToken);
        return result;
    }

    /// <summary>
    /// Al empezar un <c>SaveChanges</c> fuera de transacción, con la conexión ya
    /// abierta (retenida por la operación de la puerta): realinea ANTES del
    /// sellado de <c>TenantSelladoInterceptor</c>, que va registrado detrás (ver
    /// <c>ConfiguracionDeContexto</c>). El sellado decide con el contexto de base
    /// de la conexión y, si el lote necesita otro Tenant (una fila de Identity),
    /// lo mueve dentro del ámbito transaccional del lote, donde los comandos ya no
    /// se realinean (ver <see cref="AntesDelComandoAsync"/>). Con la conexión
    /// cerrada no hace nada: la apertura que haga el lote la preparará.
    /// </summary>
    ValueTask<InterceptionResult<int>> ISaveChangesInterceptor.SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken)
        => AntesDeGuardarAsync(eventData, result, cancellationToken);

    InterceptionResult<int> ISaveChangesInterceptor.SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        => AntesDeGuardarAsync(eventData, result, CancellationToken.None).AsTask().GetAwaiter().GetResult();

    private async ValueTask<InterceptionResult<int>> AntesDeGuardarAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken)
    {
        if (eventData.Context is { } contexto && contexto.Database.CurrentTransaction is null)
            // La conexión de EF de este contexto (la misma que abre y cierra este interceptor), no una nueva.
            await AlinearSiCambioAsync(contexto.GetService<IRelationalConnection>().DbConnection, cancellationToken);
        return result;
    }

    // ── Fin de transacción con deshacer ─────────────────────────────────────
    //
    // PostgreSQL deshace con un ROLLBACK (o un ROLLBACK TO SAVEPOINT, que EF usa
    // dentro de SaveChanges en una transacción del llamador) los SET ROLE y los
    // set_config de sesión hechos dentro: una elevación o su vuelta, una
    // renovación, un sellado (que por eso vive dentro del ámbito transaccional de
    // su lote: el deshacer de PostgreSQL es su restauración en un conflicto). Lo
    // anotado en memoria no se deshace con ellos, y
    // con la conexión retenida el comando siguiente compararía contra una sesión
    // que ya no es la de la conexión (p. ej. anotado soporte con el rol de
    // aprovisionamiento restaurado por el ROLLBACK). Con la transacción terminada
    // se olvida todo: el siguiente comando fuera de ella vuelve a aplicar la
    // sesión entera. El Dispose sin confirmar no tiene gancho: lo cubre la marca de
    // TransaccionSinConfirmar, que solo el COMMIT retira. El ROLLBACK TO SAVEPOINT
    // deja la transacción viva, así que no basta con olvidar: se realinea el rol
    // dentro de ella y se conserva el estado del firmante (ver RolledBackToSavepoint).

    DbTransaction IDbTransactionInterceptor.TransactionStarted(
        DbConnection connection, TransactionEndEventData eventData, DbTransaction result)
    {
        MarcarSinConfirmar(connection);
        return result;
    }

    ValueTask<DbTransaction> IDbTransactionInterceptor.TransactionStartedAsync(
        DbConnection connection, TransactionEndEventData eventData, DbTransaction result,
        CancellationToken cancellationToken)
    {
        MarcarSinConfirmar(connection);
        return ValueTask.FromResult(result);
    }

    void IDbTransactionInterceptor.TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData)
    {
        if (ConexionDe(transaction, eventData) is { } conexion)
            TransaccionSinConfirmar.Remove(conexion);
    }

    Task IDbTransactionInterceptor.TransactionCommittedAsync(
        DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken)
    {
        if (ConexionDe(transaction, eventData) is { } conexion)
            TransaccionSinConfirmar.Remove(conexion);
        return Task.CompletedTask;
    }

    /// <summary>
    /// La conexión de una transacción en sus ganchos. Tras el <c>COMMIT</c> o el
    /// <c>ROLLBACK</c>, <see cref="DbTransaction.Connection"/> puede ser
    /// <c>null</c> (una transacción terminada ya no está ligada a su conexión), así
    /// que manda la conexión de EF del contexto, la misma que abre y cierra este
    /// interceptor (revisión puente de la segunda ronda). Sin contexto, la de la
    /// transacción.
    /// </summary>
    private static DbConnection? ConexionDe(DbTransaction transaction, TransactionEventData eventData) =>
        eventData.Context?.GetService<IRelationalConnection>().DbConnection ?? transaction.Connection;

    private static void MarcarSinConfirmar(DbConnection conexion) =>
        TransaccionSinConfirmar.AddOrUpdate(conexion, Marca);

    private static readonly object Marca = new();

    void IDbTransactionInterceptor.TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) =>
        OlvidarSiHayConexion(ConexionDe(transaction, eventData));

    Task IDbTransactionInterceptor.TransactionRolledBackAsync(
        DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken)
    {
        OlvidarSiHayConexion(ConexionDe(transaction, eventData));
        return Task.CompletedTask;
    }

    void IDbTransactionInterceptor.TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData) =>
        TrasFalloDeTransaccion(ConexionDe(transaction, eventData), eventData.Action);

    Task IDbTransactionInterceptor.TransactionFailedAsync(
        DbTransaction transaction, TransactionErrorEventData eventData, CancellationToken cancellationToken)
    {
        TrasFalloDeTransaccion(ConexionDe(transaction, eventData), eventData.Action);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Un fallo de <c>COMMIT</c> o <c>ROLLBACK</c> termina la transacción: se
    /// olvida todo. Un fallo de una acción de punto de guardado (crearlo,
    /// liberarlo o volver a él, incluida la realineación del rol de
    /// <see cref="RealinearRolTrasPuntoDeGuardadoAsync"/> que lanza) puede dejar
    /// la transacción viva (revisión puente de la cuarta ronda): se retira la
    /// sesión anotada y se marca la conexión, como en el punto de guardado, pero se
    /// conserva el estado del firmante, sin el cual el sellado no podría firmar el
    /// contexto de su lote dentro de esa transacción.
    /// </summary>
    private static void TrasFalloDeTransaccion(DbConnection? conexion, string action)
    {
        if (conexion is null)
            return;

        if (action.Contains("Savepoint", StringComparison.Ordinal))
        {
            SesionPorConexion.Remove(conexion);
            MarcarSinConfirmar(conexion);
            return;
        }

        Olvidar(conexion);
    }

    /// <summary>
    /// <c>ROLLBACK TO SAVEPOINT</c> (segunda revisión de Codex): a diferencia del
    /// ROLLBACK, la transacción sigue viva y sus comandos siguientes no se
    /// realinean. PostgreSQL ha devuelto el rol al que tenía al crear el punto de
    /// guardado, que puede no ser el vigente: un rol de aprovisionamiento con el
    /// ámbito ya cerrado y devuelto a soporte.
    ///
    /// <para>
    /// <b>El rol se realinea aquí mismo, y en un solo comando</b>: <c>RESET ROLE</c>
    /// y el <c>SET ROLE</c> del rol que fijaría una apertura nueva en este instante
    /// (<see cref="DebeAdoptarRolDeAprovisionamiento"/> sigue exigiendo ámbito,
    /// sesión y Tenant), así que nunca concede más que eso.
    /// </para>
    /// <para>
    /// <b>Sin estado diferido</b> (quinta revisión de Codex): la ronda anterior
    /// marcaba la conexión cuando la realineación fallaba y la volvía a intentar
    /// antes del siguiente comando de EF, pero un comando directo de Npgsql no pasa
    /// por ningún interceptor y corría con el rol del punto de guardado. Ahora no
    /// queda ningún estado que el comando siguiente tenga que reparar: el rol
    /// deseado se resuelve antes de tocar la base; si resolverlo falla, la
    /// transacción se aborta en PostgreSQL (<see cref="AbortarTransaccionAsync"/>)
    /// y se propaga el error; y el <c>RESET ROLE</c> y el <c>SET ROLE</c> van en un
    /// solo comando, así que si el <c>SET ROLE</c> falla en la base la transacción
    /// queda abortada con él. En los tres desenlaces, o el rol es el deseado o
    /// ningún comando de la transacción —de EF o directo— llega a ejecutarse hasta
    /// un <c>ROLLBACK</c> o un nuevo <c>ROLLBACK TO SAVEPOINT</c>, que vuelve a pasar
    /// por aquí. Un fallo solo del cliente (conexión rota, tiempo agotado) deja la
    /// conexión rota o, con la cancelación de Npgsql, la transacción abortada.
    /// </para>
    /// <para>
    /// <b>El contexto firmado no se vuelve a firmar dentro de la transacción</b>
    /// (tercera revisión de Codex): una firma nueva podría usar una clave que la
    /// instantánea de una transacción <c>REPEATABLE READ</c> todavía no ve y dejar
    /// la transacción sin RLS utilizable. Tampoco hace falta: dentro de una
    /// transacción solo el sellado mueve el contexto, y lo mueve dentro del punto de
    /// guardado de su propio lote y lo devuelve antes de que el lote termine
    /// (<c>TenantSelladoInterceptor</c>), así que el contexto de cualquier punto de
    /// guardado del llamador es el del <c>BEGIN</c>. Se conserva el estado del
    /// firmante y se retira solo la sesión anotada: fuera de la transacción, la
    /// marca de <see cref="TransaccionSinConfirmar"/> o la falta de anotación hacen
    /// que el siguiente uso aplique la sesión entera, también tras un COMMIT.
    /// </para>
    /// </summary>
    void IDbTransactionInterceptor.RolledBackToSavepoint(DbTransaction transaction, TransactionEventData eventData) =>
        RealinearRolTrasPuntoDeGuardadoAsync(ConexionDe(transaction, eventData)).GetAwaiter().GetResult();

    Task IDbTransactionInterceptor.RolledBackToSavepointAsync(
        DbTransaction transaction, TransactionEventData eventData, CancellationToken cancellationToken) =>
        RealinearRolTrasPuntoDeGuardadoAsync(ConexionDe(transaction, eventData));

    private async Task RealinearRolTrasPuntoDeGuardadoAsync(DbConnection? conexion)
    {
        if (conexion is not NpgsqlConnection npgsql || npgsql.State != System.Data.ConnectionState.Open)
            return;

        SesionPorConexion.Remove(npgsql);
        MarcarSinConfirmar(npgsql);

        string? rolDeseado;
        try
        {
            rolDeseado = (await SesionDeseadaAsync()).Rol;
        }
        catch
        {
            await AbortarTransaccionAsync(npgsql);
            throw;
        }

        // Identificadores fijos del código (los dos roles literales de esta clase).
        await using var comando = npgsql.CreateCommand();
        comando.CommandText = rolDeseado is null ? "RESET ROLE;" : $"RESET ROLE; SET ROLE {rolDeseado};";
        await comando.ExecuteNonQueryAsync(CancellationToken.None);
    }

    /// <summary>
    /// Deja la transacción en curso abortada en PostgreSQL con un error deliberado
    /// que no necesita ningún privilegio (un literal que no es un entero): a partir
    /// de ahí la base rechaza todo comando de la transacción, de EF o directo
    /// (<c>25P02</c>), hasta un <c>ROLLBACK</c> o un <c>ROLLBACK TO SAVEPOINT</c>. Si
    /// la base no lo rechazara, no habría garantía, y se lanza.
    /// </summary>
    private static async Task AbortarTransaccionAsync(NpgsqlConnection conexion)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = "SELECT 'sesion RLS sin realinear tras ROLLBACK TO SAVEPOINT'::integer;";
        try
        {
            await comando.ExecuteNonQueryAsync(CancellationToken.None);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InvalidTextRepresentation)
        {
            return;
        }

        throw new InvalidOperationException(
            "No se pudo abortar la transacción tras no poder realinear el rol después de un ROLLBACK TO SAVEPOINT.");
    }

    private static void OlvidarSiHayConexion(DbConnection? conexion)
    {
        if (conexion is not null)
            Olvidar(conexion);
    }

    private async Task AntesDeTransaccionAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await AlinearSiCambioAsync(connection, cancellationToken);
        await RenovarAsync(connection, cancellationToken);
    }

    /// <summary>
    /// Antes de cada comando de EF: fuera de transacción, realinear y renovar;
    /// después, si hay una operación de la puerta en curso en este flujo,
    /// retener la conexión hasta que termine. La retención va al final a
    /// propósito: si realinear falla, el comando falla y la conexión se cierra
    /// por el camino de siempre, sin quedar retenida. Dentro de una transacción no
    /// se realinea nada: los puntos que cambian la sesión dentro de ella la dejan
    /// alineada, o la transacción abortada, en el mismo instante en que la cambian
    /// (<see cref="RealinearRolTrasPuntoDeGuardadoAsync"/>, el sellado).
    /// </summary>
    private async Task AntesDelComandoAsync(DbCommand command, CommandEventData eventData, CancellationToken cancellationToken)
    {
        if (command.Connection is { } conexion && command.Transaction is null)
        {
            await AlinearSiCambioAsync(conexion, cancellationToken);
            await RenovarAsync(conexion, cancellationToken);
        }

        RetenerSiHayOperacion(eventData.Context);
    }

    /// <summary>
    /// Suma una apertura al contador de EF (<see cref="IRelationalConnection"/>,
    /// sin pasar por la estrategia de ejecución): la conexión ya está abierta
    /// para este comando, así que no hay E/S, y el cierre que EF haga al
    /// terminarlo ya no la devuelve al pool. La puerta resta esa apertura al
    /// salir (<see cref="LiberarAsync"/>). Si la conexión la tenía abierta otro
    /// —una transacción, una apertura explícita, una conexión que EF no abrió—,
    /// el contador de EF la sigue dejando abierta para él: la puerta solo deshace
    /// lo que sumó. Una sola retención por conexión y operación; fuera de una
    /// operación, o desde un flujo que ya no es el suyo, no retiene nada.
    /// </summary>
    private void RetenerSiHayOperacion(DbContext? contexto)
    {
        if (puertaAccesoDatos is null || contexto is null)
            return;

        var conexion = contexto.GetService<IRelationalConnection>();
        puertaAccesoDatos.RetenerHastaSalir(conexion, () => conexion.Open(), () => LiberarAsync(conexion));
    }

    /// <summary>
    /// Cierre al salir de la operación, sin token de cancelación (es limpieza):
    /// pasa por <see cref="ConnectionClosingAsync"/> (<c>RESET ROLE</c>) y
    /// <see cref="ConnectionClosedAsync"/>, como cualquier cierre de EF. Si el
    /// cierre de EF falla, se cierra la conexión física directamente para que
    /// no quede abierta fuera de la operación con el contexto de este usuario;
    /// al reutilizarla, Npgsql descarta la sesión (<c>DISCARD ALL</c>) y la
    /// apertura siguiente vuelve a prepararla, porque <see cref="AplicarAsync"/>
    /// parte de olvidar lo anotado (olvidarlo antes del segundo intento es lo
    /// seguro: sin anotación, todo uso posterior aplica la sesión entera y el
    /// cierre hace <c>RESET ROLE</c>). Si también falla el cierre físico, no se
    /// absorbe: la operación termina con error (la puerta suelta igualmente) en
    /// vez de comunicar éxito sin saber si la conexión volvió al pool.
    /// </summary>
    private async ValueTask LiberarAsync(IRelationalConnection conexion)
    {
        try
        {
            await conexion.CloseAsync();
        }
        catch (Exception ex)
        {
            log?.LogWarning(ex, "No se pudo cerrar la conexión retenida por la operación; se cierra la conexión física.");
            try
            {
                Olvidar(conexion.DbConnection);
                await conexion.DbConnection.CloseAsync();
            }
            catch (Exception exCierre)
            {
                log?.LogError(exCierre, "No se pudo cerrar la conexión física retenida por la operación.");
                throw new AggregateException(
                    "No se pudo cerrar la conexión retenida por la operación de acceso a datos.", ex, exCierre);
            }
        }
    }

    private static async Task RenovarAsync(DbConnection conexion, CancellationToken cancellationToken)
    {
        var pendiente = await FirmanteContextoRls.RenovarSiHaceFaltaAsync(conexion, cancellationToken);
        if (pendiente is null) return;

        await using var comando = conexion.CreateCommand();
        comando.CommandText = "SELECT set_config('app.contexto', @contexto, false);";
        var parametro = comando.CreateParameter();
        parametro.ParameterName = "contexto";
        parametro.Value = pendiente.Token;
        comando.Parameters.Add(parametro);
        await comando.ExecuteNonQueryAsync(cancellationToken);
        pendiente.Confirmar();
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
    /// Se paga solo cuando se puso: cuando lo anotado para la conexión lleva
    /// rol, cuando no hay nada anotado (sesión incierta) o cuando la petición va
    /// por una sesión privilegiada. Lo anotado cuenta además de la selección
    /// actual porque la conexión puede llevar abierta toda la operación: si la
    /// sesión privilegiada se retiró a mitad, la selección ya no la nombra pero
    /// el rol sigue puesto. Ninguna petición normal gasta un viaje extra.
    /// </summary>
    private async Task DevolverRolAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        if (connection.State != System.Data.ConnectionState.Open) return;

        var sinRolAnotado = SesionPorConexion.TryGetValue(connection, out var aplicada) && aplicada.Rol is null
            && !TransaccionSinConfirmar.TryGetValue(connection, out _);
        if (sinRolAnotado && clienteActivoSeleccionado.SesionPrivilegiadaIdSeleccionada is null) return;

        await DeshacerRolAsync(connection, cancellationToken);
    }

    private static async Task DeshacerRolAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var comando = connection.CreateCommand();
        comando.CommandText = "RESET ROLE;";
        await comando.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Enforcement del plano 3 en la capa de datos (ADR-011 § 4bis.7.4):
    /// mientras la petición venga por una sesión privilegiada, la conexión
    /// adopta un rol acotado — <c>cae_app_soporte</c> (sin <c>INSERT</c>,
    /// <c>UPDATE</c> ni <c>DELETE</c> sobre nada) o, dentro de un ámbito de
    /// elevación de Aprovisionamiento, <c>cae_app_aprovisionamiento</c> (GRANT
    /// explícito por tabla, PD-A3). Deja de importar por dónde intente
    /// escribir el código — MediatR, un repositorio suelto, SQL crudo: lo que
    /// esté fuera del GRANT del rol falla en Postgres.
    ///
    /// <b>La rama de soporte se decide con el token, sin consultar la base</b>
    /// —consultar la sesión privilegiada exigiría una consulta sobre la
    /// conexión que se está abriendo en este mismo momento, que es
    /// reentrante—, y ahí sigue siendo la única opción correcta porque esa
    /// decisión solo <i>quita</i> capacidad: un token que mienta al declarar
    /// sesión no gana nada, se queda con una conexión de solo lectura.
    ///
    /// <b>La rama de Aprovisionamiento NO se decide con el token</b> —ese es
    /// justo el invariante que PD-A3 no relaja—: <see cref="DebeAdoptarRolDeAprovisionamiento"/>
    /// exige además un <see cref="AmbitoEscrituraPrivilegiada"/> abierto, que
    /// solo <c>ElevacionEscrituraAprovisionamientoBehavior</c> establece, y
    /// solo después de que <c>AutorizacionEscrituraBehavior</c> revalidó la
    /// sesión contra base de datos en ese mismo comando. Un token que mienta
    /// aquí no consigue nada: sin ámbito abierto, cae en la rama de soporte o
    /// en ninguna.
    ///
    /// En las dos ramas, quien decide si la sesión vale de verdad sigue siendo
    /// <c>ISesionPrivilegiadaActual</c>, que revalida contra la base; si no
    /// vale, la revalidación por petición tumba la selección entera y el
    /// contexto cae al tenant propio del usuario.
    ///
    /// <b>Fallo cerrado si el rol no existe o no se puede adoptar.</b> Se deja
    /// propagar la excepción de Postgres a propósito. Un entorno que no haya
    /// concedido la membresía (<c>GRANT cae_app_soporte TO &lt;rol de
    /// login&gt;</c>) verá fallar las peticiones de soporte, que es ruidoso y
    /// arreglable; tragarse el error dejaría la sesión corriendo con permisos
    /// de escritura completos y sin que nada lo dijera. De las dos formas de
    /// equivocarse, solo una es reversible.
    ///
    /// El rol se devuelve al cerrar (ver <see cref="ConnectionClosingAsync"/>).
    /// </summary>
    private static async Task AdoptarRolAsync(NpgsqlConnection connection, string rol, CancellationToken cancellationToken)
    {
        await using var comando = connection.CreateCommand();
        // Identificador fijo del código (uno de los dos roles literales de
        // esta clase), no un valor de entrada: no hay parámetro que valga
        // para SET ROLE y tampoco hace falta ninguno.
        comando.CommandText = $"SET ROLE {rol};";
        await comando.ExecuteNonQueryAsync(cancellationToken);
    }
}
