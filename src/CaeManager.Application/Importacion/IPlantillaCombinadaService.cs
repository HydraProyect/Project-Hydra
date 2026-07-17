namespace CaeManager.Application.Importacion;

/// <summary>
/// Plantilla combinada de un solo libro (4 hojas: Clientes, Empresas,
/// Centros, Trabajadores) para dar de alta la cartera completa de una vez,
/// incluyendo CIF y asociaciones Empresa↔Cliente/Centro — lo que las
/// plantillas de Fase 5/7 dejaron de poder crear desde Fase 10.
/// </summary>
public interface IPlantillaCombinadaService
{
    byte[] GenerarPlantilla();

    Task<PlanImportacionCombinadaDto> AnalizarAsync(Stream archivo, CancellationToken cancellationToken = default);
}
