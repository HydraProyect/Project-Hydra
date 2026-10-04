namespace CaeManager.Web.Components.DesignSystem;

/// <summary>
/// Tono semántico de un Badge. Exito/Advertencia/Peligro se reservan para el
/// semáforo de vigencia documental — nunca se usan como color decorativo en
/// otro contexto (ver Project-Hydra-Negocio/tecnico/docs/archive/design/DESIGN_SYSTEM.md).
/// <c>Tolerancia</c> es el cuarto escalón del semáforo: solo para «En tolerancia», entre el
/// ámbar de <c>Advertencia</c> y el rojo de <c>Peligro</c>.
/// </summary>
public enum TonoBadge
{
    Neutro,
    Exito,
    Advertencia,
    Peligro,
    Info,
    Tolerancia
}

/// <summary>
/// Tamaño de un Badge. <c>Pequeno</c> nace para la densidad de fila de las
/// listas (Centro 360, Project-Hydra-Negocio/tecnico/docs/ux-audit/PLAN-EJECUCION-UX.md § 0.9): en una fila con estado,
/// % de cumplimiento y a veces ventana de visita, el badge a tamaño normal
/// marca la altura de la fila entera. Solo cambia métrica (padding y tipo),
/// nunca color — el semáforo se lee igual.
/// </summary>
public enum TamanoBadge
{
    Medio,
    Pequeno
}
