using CaeManager.Application.Common;
using CaeManager.Domain.Plantillas;

namespace CaeManager.Application.Plantillas;

/// <summary>
/// Asignación de Cartera de una <see cref="PlantillaDocumento"/> existente: el mismo criterio con el que
/// <c>CrearPlantillaDocumentoCommandHandler</c> decide si se puede crear una plantilla atada a un Centro o a un
/// Cliente empresarial. Una plantilla sin Centro ni Cliente empresarial es general del Tenant y queda dentro;
/// si lleva uno de los dos, o los dos, cada uno tiene que estar en el alcance de quien mira.
/// </summary>
public static class AlcanceDePlantillas
{
    /// <summary>
    /// ¿Alcanza quien mira la plantilla <paramref name="plantilla"/>? Lo usan las escrituras sobre una plantilla
    /// existente (agregar versión, guardar elementos, confirmar versión, iniciar un lote de generación) y sus lecturas
    /// (lista de plantillas, versión en el editor, y la de documentos generados a través de
    /// <see cref="AlcanceDeDocumentosGenerados"/>); todas tratan el «no» igual que una plantilla o versión que no
    /// existe, para no delatarla, y la lista simplemente la omite. GenerarDocumentoIndividual no lo
    /// llama: exige que el Centro y el Cliente empresarial de la petición sean visibles y coincidan con los de la
    /// plantilla, lo que equivale a este mismo criterio.
    /// </summary>
    public static Task<bool> PlantillaDentroDeAlcanceAsync(
        this IAlcanceDatosService alcance, PlantillaDocumento plantilla, CancellationToken cancellationToken = default) =>
        alcance.PlantillaDentroDeAlcanceAsync(plantilla.CentroId, plantilla.ClienteId, cancellationToken);

    /// <summary>
    /// La misma decisión sobre las dos coordenadas de una plantilla ya proyectadas, para quien no carga la entidad
    /// (<see cref="AlcanceDeDocumentosGenerados"/>): es la única definición del criterio, la de arriba delega aquí.
    /// </summary>
    public static async Task<bool> PlantillaDentroDeAlcanceAsync(
        this IAlcanceDatosService alcance, Guid? centroId, Guid? clienteEmpresarialId, CancellationToken cancellationToken = default)
    {
        if (centroId is { } centro && !await alcance.CentroVisibleAsync(centro, cancellationToken))
            return false;

        if (clienteEmpresarialId is { } clienteEmpresarial && !await alcance.ClienteVisibleAsync(clienteEmpresarial, cancellationToken))
            return false;

        return true;
    }
}
