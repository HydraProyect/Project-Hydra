namespace CaeManager.Infrastructure.FileStorage;

public class DiskFileStorageServiceOptions
{
    public const string SeccionConfiguracion = "AlmacenamientoArchivos";

    /// <summary>Ruta absoluta o relativa al content root. Fuera de wwwroot a propósito: los PDFs solo se sirven vía endpoint autenticado.</summary>
    public string Ruta { get; set; } = "App_Data/documentos";
}
