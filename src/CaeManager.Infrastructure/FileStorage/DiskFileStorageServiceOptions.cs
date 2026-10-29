namespace CaeManager.Infrastructure.FileStorage;

public class DiskFileStorageServiceOptions
{
    public const string SeccionConfiguracion = "AlmacenamientoArchivos";

    /// <summary>Ruta absoluta o relativa al content root. Fuera de wwwroot a propósito: los PDFs solo se sirven vía endpoint autenticado.</summary>
    public string Ruta { get; set; } = "App_Data/documentos";

    /// <summary>
    /// Qué hacer con un archivo que no lleva la marca v2 y tampoco se descifra con el protector v1: en
    /// <c>true</c> no se sirve (falla cerrado, como un v2 que no supera la verificación); en <c>false</c>, el
    /// valor por defecto, se sigue sirviendo como legado en claro. En los dos casos se emite una alerta
    /// operativa crítica.
    ///
    /// El valor por defecto es <c>false</c> porque no está comprobado que en producción no queden archivos en
    /// claro: fallar cerrado sin esa comprobación podría dejar ilegible un Documento real. Pasarlo a
    /// <c>true</c> (<c>AlmacenamientoArchivos__RechazarLegadoEnClaro=true</c>) es el paso intermedio una vez
    /// confirmado, por los avisos de <see cref="DiskFileStorageService"/>, que esa población no existe; el
    /// paso final es retirar la rama legada en claro del código.
    /// </summary>
    public bool RechazarLegadoEnClaro { get; set; }
}
