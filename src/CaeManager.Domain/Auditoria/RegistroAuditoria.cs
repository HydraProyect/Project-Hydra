using CaeManager.Domain.Common;

namespace CaeManager.Domain.Auditoria;

/// <summary>
/// Registro de un cambio sobre una entidad de dominio. Lo escribe el
/// interceptor de EF Core en Infrastructure en cada SaveChanges — nunca se
/// crea manualmente desde Application (ver ARCHITECTURE.md, "Auditoría y
/// soft delete").
/// </summary>
public class RegistroAuditoria : EntidadConTenant
{
    public string EntidadTipo { get; private set; } = string.Empty;
    public Guid EntidadId { get; private set; }
    public string Accion { get; private set; } = string.Empty;
    public string? DatosAntes { get; private set; }
    public string? DatosDespues { get; private set; }
    public Guid? UsuarioId { get; private set; }
    public DateTime FechaUtc { get; private set; }

    private RegistroAuditoria()
    {
    }

    public RegistroAuditoria(
        string entidadTipo,
        Guid entidadId,
        string accion,
        string? datosAntes,
        string? datosDespues,
        Guid? usuarioId)
    {
        EntidadTipo = entidadTipo;
        EntidadId = entidadId;
        Accion = accion;
        DatosAntes = datosAntes;
        DatosDespues = datosDespues;
        UsuarioId = usuarioId;
        FechaUtc = DateTime.UtcNow;
    }
}
