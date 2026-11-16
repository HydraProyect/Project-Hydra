using System.Data.Common;
using CaeManager.Domain.Importacion;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class OperacionImportacionRepository(CaeManagerDbContext dbContext) : IOperacionImportacionRepository
{
    private const string IndiceUnicoOperacion = "IX_OperacionesImportacion_TenantId_OperacionId";

    public Task<bool> ExisteAsync(Guid operacionId, CancellationToken cancellationToken = default) =>
        dbContext.OperacionesImportacion.AnyAsync(o => o.OperacionId == operacionId, cancellationToken);

    public Task<bool> EsLaMarcaGuardadaAsync(Guid operacionId, Guid marcaId, CancellationToken cancellationToken = default) =>
        dbContext.OperacionesImportacion.AnyAsync(o => o.OperacionId == operacionId && o.Id == marcaId, cancellationToken);

    public void Agregar(OperacionImportacion operacion) => dbContext.OperacionesImportacion.Add(operacion);

    public async Task<bool> GuardarSiOperacionNuevaAsync(CancellationToken cancellationToken = default)
    {
        // Las operaciones que este guardado marca, leídas antes de guardar: tras el fallo se vuelve a preguntar por ellas.
        var operacionesPendientes = dbContext.ChangeTracker.Entries<OperacionImportacion>()
            .Where(e => e.State == EntityState.Added)
            .Select(e => e.Entity.OperacionId)
            .Distinct()
            .ToList();
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        // Una violación de unicidad es «ya ejecutada» en dos casos (#1992):
        // - la del propio índice de la operación (mismo patrón que
        //   ExpiracionAsignacionesHostedService.GuardarODejarComoEstabaAsync);
        // - la de otro índice (un CIF, una razón social, un DNI) cuando, releída
        //   tras el fallo, la operación ya está guardada: es la perdedora de dos
        //   confirmaciones simultáneas del mismo plan, y PostgreSQL ha detectado
        //   antes la fila repetida que la ganadora acaba de crear que la marca
        //   repetida (las Empresas se insertan antes que las marcas).
        // Si la operación no está guardada, la otra violación es un error real del
        // plan y se propaga tal cual en vez de disfrazarse de «ya ejecutada».
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            var pg = (PostgresException)ex.InnerException!;
            if (pg.ConstraintName != IndiceUnicoOperacion && !await OperacionYaGuardadaAsync(operacionesPendientes, cancellationToken))
                throw;
            // Un SaveChangesAsync fallido NO revierte el estado Added de las
            // entidades en memoria — solo la transacción de la base de datos. Sin
            // este Clear(), el resto del plan (Empresa/Trabajador/Documento/
            // Asignación de la confirmación perdedora) se quedaría marcado para
            // guardar en el siguiente SaveChangesAsync del mismo contexto
            // compartido, aunque nada de eso llegó a persistirse aquí.
            dbContext.ChangeTracker.Clear();
            return false;
        }
    }

    /// <summary>
    /// Si alguna de <paramref name="operacionIds"/> ya está guardada. Dentro de la transacción del llamador, EF ha
    /// vuelto al punto de guardado del lote fallido, así que la lectura es posible; si aun así falla, se responde que
    /// no y el llamador recibe la violación original, como antes de esta relectura.
    /// </summary>
    private async Task<bool> OperacionYaGuardadaAsync(IReadOnlyCollection<Guid> operacionIds, CancellationToken cancellationToken)
    {
        if (operacionIds.Count == 0)
            return false;
        try
        {
            return await dbContext.OperacionesImportacion.AnyAsync(o => operacionIds.Contains(o.OperacionId), cancellationToken);
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            return false;
        }
    }

    public void DescartarPendientes() => dbContext.ChangeTracker.Clear();
}
