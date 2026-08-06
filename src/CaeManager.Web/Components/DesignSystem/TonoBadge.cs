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

/// <summary>
/// Tamaño de un Badge. <c>Pequeno</c> nace para la densidad de fila de las
/// listas (Centro 360, PLAN-EJECUCION-UX.md § 0.9): en una fila con estado,
/// % de cumplimiento y a veces ventana de visita, el badge a tamaño normal
/// marca la altura de la fila entera. Solo cambia métrica (padding y tipo),
/// nunca color — el semáforo se lee igual.
/// </summary>
public enum TamanoBadge
{
    Medio,
    Pequeno
}
