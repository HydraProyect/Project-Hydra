namespace CaeManager.Domain.Trabajadores;

/// <summary>
/// Conjunto de <see cref="TipoContratacion"/>: a qué tipos de contratación se pide de serie un Tipo de documento de
/// Ámbito Trabajador (<see cref="Documentos.TipoDocumento.TiposContratacionDeSerie"/>). Cada bit es
/// <c>1 &lt;&lt; (int)TipoContratacion</c>, así que el ordinal persistido de <see cref="TipoContratacion"/> fija el
/// bit: ninguno de los dos se reordena. El valor persistido es el entero de la máscara.
/// </summary>
[Flags]
public enum TiposContratacion
{
    Ninguno = 0,
    CuentaAjena = 1 << (int)TipoContratacion.CuentaAjena,
    Autonomo = 1 << (int)TipoContratacion.Autonomo,
    Voluntario = 1 << (int)TipoContratacion.Voluntario,
    Todos = CuentaAjena | Autonomo | Voluntario,
}

public static class TiposContratacionExtensions
{
    /// <summary>El bit de un tipo de contratación concreto.</summary>
    public static TiposContratacion ComoConjunto(this TipoContratacion tipo) => (TiposContratacion)(1 << (int)tipo);

    /// <summary>¿El conjunto contiene este tipo de contratación?</summary>
    public static bool Incluye(this TiposContratacion conjunto, TipoContratacion tipo) =>
        (conjunto & tipo.ComoConjunto()) != TiposContratacion.Ninguno;
}
