namespace CaeManager.Application.Importacion;

/// <summary>
/// Textos y hojas de los omitidos de las plantillas de Clientes y de Documentos que <see cref="RecorteDeAnalisisAlAmbito"/>
/// reconoce (plantillas de Clientes y Documentos; la combinada analiza con lo existente en el ámbito) para que la vista previa de una carga masiva acotada no distinga «existe fuera de tu ámbito» de «no existe».
/// Los analizadores de Infrastructure y el recorte los comparten para que un retoque del texto no reabra el oráculo.
/// </summary>
public static class MotivosDeOmision
{
    public const string HojaDocumentos = "Documentos";
    public const string HojaClientes = "Clientes";

    public const string TrabajadorNoExiste =
        "No existe ningún trabajador con este DNI. Da de alta al trabajador antes de importar sus documentos.";
    public const string TipoDocumentoNoExiste = "No existe este tipo de documento en el catálogo del sistema.";
    public const string PrefijoFechaEmisionFutura = "La fecha de emisión (";
    public const string FilaDuplicadaEnElArchivo = "Fila duplicada dentro del propio archivo.";

    public const string ClienteNoExiste =
        "Este Cliente no existe todavía. Ahora requiere una identificación fiscal (CIF, DNI o NIE), que esta plantilla no recoge — créalo manualmente en Clientes.";
    public const string PrefijoCentroNoExiste = "Este centro no existe todavía";
    public const string CentroNoExiste =
        PrefijoCentroNoExiste + ". Ahora requiere una Empresa asociada, que esta plantilla no recoge — créalo manualmente en Centros.";
}
