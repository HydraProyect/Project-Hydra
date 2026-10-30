namespace CaeManager.Application.Importacion;

/// <summary>
/// Si este entorno tiene configurada la plantilla «CAE completa»: al menos una hoja de Trabajadores con la
/// Empresa a la que pertenecen. Sin ella el lector no lee Trabajadores ni Documentos, así que la pantalla de
/// Importación no deja usar esa plantilla y <c>/salud</c> lo avisa.
/// </summary>
public interface IPlantillaCaeCompletaConfigurada
{
    bool EstaConfigurada { get; }
}
