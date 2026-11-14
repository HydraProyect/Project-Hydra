using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Proyectos;
using CaeManager.Domain.Plantillas;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Plantillas;

/// <summary>Las coordenadas de un <see cref="DocumentoGenerado"/> que deciden su Asignación de Cartera, ya proyectadas.</summary>
public sealed record CoordenadasDocumentoGenerado(
    Guid Id, Guid DocumentoId, Guid PlantillaDocumentoVersionId, Guid? TrabajadorId, Guid? EmpresaId, Guid? CentroId);

/// <summary>
/// Asignación de Cartera de un <see cref="DocumentoGenerado"/>: la puerta de todas las lecturas del registro de
/// generación (la lista de trazabilidad y el contador de «con avisos»). Un registro generado cuelga de un Documento
/// real, así que verlo exige poder leer ese Documento (<see cref="DocumentoAlcanceExtensions.DocumentosLegiblesAsync"/>,
/// SEG-16, OWASP ASVS 4.0.3 V4.2.1), y además que cada coordenada que el registro guarda esté en el alcance de quien
/// mira: su Trabajador, su Empresa, su Centro y la plantilla de la que salió
/// (<see cref="AlcanceDePlantillas.PlantillaDentroDeAlcanceAsync"/>).
///
/// <para>
/// Lo que motiva el Documento: un documento generado desde una plantilla de ámbito Cliente sin Centro guarda
/// <c>TrabajadorId</c> y <c>EmpresaId</c> nulos, y el Cliente empresarial al que pertenece solo consta en el Documento.
/// Filtrar solo por las coordenadas no nulas del registro dejaba pasar esos registros a un Gestor CAE con Cartera
/// limitada (ASVS V4.2.1 y V4.1.3). La misma puerta decide la descarga del PDF (<c>ObtenerDocumentoPorIdQuery</c>).
/// </para>
///
/// <para>
/// Con acceso total (Administrador, Dirección CAE, Consulta) no se recorta nada, igual que antes: el aislamiento entre
/// Tenants propietarios lo da RLS, no esta puerta. Sin acceso total, un Documento dado de baja, inexistente o de otro
/// Tenant no es legible y su registro no se ve (falla cerrado). Una plantilla que ya no se lee no aporta coordenada:
/// la fila sale sin su nombre («—») y la deciden las demás.
/// </para>
/// </summary>
public static class AlcanceDeDocumentosGenerados
{
    /// <summary>Los Ids de los registros de <paramref name="generados"/> que quien mira alcanza.</summary>
    public static async Task<HashSet<Guid>> DocumentosGeneradosVisiblesAsync(
        this IAlcanceDatosService alcance,
        IReadOnlyCollection<CoordenadasDocumentoGenerado> generados,
        IPlantillasQueryContext plantillasContext,
        IDocumentosQueryContext documentosContext,
        IProyectosQueryContext proyectosContext,
        CancellationToken cancellationToken = default)
    {
        if (generados.Count == 0)
            return [];

        if (await alcance.TieneAccesoTotalAsync(cancellationToken))
            return generados.Select(g => g.Id).ToHashSet();

        var documentosLegibles = await alcance.DocumentosLegiblesAsync(
            generados.Select(g => g.DocumentoId).Distinct().ToList(), documentosContext, proyectosContext, cancellationToken);

        var idsVersiones = generados.Select(g => g.PlantillaDocumentoVersionId).Distinct().ToList();
        var versiones = await plantillasContext.PlantillasDocumentoVersion
            .Where(v => idsVersiones.Contains(v.Id))
            .Select(v => new { v.Id, v.PlantillaDocumentoId })
            .ToListAsync(cancellationToken);

        // La entidad entera, como en ObtenerPlantillasDocumentoQuery: PlantillaDentroDeAlcanceAsync decide sobre ella.
        var idsPlantillas = versiones.Select(v => v.PlantillaDocumentoId).Distinct().ToList();
        var plantillas = await plantillasContext.PlantillasDocumento
            .Where(p => idsPlantillas.Contains(p.Id))
            .ToListAsync(cancellationToken);

        var plantillaDentroPorId = new Dictionary<Guid, bool>();
        foreach (var plantilla in plantillas)
            plantillaDentroPorId[plantilla.Id] = await alcance.PlantillaDentroDeAlcanceAsync(plantilla, cancellationToken);

        var plantillaDentroPorVersion = versiones
            .Where(v => plantillaDentroPorId.ContainsKey(v.PlantillaDocumentoId))
            .ToDictionary(v => v.Id, v => plantillaDentroPorId[v.PlantillaDocumentoId]);

        var visibles = new HashSet<Guid>();
        foreach (var generado in generados)
        {
            if (!documentosLegibles.Contains(generado.DocumentoId))
                continue;
            if (generado.TrabajadorId is { } trabajadorId && !await alcance.TrabajadorVisibleAsync(trabajadorId, cancellationToken))
                continue;
            if (generado.EmpresaId is { } empresaId && !await alcance.EmpresaVisibleAsync(empresaId, cancellationToken))
                continue;
            if (generado.CentroId is { } centroId && !await alcance.CentroVisibleAsync(centroId, cancellationToken))
                continue;
            if (plantillaDentroPorVersion.TryGetValue(generado.PlantillaDocumentoVersionId, out var plantillaDentro) && !plantillaDentro)
                continue;

            visibles.Add(generado.Id);
        }

        return visibles;
    }
}
