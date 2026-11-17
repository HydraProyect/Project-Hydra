namespace CaeManager.Domain.Trabajadores;

/// <summary>
/// Conjunto de <see cref="TipoContratacion"/>: a qué tipos de contratación se pide de serie un Tipo de documento de
/// Ámbito Trabajador (<see cref="Documentos.TipoDocumento.TiposContratacionDeSerie"/>). Cada bit es
/// <c>1 &lt;&lt; (int)TipoContratacion</c>, así que el ordinal persistido de <see cref="TipoContratacion"/> fija el
/// bit: ninguno de los dos se reordena. El valor persistido es el entero de la máscara.
/// <para>
/// <see cref="Extranjero"/> (8) llegó el 2026-11-17 y <see cref="Todos"/> pasó de 7 a 15. La migración
/// <c>TraduceExtranjeroAModeloDeContratacion</c> subió a 15 las máscaras guardadas que eran exactamente el 7 anterior y
/// tradujo el «Se pide a» de los requisitos de plantilla de Trabajador a este bit: la máscara es el único criterio para pedir
/// algo a un Trabajador extranjero. En un requisito de Empresa contratista no hay máscara y sigue decidiendo «Se pide a».
/// </para>
/// </summary>
[Flags]
public enum TiposContratacion
{
    Ninguno = 0,
    CuentaAjena = 1 << (int)TipoContratacion.CuentaAjena,
    Autonomo = 1 << (int)TipoContratacion.Autonomo,
    Voluntario = 1 << (int)TipoContratacion.Voluntario,
    Extranjero = 1 << (int)TipoContratacion.Extranjero,
    Todos = CuentaAjena | Autonomo | Voluntario | Extranjero,
}

public static class TiposContratacionExtensions
{
    /// <summary>El bit de un tipo de contratación concreto.</summary>
    public static TiposContratacion ComoConjunto(this TipoContratacion tipo) => (TiposContratacion)(1 << (int)tipo);

    /// <summary>¿El conjunto contiene este tipo de contratación?</summary>
    public static bool Incluye(this TiposContratacion conjunto, TipoContratacion tipo) =>
        (conjunto & tipo.ComoConjunto()) != TiposContratacion.Ninguno;
}
