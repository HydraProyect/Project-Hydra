using CaeManager.Application.Common;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Persistence.ContextoRls;
using CaeManager.Infrastructure.Persistence.Interceptors;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.MultiTenancy;

/// <summary>
/// Sella <c>TenantId</c> en toda entidad nueva desde <see cref="ITenantActual"/>
/// (ver Project-Hydra-Negocio/tecnico/docs/MULTITENANCY.md § 4.3) y rechaza cualquier modificación o
/// eliminación de una entidad que pertenezca a otro tenant — defensa en
/// profundidad además del filtro global de lectura (ver
/// <c>CaeManagerDbContext.OnModelCreating</c>), para el caso de una entidad
/// cargada por una vía que no pasó por el filtro (p. ej. un
/// <c>IgnoreQueryFilters()</c> justificado y revisado). Los Commands nunca
/// asignan <c>TenantId</c> directamente — es exclusivo de este interceptor,
/// mismo principio arquitectónico que <c>AuditoriaInterceptor</c> para los
/// campos de auditoría.
///
/// <para>
/// <b>El contexto de sesión del lote se mueve dentro del lote</b> (quinta
/// revisión de Codex de la conexión por operación). Cuando un lote necesita un
/// Tenant de sesión distinto del de la conexión —una fila de Identity del Tenant
/// propietario de la cuenta, o un ámbito que cambió dentro de una transacción del
/// llamador, ver <see cref="SellarYValidarAsync"/>—, el <c>set_config</c> no se
/// hace al empezar el <c>SaveChanges</c>, fuera de cualquier transacción, sino
/// dentro del ámbito transaccional del propio lote: justo antes del primer comando
/// del lote (<see cref="CommandSource.SaveChanges"/>), después del <c>BEGIN</c> de
/// la transacción que abre EF para el lote o del punto de guardado que EF crea
/// dentro de la transacción del llamador (<see cref="AplicarTenantDelLoteAsync"/>).
/// Para que siempre haya uno, un lote que mueve el contexto se ejecuta con
/// <see cref="AutoTransactionBehavior.Always"/>, que para una sola sentencia es
/// equivalente en atomicidad.
/// </para>
/// <para>
/// <b>Solo un comando del propio <c>SaveChanges</c> mueve el contexto</b>
/// (revisión puente de la sexta ronda, hallazgo 2). El <c>BEGIN</c> y el punto de
/// guardado solo abren el ámbito; el movimiento lo hace el primer comando cuyo
/// origen es <see cref="CommandSource.SaveChanges"/>. Si otro interceptor de
/// <c>SavingChanges</c> registrado detrás, o el <c>DetectChanges</c> posterior,
/// lanzan después de preparar el lote, EF no avisa por ningún final y el lote
/// queda preparado sin ejecutar; una transacción explícita o un punto de guardado
/// manual del llamador antes del <c>SaveChanges</c> siguiente abren un ámbito pero
/// no mueven nada, porque ningún comando de <c>SaveChanges</c> puede llegar sin que
/// antes pase ese <c>SaveChanges</c> siguiente por <see cref="SellarYValidarAsync"/>,
/// que cierra el lote preparado. Y un lote preparado que llega a su primer comando
/// sin ámbito abierto no se ejecuta.
/// </para>
/// <para>
/// Así, cualquier desenlace con error lo deshace PostgreSQL en el mismo instante
/// en que EF deshace el lote: el <c>ROLLBACK</c> de la transacción del lote o el
/// <c>ROLLBACK TO SAVEPOINT</c> de su punto de guardado devuelven
/// <c>app.tenant_id</c> y <c>app.contexto</c> al valor de antes del lote, también
/// en un conflicto de concurrencia, cuyo gancho llega con el lector del lote aún
/// abierto y no permite ejecutar nada. Antes la restauración quedaba diferida al
/// siguiente comando de EF o a la salida de la operación, y en esa ventana un
/// comando directo de Npgsql, o un punto de guardado del llamador seguido de un
/// <c>ROLLBACK TO SAVEPOINT</c>, veía el Tenant de la cuenta. La restauración ya
/// no se difiere a otro comando: en el éxito, el fallo y la cancelación la
/// conexión está libre y se restaura en el mismo final (<see cref="RestaurarAsync"/>), y si la restauración
/// falla fuera de una transacción la conexión física se cierra
/// (<see cref="TenantRlsConnectionInterceptor.DescartarSesionAsync"/>).
/// </para>
/// <para>
/// Lo que se restaura es exactamente lo que la conexión tenía antes del lote
/// (las dos variables leídas justo antes de moverlas), no un contexto vuelto a
/// firmar: dentro de una transacción no se firma el contexto de base de nuevo
/// (una clave nueva podría no ser visible para su instantánea), y la memoria del
/// firmante no se toca, porque el token del lote no es el contexto de la conexión
/// (no se llama a <see cref="FirmanteContextoRls.TokenPendiente.Confirmar"/>).
/// </para>
/// </summary>
public class TenantSelladoInterceptor(
    ITenantActual tenantActual,
    ICurrentUserService? currentUserService = null,
    ILogger<TenantSelladoInterceptor>? log = null)
    : SaveChangesInterceptor, IDbTransactionInterceptor, IDbCommandInterceptor
{
    // ── Estado del SaveChanges en vuelo ──────────────────────────────────────
    //
    // El interceptor es AddScoped (una instancia por DbContext/petición, ver
    // ConfiguracionDeContexto) y solo un SaveChanges está en vuelo a la vez por
    // contexto, así que los campos de instancia son seguros.

    /// <summary>El contexto cuyo lote necesita mover el Tenant de sesión; <c>null</c> si ninguno.</summary>
    private DbContext? _contextoDelLote;

    /// <summary>El Tenant de sesión que necesita el lote en curso (puede ser <c>null</c>: sin Tenant).</summary>
    private Guid? _tenantDelLote;

    /// <summary>
    /// El <see cref="AutoTransactionBehavior"/> del contexto antes de forzar
    /// <see cref="AutoTransactionBehavior.Always"/> para este lote; <c>null</c>
    /// cuando no hay lote armado. Se fuerza también dentro de una transacción del
    /// llamador, para que EF no se salte el punto de guardado en un lote de un
    /// solo comando (revisión puente, hallazgo 1).
    /// </summary>
    private AutoTransactionBehavior? _autoTransaccionPrevia;

    /// <summary>
    /// <c>true</c> si el lote armado corre en una transacción del llamador (se
    /// mueve el contexto en el punto de guardado de EF); <c>false</c> si en su
    /// transacción propia (se mueve tras el <c>BEGIN</c>).
    /// </summary>
    private bool _loteEnTransaccionDelLlamador;

    /// <summary>
    /// <c>true</c> desde que se abre el ámbito transaccional que corresponde al
    /// lote preparado (el <c>BEGIN</c> de su transacción propia o el punto de
    /// guardado en la del llamador) hasta que su primer comando de
    /// <c>SaveChanges</c> mueve el contexto. Abrir el ámbito no ejecuta nada: un
    /// <c>BEGIN</c> o un punto de guardado manual del llamador con un lote preparado
    /// y sin ejecutar solo pone esta marca, que el <c>SaveChanges</c> siguiente
    /// borra al cerrar ese lote.
    /// </summary>
    private bool _ambitoDelLoteAbierto;

    /// <summary>
    /// Lo que la conexión tenía antes de que este lote moviera el contexto, y que
    /// hay que devolverle al terminar. <c>null</c> cuando no hay nada movido
    /// pendiente de devolver.
    /// </summary>
    private ValoresDeSesion? _restauracion;

    /// <summary>
    /// <c>true</c> si el contexto se movió dentro del punto de guardado que EF
    /// creó en una transacción del llamador; <c>false</c> si dentro de la
    /// transacción propia del lote.
    /// </summary>
    private bool _movidoEnPuntoDeGuardado;

    private sealed record ValoresDeSesion(DbContext Contexto, string TenantId, string ContextoFirmado);

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is DbContext context)
            await SellarYValidarAsync(context);

        return await base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    /// <summary>
    /// La versión síncrona también sella (hallazgo N-15 de
    /// Project-Hydra-Negocio/seguridad/INFORME-AUDITORIA-2.md). Hoy no hay ningún <c>SaveChanges()</c>
    /// síncrono en el código, así que sobrescribir solo la asíncrona era
    /// inocuo — pero el día que aparezca uno, saltarse el sellado no daría
    /// ningún error: guardaría la fila con <c>TenantId</c> vacío o permitiría
    /// modificar la de otro tenant, en silencio. Un agujero que se abre por
    /// omisión no debería depender de que nadie escriba una línea corriente.
    /// </summary>
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is DbContext context)
            SellarYValidarAsync(context).GetAwaiter().GetResult();

        return base.SavingChanges(eventData, result);
    }

    // ── Los cuatro finales de un SaveChanges ─────────────────────────────────
    //
    // Éxito (SavedChanges), fallo (SaveChangesFailed), cancelación
    // (SaveChangesCanceled) y conflicto de concurrencia
    // (ThrowingConcurrencyException). Los dos últimos NO pasan por
    // SaveChangesFailed (medido). En los tres primeros la conexión está libre y se
    // restaura ahí mismo; en el conflicto lo deshace el ROLLBACK del lote.

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is DbContext context)
            await TerminarLoteAsync(context, propagarFallo: true);

        return await base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    /// <summary>Versión síncrona — ver <see cref="SavingChanges"/> para el porqué.</summary>
    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        if (eventData.Context is DbContext context)
            TerminarLoteAsync(context, propagarFallo: true).GetAwaiter().GetResult();

        return base.SavedChanges(eventData, result);
    }

    public override async Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is DbContext context)
            await TerminarLoteAsync(context, propagarFallo: false);

        await base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

    /// <summary>Versión síncrona — ver <see cref="SavingChanges"/> para el porqué.</summary>
    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        if (eventData.Context is DbContext context)
            TerminarLoteAsync(context, propagarFallo: false).GetAwaiter().GetResult();

        base.SaveChangesFailed(eventData);
    }

    /// <summary>
    /// EF llama a <c>SaveChangesCanceled</c>, no a <c>SaveChangesFailed</c>,
    /// cuando el <c>SaveChanges</c> se cancela (hallazgo P2 de Codex, 8ª ronda).
    /// </summary>
    public override async Task SaveChangesCanceledAsync(DbContextEventData eventData, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is DbContext context)
            await TerminarLoteAsync(context, propagarFallo: false);

        await base.SaveChangesCanceledAsync(eventData, cancellationToken);
    }

    /// <summary>Versión síncrona — ver <see cref="SaveChangesCanceledAsync"/>.</summary>
    public override void SaveChangesCanceled(DbContextEventData eventData)
    {
        if (eventData.Context is DbContext context)
            TerminarLoteAsync(context, propagarFallo: false).GetAwaiter().GetResult();

        base.SaveChangesCanceled(eventData);
    }

    /// <summary>
    /// <c>SaveChangesFailed</c> NO se invoca cuando EF va a lanzar un
    /// <see cref="DbUpdateConcurrencyException"/>: EF avisa por este gancho,
    /// justo antes de lanzar, con el lector del lote todavía abierto en la
    /// conexión. <b>Aquí no se puede ejecutar nada</b>: un <c>set_config</c> desde
    /// aquí lanza <c>NpgsqlOperationInProgressException</c> y EF lo envuelve en un
    /// <see cref="DbUpdateException"/> que sustituye al de concurrencia —lo que
    /// rompería a quien lo captura: <c>UserStore</c>, <c>ConcurrenciaBehavior</c>—.
    /// Medido, no supuesto.
    ///
    /// <para>
    /// Tampoco hace falta: el contexto se movió dentro del ámbito transaccional
    /// del lote, y lo que EF hace a continuación lo deshace en PostgreSQL antes de
    /// que la excepción llegue al llamador. Con la transacción propia del lote, el
    /// <c>Dispose</c> sin confirmar es un <c>ROLLBACK</c>; si ese <c>ROLLBACK</c>
    /// fallara, la conexión queda rota y no ejecuta nada más. Con la transacción
    /// del llamador, EF vuelve a su punto de guardado: el éxito lo recoge
    /// <see cref="IDbTransactionInterceptor.RolledBackToSavepoint"/> y el fallo,
    /// que EF registra y se traga, <see cref="IDbTransactionInterceptor.TransactionFailed"/>,
    /// que restaura en ese momento, ya con la conexión libre. Si nadie suprime la
    /// excepción; si se suprime, el lote continúa y lo cierra <c>SavedChanges</c>.
    /// </para>
    /// </summary>
    public override ValueTask<InterceptionResult> ThrowingConcurrencyExceptionAsync(
        ConcurrencyExceptionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        TrasConflicto(eventData.Context, result);
        return base.ThrowingConcurrencyExceptionAsync(eventData, result, cancellationToken);
    }

    /// <summary>Versión síncrona — ver <see cref="ThrowingConcurrencyExceptionAsync"/>.</summary>
    public override InterceptionResult ThrowingConcurrencyException(
        ConcurrencyExceptionEventData eventData, InterceptionResult result)
    {
        TrasConflicto(eventData.Context, result);
        return base.ThrowingConcurrencyException(eventData, result);
    }

    private void TrasConflicto(DbContext? context, InterceptionResult result)
    {
        if (result.IsSuppressed || context is null || !ReferenceEquals(context, _contextoDelLote))
            return;

        // Transacción propia del lote: su ROLLBACK lo deshace todo. En un punto de
        // guardado del llamador se espera a saber si EF pudo volver a él.
        if (!_movidoEnPuntoDeGuardado)
            _restauracion = null;
        CerrarLote(context);
    }

    // ── Dentro del ámbito transaccional del lote ─────────────────────────────

    /// <summary>
    /// Transacción propia del lote (sin transacción del llamador, con
    /// <see cref="AutoTransactionBehavior.Always"/> forzado): el <c>BEGIN</c> abre
    /// el ámbito y el primer comando del lote mueve el contexto
    /// (<see cref="AntesDelComandoDelLoteAsync"/>). Si la estrategia de reintentos
    /// repite el lote, cada intento abre su transacción y lo vuelve a mover; el
    /// anterior lo deshizo su <c>ROLLBACK</c>. Aquí no se ejecuta nada: si este
    /// <c>BEGIN</c> no es el del lote sino uno del llamador tras un lote preparado
    /// sin ejecutar, no hay nada que deshacer.
    /// </summary>
    DbTransaction IDbTransactionInterceptor.TransactionStarted(
        DbConnection connection, TransactionEndEventData eventData, DbTransaction result)
    {
        AbrirAmbitoDelLote(eventData.Context, enPuntoDeGuardado: false);
        return result;
    }

    ValueTask<DbTransaction> IDbTransactionInterceptor.TransactionStartedAsync(
        DbConnection connection, TransactionEndEventData eventData, DbTransaction result,
        CancellationToken cancellationToken)
    {
        AbrirAmbitoDelLote(eventData.Context, enPuntoDeGuardado: false);
        return new(result);
    }

    /// <summary>
    /// Transacción del llamador: el punto de guardado que EF crea para el lote abre
    /// el ámbito, y el primer comando del lote mueve el contexto, así que el
    /// <c>ROLLBACK TO SAVEPOINT</c> del lote lo deshace. Cualquier punto de guardado
    /// del llamador captura siempre el contexto de antes del lote: el lote no lo
    /// mueve hasta su primer comando y lo devuelve en su final.
    /// </summary>
    void IDbTransactionInterceptor.CreatedSavepoint(DbTransaction transaction, TransactionEventData eventData) =>
        AbrirAmbitoDelLote(eventData.Context, enPuntoDeGuardado: true);

    Task IDbTransactionInterceptor.CreatedSavepointAsync(
        DbTransaction transaction, TransactionEventData eventData, CancellationToken cancellationToken)
    {
        AbrirAmbitoDelLote(eventData.Context, enPuntoDeGuardado: true);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Solo memoria: anota que el ámbito transaccional que corresponde al lote
    /// preparado está abierto. Sin transacción del llamador el lote va en
    /// transacción propia; con ella, en el punto de guardado de EF. El otro gancho
    /// no es el del lote.
    /// </summary>
    private void AbrirAmbitoDelLote(DbContext? context, bool enPuntoDeGuardado)
    {
        if (context is not null && ReferenceEquals(context, _contextoDelLote)
            && enPuntoDeGuardado == _loteEnTransaccionDelLlamador)
            _ambitoDelLoteAbierto = true;
    }

    // ── El primer comando del lote ───────────────────────────────────────────

    InterceptionResult<DbDataReader> IDbCommandInterceptor.ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        AntesDelComandoDelLoteAsync(command, eventData).GetAwaiter().GetResult();
        return result;
    }

    async ValueTask<InterceptionResult<DbDataReader>> IDbCommandInterceptor.ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken)
    {
        await AntesDelComandoDelLoteAsync(command, eventData);
        return result;
    }

    InterceptionResult<int> IDbCommandInterceptor.NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        AntesDelComandoDelLoteAsync(command, eventData).GetAwaiter().GetResult();
        return result;
    }

    async ValueTask<InterceptionResult<int>> IDbCommandInterceptor.NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken)
    {
        await AntesDelComandoDelLoteAsync(command, eventData);
        return result;
    }

    InterceptionResult<object> IDbCommandInterceptor.ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        AntesDelComandoDelLoteAsync(command, eventData).GetAwaiter().GetResult();
        return result;
    }

    async ValueTask<InterceptionResult<object>> IDbCommandInterceptor.ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
        CancellationToken cancellationToken)
    {
        await AntesDelComandoDelLoteAsync(command, eventData);
        return result;
    }

    /// <summary>
    /// Antes del primer comando del <c>SaveChanges</c> del lote preparado, ya
    /// dentro de su ámbito transaccional, mueve el contexto. Solo los comandos cuyo
    /// origen es <see cref="CommandSource.SaveChanges"/>: una consulta, un
    /// <c>ExecuteSqlRaw</c> o un <c>ExecuteUpdate</c> del llamador nunca lo mueven.
    /// Un lote preparado cuyo primer comando llega sin ámbito abierto no se
    /// ejecuta: no habría nada que deshiciera el movimiento. Los comandos
    /// siguientes del mismo lote ya corren con el contexto movido. El error de
    /// cualquier paso lo recoge EF dentro del lote, que deshace su transacción o
    /// vuelve a su punto de guardado.
    /// </summary>
    private async Task AntesDelComandoDelLoteAsync(DbCommand command, CommandEventData eventData)
    {
        if (eventData.CommandSource != CommandSource.SaveChanges
            || eventData.Context is not { } context
            || !ReferenceEquals(context, _contextoDelLote))
            return;

        if (!_ambitoDelLoteAbierto)
        {
            if (_restauracion is not null)
                return;
            throw new InvalidOperationException(
                "Un lote que mueve el Tenant de sesión llegó a su primer comando sin ámbito transaccional propio; el lote no se ejecuta.");
        }

        _ambitoDelLoteAbierto = false;
        await AplicarTenantDelLoteAsync(context, command.Connection ?? context.Database.GetDbConnection());
    }

    /// <summary>
    /// EF volvió al punto de guardado del lote (fallo o conflicto dentro de una
    /// transacción del llamador): PostgreSQL ya devolvió el contexto al de antes
    /// del lote, no queda nada que restaurar.
    /// </summary>
    void IDbTransactionInterceptor.RolledBackToSavepoint(DbTransaction transaction, TransactionEventData eventData) =>
        TrasVolverAlPuntoDeGuardado(eventData.Context);

    Task IDbTransactionInterceptor.RolledBackToSavepointAsync(
        DbTransaction transaction, TransactionEventData eventData, CancellationToken cancellationToken)
    {
        TrasVolverAlPuntoDeGuardado(eventData.Context);
        return Task.CompletedTask;
    }

    /// <summary>
    /// EF no pudo volver al punto de guardado del lote (lo registra y se lo
    /// traga): si el contexto movido sigue en la transacción del llamador, se
    /// restaura aquí, con la conexión ya libre, antes de que la excepción del lote
    /// llegue al llamador. Si la restauración también falla, o la base la rechazó
    /// (transacción abortada) o la conexión quedó rota.
    /// </summary>
    void IDbTransactionInterceptor.TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData)
    {
        if (EsVueltaFallidaAlPuntoDelLote(eventData))
            RestaurarAsync(propagarFallo: false).GetAwaiter().GetResult();
    }

    async Task IDbTransactionInterceptor.TransactionFailedAsync(
        DbTransaction transaction, TransactionErrorEventData eventData, CancellationToken cancellationToken)
    {
        if (EsVueltaFallidaAlPuntoDelLote(eventData))
            await RestaurarAsync(propagarFallo: false);
    }

    private bool EsVueltaFallidaAlPuntoDelLote(TransactionErrorEventData eventData) =>
        _restauracion is { } previo && _movidoEnPuntoDeGuardado
        && ReferenceEquals(eventData.Context, previo.Contexto)
        && eventData.Action.Contains("RollbackToSavepoint", StringComparison.Ordinal);

    private void TrasVolverAlPuntoDeGuardado(DbContext? context)
    {
        if (_restauracion is { } previo && _movidoEnPuntoDeGuardado && ReferenceEquals(context, previo.Contexto))
            _restauracion = null;
    }

    // ── Mover y devolver el contexto ─────────────────────────────────────────

    /// <summary>
    /// Mueve <c>app.tenant_id</c> y el contexto firmado de la conexión al Tenant
    /// que necesita el lote en curso, antes de su primer comando, dentro de su
    /// transacción propia o del punto de guardado de EF en la del llamador
    /// (<see cref="AntesDelComandoDelLoteAsync"/>). Primero lee lo que la conexión tiene (para devolvérselo tal cual)
    /// y después lo cambia. El token del lote se firma con la clave en memoria
    /// sobre el contexto de base de la conexión (<see cref="FirmanteContextoRls.FirmarConTenantAsync"/>)
    /// y no se confirma: la memoria del firmante sigue describiendo el contexto
    /// de base, que es el que la conexión vuelve a tener al terminar el lote. Sin
    /// clave, el token es vacío y el lote corre sin contexto firmado: falla
    /// cerrado.
    /// </summary>
    private async Task AplicarTenantDelLoteAsync(DbContext context, DbConnection conexion)
    {
        await using (var lectura = conexion.CreateCommand())
        {
            lectura.CommandText =
                "SELECT COALESCE(current_setting('app.tenant_id', true), ''), COALESCE(current_setting('app.contexto', true), '');";
            await using var lector = await lectura.ExecuteReaderAsync(CancellationToken.None);
            if (!await lector.ReadAsync(CancellationToken.None))
                throw new InvalidOperationException("No se pudo leer el contexto de sesión antes de mover el del lote.");
            // Antes de cambiar nada: si el cambio fallara a medias, devolver esto
            // es inocuo (es lo que la conexión ya tiene).
            _restauracion = new ValoresDeSesion(context, lector.GetString(0), lector.GetString(1));
            _movidoEnPuntoDeGuardado = _loteEnTransaccionDelLlamador;
        }

        var token = await FirmanteContextoRls.FirmarConTenantAsync(conexion, _tenantDelLote, CancellationToken.None);
        await FijarAsync(conexion, _tenantDelLote?.ToString() ?? string.Empty, token?.Token);
    }

    /// <summary>
    /// Devuelve a la conexión lo que tenía antes de que el lote moviera el
    /// contexto. Con la conexión cerrada no hay nada que devolver: el pool
    /// descarta la sesión y la apertura siguiente la prepara entera. Si falla
    /// fuera de una transacción, la conexión física se cierra
    /// (<see cref="TenantRlsConnectionInterceptor.DescartarSesionAsync"/>): ningún
    /// comando, de EF o directo, corre con el contexto del lote. Dentro de una
    /// transacción, un fallo en la base la deja abortada y uno del cliente deja la
    /// conexión rota. Sin token de cancelación: restaurar es limpieza, no trabajo
    /// cancelable (hallazgo P2 de Codex, 8ª ronda). <paramref name="propagarFallo"/>
    /// es <c>false</c> en los finales con error, para no sustituir su excepción.
    /// </summary>
    private async Task RestaurarAsync(bool propagarFallo)
    {
        if (_restauracion is not { } previo)
            return;

        var context = previo.Contexto;
        var conexion = context.Database.GetDbConnection();
        if (conexion.State != System.Data.ConnectionState.Open)
        {
            _restauracion = null;
            return;
        }

        try
        {
            await FijarAsync(conexion, previo.TenantId, previo.ContextoFirmado);
            _restauracion = null;
        }
        catch (Exception ex)
        {
            // Nada queda pendiente para después: devolver más tarde unos valores
            // leídos antes podría pisar una realineación posterior. Dentro de una
            // transacción, el error deja la transacción abortada (en la base) o la
            // conexión rota (en el cliente), y su ROLLBACK devuelve el valor del
            // BEGIN, anterior al lote. Fuera, se cierra la conexión física.
            _restauracion = null;
            log?.LogError(ex, "No se pudo devolver a la conexión el contexto de sesión anterior al lote.");
            if (context.Database.CurrentTransaction is null)
            {
                try
                {
                    await TenantRlsConnectionInterceptor.DescartarSesionAsync(conexion);
                }
                catch (Exception exDescarte) when (!propagarFallo)
                {
                    // En un final con error no se sustituye su excepción.
                    log?.LogError(exDescarte, "No se pudo cerrar la conexión física tras fallar la devolución del contexto.");
                }
            }

            if (propagarFallo)
                throw;
        }
    }

    /// <summary>
    /// Un movimiento del contexto que sigue anotado al empezar otro
    /// <c>SaveChanges</c> no debería existir (cada final lo devuelve o lo da por
    /// deshecho). Si existiera, no se devuelven valores leídos antes, que podrían
    /// pisar una realineación posterior: la sesión se trata como desconocida. Fuera
    /// de una transacción se cierra la conexión física (la apertura siguiente la
    /// prepara entera); dentro, el lote no se ejecuta.
    /// </summary>
    private async Task DescartarMovimientoPendienteAsync()
    {
        if (_restauracion is not { } previo)
            return;

        _restauracion = null;
        var conexion = previo.Contexto.Database.GetDbConnection();
        if (conexion.State != System.Data.ConnectionState.Open)
            return;

        if (previo.Contexto.Database.CurrentTransaction is not null)
            throw new InvalidOperationException(
                "El contexto de sesión de un lote anterior sigue movido dentro de esta transacción; el lote no se ejecuta.");

        await TenantRlsConnectionInterceptor.DescartarSesionAsync(conexion);
    }

    /// <summary>
    /// <c>set_config</c> de sesión (<c>false</c>) de <c>app.tenant_id</c> y, si hay
    /// contexto firmado que tocar, de <c>app.contexto</c>. Como función
    /// parametrizada, igual que <c>TenantRlsConnectionInterceptor</c>.
    /// <c>valorTenantId</c> acepta cadena vacía a propósito: es el centinela de
    /// «sin tenant» (fallo cerrado, <c>NULLIF(..., '')::uuid</c> da <c>NULL</c>).
    /// </summary>
    private static async Task FijarAsync(DbConnection conexion, string valorTenantId, string? contexto)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = contexto is null
            ? "SELECT set_config('app.tenant_id', @tenantId, false);"
            : "SELECT set_config('app.tenant_id', @tenantId, false), set_config('app.contexto', @contexto, false);";
        var parametro = comando.CreateParameter();
        parametro.ParameterName = "tenantId";
        parametro.Value = valorTenantId;
        comando.Parameters.Add(parametro);
        if (contexto is not null)
        {
            var parametroContexto = comando.CreateParameter();
            parametroContexto.ParameterName = "contexto";
            parametroContexto.Value = contexto;
            comando.Parameters.Add(parametroContexto);
        }
        await comando.ExecuteNonQueryAsync(CancellationToken.None);
    }

    /// <summary>
    /// El lote en curso necesita el Tenant de sesión <paramref name="tenant"/>.
    /// Se fuerza <see cref="AutoTransactionBehavior.Always"/> para que el lote tenga
    /// siempre un ámbito propio dentro del cual moverlo: su transacción sin
    /// transacción del llamador, y con ella el punto de guardado automático de EF; sin él (o con una transacción ambiente, que EF no
    /// gestiona) no habría ámbito que lo deshiciera, y se falla cerrado antes de
    /// escribir nada. Ninguna de las dos configuraciones existe hoy en el código.
    /// </summary>
    private void PrepararLote(DbContext context, Guid? tenant)
    {
        if (System.Transactions.Transaction.Current is not null)
            throw new InvalidOperationException(
                "Un lote que mueve el Tenant de sesión no puede ejecutarse en una transacción ambiente.");

        var enTransaccionDelLlamador = context.Database.CurrentTransaction is not null;
        if (enTransaccionDelLlamador && !context.Database.AutoSavepointsEnabled)
            throw new InvalidOperationException(
                "Un lote que mueve el Tenant de sesión dentro de una transacción necesita los puntos de guardado automáticos de EF.");

        // Always en los dos casos: sin transacción del llamador, EF abre siempre la
        // suya; con ella, no se salta el punto de guardado aunque el lote sea de un
        // solo comando.
        _autoTransaccionPrevia = context.Database.AutoTransactionBehavior;
        context.Database.AutoTransactionBehavior = AutoTransactionBehavior.Always;
        _loteEnTransaccionDelLlamador = enTransaccionDelLlamador;
        _contextoDelLote = context;
        _tenantDelLote = tenant;
    }

    /// <summary>Final con la conexión libre: devolver el contexto y cerrar el lote.</summary>
    private async Task TerminarLoteAsync(DbContext context, bool propagarFallo)
    {
        if (!ReferenceEquals(context, _contextoDelLote) && !ReferenceEquals(context, _restauracion?.Contexto))
            return;

        try
        {
            await RestaurarAsync(propagarFallo);
        }
        finally
        {
            CerrarLote(context);
        }
    }

    /// <summary>Solo memoria: devuelve el <see cref="AutoTransactionBehavior"/> del llamador y olvida el lote.</summary>
    private void CerrarLote(DbContext context)
    {
        if (_autoTransaccionPrevia is { } previa && ReferenceEquals(context, _contextoDelLote))
            context.Database.AutoTransactionBehavior = previa;

        _autoTransaccionPrevia = null;
        _loteEnTransaccionDelLlamador = false;
        _ambitoDelLoteAbierto = false;
        _contextoDelLote = null;
        _tenantDelLote = null;
    }

    private async Task SellarYValidarAsync(DbContext context)
    {
        // Un SaveChanges anterior que no llegó a ejecutar su lote (otro interceptor
        // de SavingChanges o el DetectChanges posterior lanzaron fuera del try de
        // EF, que entonces no avisa por ningún final) dejó el suyo preparado. No
        // movió nada: solo lo mueve un comando de SaveChanges, y este es el primer
        // SaveChanges desde entonces. Se cierra antes de decidir nada de este (y
        // con él la marca de un ámbito que el llamador abriera entre medias): no
        // puede aplicarse a otro lote.
        await DescartarMovimientoPendienteAsync();
        if (_contextoDelLote is { } anterior)
            CerrarLote(anterior);

        var tenantId = tenantActual.TenantId;
        var hayTenantDelLote = false;
        Guid? tenantDelLote = null;

        // Conexión retenida por una transacción explícita que cambió de ámbito
        // (AmbitoTenantExplicito) después de abrirse: TenantRlsConnectionInterceptor
        // fijó app.tenant_id al abrirla, con el Tenant de entonces, y fuera de una
        // transacción cada SaveChanges abriría una conexión nueva con el de ahora.
        // El lote se ejecuta con ITenantActual —lo mismo que tendría una conexión
        // recién abierta, nada que el ámbito no conceda ya—, movido dentro de su
        // punto de guardado como el sellado de Identity de abajo. Sin esto,
        // escribir en dos Tenants dentro de una misma transacción (el alta de un
        // Gestor CAE con su cartera, CrearUsuarioCommand) fallaba cerrado con 42501.
        // Solo con transacción abierta: sin ella, una conexión que sigue abierta
        // (la retenida por la operación de PuertaAccesoDatos, o una abierta a
        // mano) ya la ha realineado TenantRlsConnectionInterceptor con el
        // contexto actual al empezar este SaveChanges, antes que este sellado
        // (va registrado delante, ver ConfiguracionDeContexto), y dentro de una
        // transacción no se realinea.
        if (context.Database.CurrentTransaction is not null
            && FirmanteContextoRls.TryObtenerTenantBase(context.Database.GetDbConnection(), out var tenantDeLaConexion)
            && tenantDeLaConexion != tenantId)
        {
            hayTenantDelLote = true;
            tenantDelLote = tenantId;
        }

        // Un solo Tenant de sesión por lote, y solo cuando hace falta: todas
        // las filas de Identity de un SaveChanges pertenecen hoy a la MISMA
        // cuenta (UserManager opera sobre un ApplicationUser a la vez, y sus
        // roles, logins y tokens cuelgan de él). Si algún día un mismo
        // SaveChanges auditara cuentas de DOS tenants distintos a la vez, la
        // variable de sesión solo reflejaría la última y RLS rechazaría las
        // demás con 42501 —la política compara contra un único app.tenant_id
        // por conexión—; el lote entero se revertiría, así que sería un fallo
        // ruidoso, no una fila sellada con el tenant equivocado. Lo mismo
        // vale para un lote que mezcle Identity de otro tenant con una
        // escritura de dominio del tenant de sesión: se ejecuta con la
        // variable del propietario de la cuenta y falla entero. Demostrado
        // bajo cae_app_runtime, para INSERT, UPDATE y DELETE, con restauración
        // de la variable, en LoteMixtoIdentidadDominioFallaCerradoTests. La
        // revisión de Codex de la misión N6/V4 no encontró ningún camino de
        // producción que construya el lote, y la lectura posterior (misión
        // T5) tampoco lo encontró por diseño: queda fuera de alcance
        // ampliarlo sin que aparezca un caso real.

        // ToList: la rama de reclasificación de abajo cambia el State de una
        // entrada, y eso no puede hacerse mientras se enumera el ChangeTracker.
        foreach (var entrada in context.ChangeTracker.Entries<EntidadConTenant>().ToList())
        {
            switch (entrada.State)
            {
                case EntityState.Added:
                    // El tenant PROPIETARIO de la cuenta auditada manda
                    // siempre que exista (Identity, ver
                    // ResolverTenantDeIdentidadAuditada) — nunca el tenant
                    // del CONTEXTO/sesión. Antes este orden estaba invertido
                    // (tenantId ?? fallback): un Gestor CAE de un Operador
                    // CAE externo, operando en el Workspace operativo
                    // derivado de un Tenant beneficiario
                    // (TenantActual.TenantId = clienteActivoSeleccionado.
                    // TenantIdSeleccionado ?? tenantId, ver TenantActual.cs),
                    // que cambiaba su propio tema/teléfono/2FA generaba una
                    // fila de auditoría sellada con el TenantId del
                    // BENEFICIARIO en vez del Tenant propietario real de su
                    // cuenta — el administrador del beneficiario veía en SU
                    // auditoría la actividad de una cuenta ajena: mezcla del
                    // plano de Operación con el de Propiedad (ADR-011),
                    // hallazgo ALTA de sesión coordinadora, confirmado por
                    // PRUEBA DIRECTA antes de corregir (ver
                    // PropagacionTenantRlsSinSesionTests). Para el resto de
                    // entidades (dominio), ResolverTenantDeIdentidadAuditada
                    // siempre devuelve null, así que el comportamiento no
                    // cambia: sigue siendo tenantId (el único caso posible).
                    //
                    // P1-M1: ese traslado de app.tenant_id vale para TODO el
                    // lote, también para el UPDATE de AspNetUsers, cuya
                    // política de modificación compara contra él. Por eso
                    // solo se hace en los dos casos legítimos —sin Tenant de
                    // sesión (identificación previa al login) o sobre la
                    // PROPIA cuenta—: con Tenant de sesión X y la cuenta de
                    // otra persona del Tenant Y, trasladarlo convertiría la
                    // auditoría en un salvoconducto para escribir cuentas de
                    // Y desde X. En ese caso manda la sesión, la política
                    // deja el UPDATE en cero filas y el lote entero se
                    // revierte (DbUpdateConcurrencyException).
                    var tenantDeLaCuenta = ResolverTenantDeIdentidadAuditada(context, entrada.Entity);
                    var tenantParaEsta = tenantDeLaCuenta is not null
                        && (tenantId is null || tenantDeLaCuenta == tenantId
                            || await EsLaPropiaCuentaAsync((RegistroAuditoria)entrada.Entity))
                        ? tenantDeLaCuenta
                        : tenantId;
                    if (tenantParaEsta is null)
                        throw new InvalidOperationException(
                            $"No se puede crear una entidad de tipo {entrada.Entity.GetType().Name} sin un tenant resuelto (ver ITenantActual).");

                    // Hallazgo P1 de Codex (5ª ronda) + hallazgo ALTA de
                    // sesión coordinadora: sin esto, el valor de abajo queda
                    // solo en el objeto .NET — la política RLS de
                    // "RegistrosAuditoria" compara contra
                    // current_setting('app.tenant_id'), que
                    // TenantRlsConnectionInterceptor fija al abrir la conexión
                    // (o al realinear una retenida, antes de este sellado:
                    // va registrado delante) con el tenant de SESIÓN (que puede
                    // ser null —sin sesión— o distinto del propietario real
                    // —workspace delegado—). Bajo el rol restringido
                    // cae_app_runtime (confirmado en producción, ver
                    // hydra-rls-fallo-cerrado-prerrequisito-despliegue), el
                    // INSERT lo rechaza Postgres con 42501 si no coinciden. El
                    // lote se ejecuta con el Tenant de la cuenta dentro de su
                    // propio ámbito transaccional (AplicarTenantDelLoteAsync),
                    // y la conexión vuelve al de sesión en los cuatro finales
                    // de un SaveChanges (éxito, fallo, concurrencia,
                    // cancelación).
                    if (tenantParaEsta != tenantId)
                    {
                        hayTenantDelLote = true;
                        tenantDelLote = tenantParaEsta;
                    }

                    entrada.Property(nameof(EntidadConTenant.TenantId)).CurrentValue = tenantParaEsta.Value;
                    break;

                case EntityState.Modified when (Guid)entrada.Property(nameof(EntidadConTenant.TenantId)).OriginalValue! == Guid.Empty:
                    // Entidad NUEVA que el fixup de DetectChanges clasificó
                    // como Modified: las entidades hijas de un agregado
                    // cargado (p. ej. un rol nuevo de un ContactoAgenda ya
                    // existente) las descubre EF por la navegación, no por
                    // un Add explícito, y como Entity asigna el Guid en el
                    // constructor la clave "ya está puesta" y EF asume que
                    // la fila existe. Un TenantId ORIGINAL vacío no puede
                    // venir de la base de datos (columna NOT NULL, sellada
                    // en el insert), así que es la firma inequívoca de este
                    // caso — se reclasifica como inserción y se sella.
                    // Mensaje no pasa por aquí: declara su Id
                    // ValueGeneratedNever y EF ya lo clasifica como Added
                    // (ver MensajeConfiguration). Una fila
                    // REAL de otro tenant nunca entra por esta rama: su
                    // original viene de BD y no está vacío, y sigue cayendo
                    // al rechazo de abajo.
                    if (tenantId is null)
                        throw new InvalidOperationException(
                            $"No se puede crear una entidad de tipo {entrada.Entity.GetType().Name} sin un tenant resuelto (ver ITenantActual).");

                    entrada.State = EntityState.Added;
                    entrada.Property(nameof(EntidadConTenant.TenantId)).CurrentValue = tenantId.Value;
                    break;

                case EntityState.Modified:
                case EntityState.Deleted:
                    var tenantOriginal = (Guid)entrada.Property(nameof(EntidadConTenant.TenantId)).OriginalValue!;
                    if (tenantOriginal != tenantId)
                        throw new InvalidOperationException(
                            $"No se puede modificar una entidad de tipo {entrada.Entity.GetType().Name} perteneciente a otro tenant.");
                    break;
            }
        }

        // Al final, cuando ya no puede lanzar nada de este método: un lote
        // preparado y no ejecutado lo recoge el SaveChanges siguiente (arriba).
        if (hayTenantDelLote)
            PrepararLote(context, tenantDelLote);
    }

    /// <summary>
    /// Solo para <see cref="RegistroAuditoria"/> de las excepciones de
    /// Identity que <c>AuditoriaInterceptor</c> audita fuera del namespace de
    /// dominio (<c>ApplicationUser</c>, <c>IdentityUserRole{Guid}</c>,
    /// <c>IdentityUserLogin{Guid}</c>, <c>IdentityUserToken{Guid}</c> — ver
    /// <c>EntidadTipoAuditoria</c>, la única fuente de esos nombres). A
    /// diferencia de una entidad de dominio, cuyo
    /// TenantId solo puede venir de una sesión resuelta —nunca de la propia
    /// entidad, que sería confiar en un dato que el llamante controla—, aquí
    /// el tenant no es una decisión de autorización: es un hecho que ya vive
    /// en la fila que se está auditando (<c>ApplicationUser.TenantId</c>, NOT
    /// NULL). <c>UserManager</c> escribe esa fila en varios caminos sin
    /// sesión ni <c>AmbitoTenantExplicito</c> —un intento de contraseña
    /// incorrecto en <c>Login.razor</c> incrementa <c>AccessFailedCount</c>,
    /// <c>RestablecerContrasena.razor.cs</c> escribe desde un enlace anónimo,
    /// el vínculo por SSO de <c>IdentityEndpointsExtensions</c> corre antes de
    /// que exista sesión de CAE Manager—; sin esta excepción, el fallo
    /// cerrado de arriba revertía el <c>SaveChanges</c> entero y esos tres
    /// caminos dejaban de funcionar (hallazgo de Codex antes de abrir la PR
    /// que introdujo la auditoría de Identity).
    ///
    /// El <see cref="ApplicationUser"/> referenciado por <c>EntidadId</c>
    /// está en el mismo <c>DbContext</c> que el <c>RegistroAuditoria</c> que
    /// lo audita: o es la entidad que disparó su creación (alta/edición de la
    /// cuenta), o es el usuario que <c>UserManager</c> recibió como argumento
    /// y que este mismo contexto cargó antes de escribir su rol, su login
    /// externo o su token (<c>UserStore</c> opera sobre el
    /// <c>CaeManagerDbContext</c> del scope, ver
    /// <c>AddEntityFrameworkStores</c>). Por eso basta con buscarlo entre las
    /// entradas del <c>ChangeTracker</c> —incluidas las <c>Unchanged</c>, que
    /// es como queda un usuario recién cargado por <c>FindByIdAsync</c>—: no
    /// añade una consulta a base de datos ni ninguna confianza nueva, lee un
    /// valor que la propia operación ya tenía delante. Si no se
    /// encuentra, sigue fallando cerrado como cualquier otra entidad: el
    /// tenant lo pondrá la sesión, o no habrá tenant y el <c>SaveChanges</c>
    /// se rechazará en vez de escribir una fila que no se sabe de quién es.
    /// </summary>
    private async Task<bool> EsLaPropiaCuentaAsync(RegistroAuditoria registro) =>
        currentUserService is not null
        && await currentUserService.ObtenerUsuarioActualIdAsync() is { } usuarioId
        && usuarioId == registro.EntidadId;

    private static Guid? ResolverTenantDeIdentidadAuditada(DbContext context, object entidad)
    {
        if (entidad is not RegistroAuditoria registro) return null;
        if (!EntidadTipoAuditoria.TodosLosDeIdentidad.Contains(registro.EntidadTipo)) return null;

        var usuario = context.ChangeTracker.Entries<ApplicationUser>()
            .FirstOrDefault(e => e.Entity.Id == registro.EntidadId)?.Entity;

        return usuario?.TenantId is { } tenantIdUsuario && tenantIdUsuario != Guid.Empty ? tenantIdUsuario : null;
    }
}
