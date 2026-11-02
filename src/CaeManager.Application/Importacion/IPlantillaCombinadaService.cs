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

    /// <param name="archivo">El libro a analizar.</param>
    /// <param name="ambito">
    /// Con autoridad acotada, lo existente que el acto puede ver: lo demás se trata como inexistente (mismos avisos, mismas
    /// asociaciones, mismas filas). Null (autoridad Total) analiza contra todo lo que existe en el Tenant.
    /// </param>
    Task<PlanImportacionCombinadaDto> AnalizarAsync(
        Stream archivo, ExistentesEnAmbito? ambito = null, CancellationToken cancellationToken = default);
}
