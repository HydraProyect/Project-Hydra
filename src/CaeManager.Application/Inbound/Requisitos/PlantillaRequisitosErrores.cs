using CaeManager.Domain.Common;

namespace CaeManager.Application.Inbound.Requisitos;

public static class PlantillaRequisitosErrores
{
    /// <summary>También cuando existe pero queda fuera de la cartera: no se revela si existe.</summary>
    public static readonly Error NoEncontrada =
        Error.Crear("PlantillaRequisitos.NoEncontrada", "No encontramos esta plantilla de requisitos.");

    public static readonly Error ReferenciaNoEncontrada =
        Error.Crear("PlantillaRequisitos.ReferenciaNoEncontrada", "No encontramos la Actividad, el riesgo, el Centro o el Cliente de la plantilla.");

    public static readonly Error YaExiste =
        Error.Crear("PlantillaRequisitos.YaExiste", "Ya hay una plantilla activa para esta referencia.");

    public static readonly Error TipoDocumentoNoEncontrado =
        Error.Crear("PlantillaRequisitos.TipoDocumentoNoEncontrado", "No encontramos este tipo de documento.");

    public static readonly Error RequisitoBaseNoEncontrado =
        Error.Crear("PlantillaRequisitos.RequisitoBaseNoEncontrado", "No encontramos este requisito del catálogo base.");

    public static readonly Error NombreDuplicado =
        Error.Crear("PlantillaRequisitos.NombreDuplicado", "Ya existe un tipo de documento con este nombre.");

    /// <summary>
    /// Toda escritura sobre una plantilla trae la versión que leyó: sin ella, <c>ConcurrenciaOptimista</c> no comprueba nada y
    /// dos Gestores CAE que editan a la vez se pisarían sin verlo.
    /// </summary>
    public const string MensajeVersionObligatoria = "Falta la versión de la plantilla que leíste: vuelve a abrirla.";

    public static Error Invalido(string mensaje) => Error.Crear("PlantillaRequisitos.Invalido", mensaje);
}
