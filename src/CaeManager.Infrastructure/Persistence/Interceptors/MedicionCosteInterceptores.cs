using System.Data.Common;
using CaeManager.Application.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CaeManager.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Solo medición: suma al <see cref="CosteCarga"/> del scope cada comando que EF
/// Core ejecuta y lo que tardó en ejecutarse (Server-Timing <c>db</c> y
/// <c>cmd</c>). No lee ni el texto ni los parámetros del comando, no lo altera
/// y no suprime nada: devuelve siempre el resultado que recibe.
///
/// <para>
/// La duración es la que mide EF Core hasta que el proveedor devuelve el
/// resultado (<see cref="CommandExecutedEventData.Duration"/>): en una
/// consulta, hasta el primer resultado; la materialización de filas posterior
/// no entra. Los comandos que un interceptor de conexión lanza a mano sobre la
/// <see cref="DbConnection"/> (el <c>set_config</c> firmado del contexto RLS)
/// no pasan por EF y no se cuentan aquí: se ven en <c>conn</c>.
/// </para>
/// </summary>
internal sealed class MedicionCosteComandosInterceptor(CosteCarga coste) : DbCommandInterceptor
{
    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        coste.RegistrarComando(eventData.Duration);
        return result;
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    {
        coste.RegistrarComando(eventData.Duration);
        return ValueTask.FromResult(result);
    }

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        coste.RegistrarComando(eventData.Duration);
        return result;
    }

    public override ValueTask<int> NonQueryExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        coste.RegistrarComando(eventData.Duration);
        return ValueTask.FromResult(result);
    }

    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
    {
        coste.RegistrarComando(eventData.Duration);
        return result;
    }

    public override ValueTask<object?> ScalarExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, object? result, CancellationToken cancellationToken = default)
    {
        coste.RegistrarComando(eventData.Duration);
        return ValueTask.FromResult(result);
    }

    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData) =>
        coste.RegistrarComando(eventData.Duration);

    public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        coste.RegistrarComando(eventData.Duration);
        return Task.CompletedTask;
    }

    public override void CommandCanceled(DbCommand command, CommandEndEventData eventData) =>
        coste.RegistrarComando(eventData.Duration);

    public override Task CommandCanceledAsync(DbCommand command, CommandEndEventData eventData, CancellationToken cancellationToken = default)
    {
        coste.RegistrarComando(eventData.Duration);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Solo medición: cuenta en el <see cref="CosteCarga"/> del scope cada apertura
/// de conexión que hace EF Core (Server-Timing <c>conn</c>). No toca la
/// conexión: el contexto RLS lo fija <see cref="TenantRlsConnectionInterceptor"/>,
/// que no cambia por esto.
/// </summary>
internal sealed class MedicionCosteConexionesInterceptor(CosteCarga coste) : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) =>
        coste.RegistrarConexion();

    public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        coste.RegistrarConexion();
        return Task.CompletedTask;
    }
}
