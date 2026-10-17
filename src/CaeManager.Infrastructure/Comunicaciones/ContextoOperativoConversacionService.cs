using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones;
using CaeManager.Domain.Notificaciones;
using CaeManager.Domain.Operaciones;
using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Comunicaciones;

/// <summary>Resolución acotada al Tenant propietario activo. Las dimensiones diferidas nunca conceden cobertura.</summary>
public class ContextoOperativoConversacionService(CaeManagerDbContext db, ITenantActual tenantActual,
    IDirectorioUsuariosService directorio, INotificacionUsuarioRepository notificaciones, IUnitOfWork unitOfWork)
    : IContextoOperativoConversacionService
{
    public async Task<OperacionDeConversacion?> ResolverAsync(Guid propietarioTenantId, Guid? clienteEmpresarialId,
        Guid? empresaId, CancellationToken cancellationToken)
    {
        if (tenantActual.TenantId != propietarioTenantId) return null;
        var ahora = DateTime.UtcNow;
        var operaciones = await db.AsignacionesOperacion.Where(o => o.PropietarioTenantId == propietarioTenantId
            && o.Servicio == ServicioCae.Outbound && o.Estado == EstadoAsignacion.Vigente
            && o.VigenciaDesde <= ahora && (o.VigenciaHasta == null || ahora < o.VigenciaHasta))
            .ToListAsync(cancellationToken);
        var externas = operaciones.Where(o => !o.EsRaiz && (clienteEmpresarialId == null
            || o.AmbitoRelacionClienteId == null || o.AmbitoRelacionClienteId == clienteEmpresarialId)).ToList();
        // Sin una dimensión que identifique el hilo, una operación parcial no determina su Operador CAE.
        if (externas.Any(o => o.AmbitoCentroId != null || o.AmbitoTrabajadorId != null || o.AmbitoProyectoId != null
                             || (clienteEmpresarialId == null && o.AmbitoRelacionClienteId != null)))
            return null;
        var candidatas = externas.Where(o => o.AmbitoRelacionClienteId == null
                                              || o.AmbitoRelacionClienteId == clienteEmpresarialId).ToList();
        if (candidatas.Count == 0) candidatas = operaciones.Where(o => o.EsRaiz).ToList();
        return candidatas.Count == 1 ? new(candidatas[0].OperadorTenantId, candidatas[0].Id) : null;
    }

    public async Task AvisarCriticaAsync(OperacionDeConversacion operacion, Guid eventoId, CancellationToken cancellationToken)
    {
        var ahora = DateTime.UtcNow;
        var propietarioTenantId = tenantActual.TenantId;
        if (propietarioTenantId is null || !await db.AsignacionesOperacion.AnyAsync(o =>
                o.Id == operacion.AsignacionOperacionId && o.PropietarioTenantId == propietarioTenantId
                && o.OperadorTenantId == operacion.OperadorCaeTenantId && o.Servicio == ServicioCae.Outbound
                && o.Estado == EstadoAsignacion.Vigente && o.VigenciaDesde <= ahora
                && (o.VigenciaHasta == null || ahora < o.VigenciaHasta), cancellationToken))
            throw new InvalidOperationException("La Asignación de Operación ya no está vigente.");

        // Mismo patrón del aviso de apoyo de cartera: guardar primero la operación de negocio en su
        // Tenant propietario; después, avisos genéricos en el del Operador CAE. Nunca revelan el asunto ni la Empresa.
        using (AmbitoTenantExplicito.Establecer(operacion.OperadorCaeTenantId))
        {
            var equipo = new HashSet<Guid>();
            foreach (var rol in new[] { "GestorCae", "CoordinadorCae", "DireccionCae", "Administrador" })
                equipo.UnionWith(await directorio.ObtenerCuentasActivasConRolAsync(operacion.OperadorCaeTenantId, rol, cancellationToken));
            if (equipo.Count == 0) throw new InvalidOperationException("El Operador CAE no tiene destinatarios activos para el aviso.");
            var avisados = await db.NotificacionesUsuario.Where(n => n.EventoOrigenId == eventoId)
                .Select(n => n.UsuarioDestinatarioId).ToListAsync(cancellationToken);
            foreach (var personaId in equipo.Except(avisados))
                notificaciones.Agregar(new NotificacionUsuario(personaId, "Conversación Crítica",
                    "Hay una conversación Crítica que requiere atención del equipo del Operador CAE. Consulta tus conversaciones autorizadas.",
                    "/comunicaciones", "Abrir conversaciones", eventoId));
            try { await unitOfWork.SaveChangesAsync(cancellationToken); }
            catch
            {
                foreach (var entrada in db.ChangeTracker.Entries<NotificacionUsuario>()
                             .Where(e => e.State == EntityState.Added && e.Entity.EventoOrigenId == eventoId).ToList())
                    entrada.State = EntityState.Detached;
                throw;
            }
        }
    }
}
