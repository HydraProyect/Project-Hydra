namespace CaeManager.Domain.Subcontratas;

/// <summary>
/// Estado derivado de la supervisión de un tipo documental exigido a una
/// Subcontrata en un Centro (ADR-005 § 2.3). Se calcula siempre
/// (<see cref="CalculadoraEstadoSupervision"/>), nunca se almacena — mismo
/// principio que <c>EstadoDocumento</c>.
/// </summary>
public enum EstadoSupervision
{
    /// <summary>Ninguna verificación registrada todavía para el tipo exigido.</summary>
    SinVerificar = 0,

    /// <summary>Última verificación válida y dentro de plazo (o sin caducidad declarada).</summary>
    Vigente = 1,

    /// <summary>Válida pero la caducidad declarada entra en el umbral ámbar.</summary>
    Proximo = 2,

    /// <summary>Válida pero la caducidad declarada entra en el umbral rojo.</summary>
    Urgente = 3,

    /// <summary>La caducidad declarada por la plataforma ya pasó.</summary>
    Vencido = 4,

    /// <summary>La última verificación encontró el documento rechazado/caducado o directamente ausente.</summary>
    NoValido = 5
}
