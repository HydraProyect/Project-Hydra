using CaeManager.Application.Retencion;
using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Auditing;

/// <inheritdoc cref="IEnmascaradoAuditoriaTrabajadorAnonimizado" />
/// <remarks>
/// La aplicación no tiene UPDATE sobre <c>RegistrosAuditoria</c>
/// (<c>AuditoriaSoloInsercionBajoRuntimeTests</c>), así que la reescritura pasa por la
/// función SECURITY DEFINER de la migración <c>EnmascaraAuditoriaDeTrabajadorAnonimizado</c>.
/// La función toma el Tenant de <c>app.tenant_id</c>, no de un parámetro: con la conexión
/// de este mismo contexto, es el Tenant con el que se acaba de guardar la anonimización.
/// </remarks>
public class EnmascaradoAuditoriaTrabajadorAnonimizado(CaeManagerDbContext dbContext) : IEnmascaradoAuditoriaTrabajadorAnonimizado
{
    public Task<int> EnmascararAsync(Guid trabajadorId, CancellationToken cancellationToken = default) =>
        dbContext.Database
            .SqlQuery<int>($"SELECT app_enmascarar_auditoria_de_trabajador_anonimizado({trabajadorId}) AS \"Value\"")
            .SingleAsync(cancellationToken);
}
