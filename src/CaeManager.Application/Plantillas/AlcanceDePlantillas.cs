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
    /// (lista de plantillas, versión en el editor); todas tratan el «no» igual que una plantilla o versión que no
    /// existe, para no delatarla, y la lista simplemente la omite. GenerarDocumentoIndividual no lo
    /// llama: exige que el Centro y el Cliente empresarial de la petición sean visibles y coincidan con los de la
    /// plantilla, lo que equivale a este mismo criterio.
    /// </summary>
    public static async Task<bool> PlantillaDentroDeAlcanceAsync(
        this IAlcanceDatosService alcance, PlantillaDocumento plantilla, CancellationToken cancellationToken = default)
    {
        if (plantilla.CentroId is { } centroId && !await alcance.CentroVisibleAsync(centroId, cancellationToken))
            return false;

        if (plantilla.ClienteId is { } clienteEmpresarial && !await alcance.ClienteVisibleAsync(clienteEmpresarial, cancellationToken))
            return false;

        return true;
    }
}
