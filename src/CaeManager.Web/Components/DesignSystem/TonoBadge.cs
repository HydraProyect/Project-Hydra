namespace CaeManager.Web.Components.DesignSystem;

/// <summary>
/// Tono semántico de un Badge. Exito/Advertencia/Peligro se reservan para el
/// semáforo de vigencia documental — nunca se usan como color decorativo en
/// otro contexto (ver DESIGN_SYSTEM.md).
/// </summary>
public enum TonoBadge
{
    Neutro,
    Exito,
    Advertencia,
    Peligro,
    Info
}
