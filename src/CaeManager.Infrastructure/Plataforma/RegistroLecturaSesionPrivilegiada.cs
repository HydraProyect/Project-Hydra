using CaeManager.Application.Plataforma;
using CaeManager.Domain.Common;
using CaeManager.Domain.Soporte;
using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace CaeManager.Infrastructure.Plataforma;

/// <inheritdoc cref="IRegistroLecturaSesionPrivilegiada" />
/// <remarks>
/// Sesión Privilegiada (ADR-011 § 4bis, ver TenantRlsConnectionInterceptor): la conexión adopta
/// cae_app_soporte, que solo tiene SELECT sobre RegistrosActividadSoporte y no lo pierde. La lectura
/// se registra igual por la única puerta de escritura de esa sesión, la función
/// app_registrar_actividad_soporte_por_sesion (migración RegistraActividadSoportePorSesionPrivilegiada).
/// Mismo patrón que RegistroAccesoDocumentoSensibleRepository.GuardarPorSesionPrivilegiadaAsync:
/// valores parametrizados por EF, código de vuelta en vez de excepción, y false ante cualquier
/// rechazo para que quien llama falle en cerrado. Solo registra fuera de una transacción explícita,
/// para que un ROLLBACK posterior no borre la fila de una lectura ya servida.
/// </remarks>
public class RegistroLecturaSesionPrivilegiada(
    CaeManagerDbContext dbContext, ILogger<RegistroLecturaSesionPrivilegiada> logger)
    : IRegistroLecturaSesionPrivilegiada
{
    internal const string Registrado = "registrado";

    public async Task<bool> RegistrarAsync(
        Guid sesionPrivilegiadaId, string operacion, CancellationToken cancellationToken = default)
    {
        if (sesionPrivilegiadaId == Guid.Empty)
        {
            logger.LogError(
                "No se pudo registrar la lectura {Operacion}: no se nombra la Sesión Privilegiada. Los datos no se sirven.",
                operacion);
            return false;
        }

        // La fila tiene que sobrevivir a la lectura que autoriza. Dentro de una transacción explícita
        // (de EF o ambiente) el handler serviría los datos y un ROLLBACK posterior borraría la fila:
        // solo se registra en autocommit. Ningún flujo actual lee bajo Sesión Privilegiada dentro de
        // una transacción; si alguno llega a hacerlo, falla aquí en cerrado.
        if (dbContext.Database.CurrentTransaction is not null || System.Transactions.Transaction.Current is not null)
        {
            logger.LogError(
                "No se registra la lectura {Operacion} en la Sesión Privilegiada {SesionId}: hay una transacción " +
                "abierta que podría deshacer la fila. Los datos no se sirven.",
                operacion, sesionPrivilegiadaId);
            return false;
        }

        var registroId = IdentificadorEntidad.Nuevo();
        var tipo = nameof(TipoActividadSoporte.Lectura);
        string codigo;
        try
        {
            // Los cuatro valores van parametrizados por EF.
            codigo = await dbContext.Database
                .SqlQuery<string>(
                    $"SELECT app_registrar_actividad_soporte_por_sesion({sesionPrivilegiadaId}, {registroId}, {tipo}, {operacion}) AS \"Value\"")
                .SingleAsync(cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            // Contexto RLS firmado inválido, o conexión sin el rol de soporte (sin EXECUTE): por
            // ejemplo, una conexión ya elevada a cae_app_aprovisionamiento.
            logger.LogError(ex,
                "No se pudo registrar la lectura {Operacion} en la Sesión Privilegiada {SesionId}. Los datos no se sirven.",
                operacion, sesionPrivilegiadaId);
            return false;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            // La función espera como mucho lock_timeout el candado SHARE sobre la tabla de sesiones
            // privilegiadas (una sesión abriéndose o cerrándose a la vez): sin la cuenta, no se registra.
            logger.LogError(ex,
                "No se pudo registrar la lectura {Operacion} en la Sesión Privilegiada {SesionId}: venció la espera " +
                "del candado sobre las Sesiones Privilegiadas. Los datos no se sirven.",
                operacion, sesionPrivilegiadaId);
            return false;
        }

        if (codigo == Registrado)
            return true;

        logger.LogError(
            "La base rechazó el registro de la lectura {Operacion} en la Sesión Privilegiada {SesionId} ({Codigo}). " +
            "Los datos no se sirven.",
            operacion, sesionPrivilegiadaId, codigo);
        return false;
    }
}
