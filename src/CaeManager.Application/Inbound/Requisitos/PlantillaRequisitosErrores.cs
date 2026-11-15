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

    public static Error Invalido(string mensaje) => Error.Crear("PlantillaRequisitos.Invalido", mensaje);
}
