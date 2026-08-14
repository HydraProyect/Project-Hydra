namespace CaeManager.Domain.Tenants;

/// <summary>
/// Perfil de vocabulario del tenant (DDL-072, tecnico/DESIGN_DECISION_LOG.md):
/// capa de presentación pura, nunca rama de dominio — decide qué etiqueta y
/// qué forma (registro único / lista) usa la interfaz para <c>Empresa</c>,
/// nunca cambia el esquema ni la lógica. Se declara explícitamente en cada
/// alta de Tenant, nunca se infiere del número de Empresas que tenga.
/// </summary>
public enum PerfilVocabularioTenant
{
    /// <summary>
    /// El tenant ES la Empresa contratista (Escenario 2 de
    /// docs/MULTITENANCY.md § 2 — el usuario de MVP-1). "Empresa" se muestra
    /// como "Mi empresa", registro único.
    /// </summary>
    ClienteDirecto,

    /// <summary>
    /// El tenant gestiona la CAE de varias Empresas contratistas
    /// (Escenario 1). "Empresa" se muestra como "Empresas", lista — eje de
    /// trabajo del gestor.
    /// </summary>
    Consultora,
}
