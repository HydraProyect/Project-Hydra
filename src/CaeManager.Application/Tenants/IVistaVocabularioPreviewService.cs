using CaeManager.Domain.Tenants;

namespace CaeManager.Application.Tenants;

/// <summary>
/// Vista previa del perfil de vocabulario (DDL-072), solo para el
/// Administrador del tenant activo: le permite alternar entre "Empresa
/// final" (Cliente Directo) y "Empresa delegada" (Consultora) para
/// confirmar que la interfaz lee bien en los dos escenarios, sin cambiar el
/// <see cref="Tenant.PerfilVocabulario"/> real de nadie. Capa de
/// presentación pura, igual que el propio DDL-072: no gobierna acceso a
/// datos, solo qué etiqueta y qué comportamiento derivado (DDL-076) usa la
/// pantalla mientras dura la vista previa.
/// </summary>
public interface IVistaVocabularioPreviewService
{
    /// <summary>Perfil forzado por la vista previa, o null si no hay ninguna activa (lee el perfil real del tenant).</summary>
    PerfilVocabularioTenant? PerfilForzado { get; }
}
