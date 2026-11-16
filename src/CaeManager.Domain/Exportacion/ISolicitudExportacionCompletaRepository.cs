using CaeManager.Domain.Auditoria;

namespace CaeManager.Domain.Exportacion;

/// <summary>Solicitudes de exportación completa del Tenant en curso (RLS y filtro de Tenant).</summary>
public interface ISolicitudExportacionCompletaRepository
{
    /// <summary>
    /// Si el Tenant en curso tiene alguna solicitud registrada estrictamente después de <paramref name="desdeUtc"/>,
    /// en cualquier estado, también fallida (OWASP API4:2023: un fallo repetible no abre la puerta a reintentos sin
    /// límite). Con <c>ahora - 24 h</c>, la pedida hace justo 24 h ya no bloquea. Solo es fiable dentro del candado
    /// por Tenant de la solicitud.
    /// </summary>
    Task<bool> ExisteRegistradaDesdeAsync(DateTime desdeUtc, CancellationToken cancellationToken = default);

    Task<SolicitudExportacionCompleta?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    void Agregar(SolicitudExportacionCompleta solicitud);

    /// <summary>Añade la fila de auditoría al mismo guardado que el cambio que audita.</summary>
    void AgregarAuditoria(RegistroAuditoria registro);
}
