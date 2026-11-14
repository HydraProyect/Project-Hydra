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
/// generación (la lista de trazabilidad, el contador de «con avisos» y el progreso de un lote). Un registro generado
/// cuelga de un Documento real, así que verlo exige poder leer ese Documento
/// (<see cref="DocumentoAlcanceExtensions.DocumentosLegiblesAsync"/>, SEG-16, OWASP ASVS 4.0.3 V4.2.1), y además que
/// cada coordenada que el registro guarda esté en el alcance de quien mira: su Trabajador, su Empresa, su Centro y la
/// plantilla de la que salió (<see cref="AlcanceDePlantillas"/>).
///
/// <para>
/// Lo que motiva el Documento: un documento generado desde una plantilla de ámbito Cliente sin Centro guarda
/// <c>TrabajadorId</c> y <c>EmpresaId</c> nulos, y el Cliente empresarial al que pertenece solo consta en el Documento.
/// Filtrar solo por las coordenadas no nulas del registro dejaba pasar esos registros a un Gestor CAE con Cartera
/// limitada (ASVS V4.2.1 y V4.1.3). La descarga del PDF (<c>ObtenerDocumentoPorIdQuery</c>) usa solo la mitad del
/// Documento: quien puede leer el Documento lo descarga aunque el Centro, la Empresa o la plantilla del registro estén
/// fuera de su Cartera. Lo que esta puerta oculta además son los metadatos de trazabilidad del registro generado.
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
    /// <summary>
    /// Acota en la base, antes de materializar, los registros que <see cref="DocumentosGeneradosVisiblesAsync"/> va a
    /// rechazar seguro: los de un Trabajador, una Empresa o un Centro fuera de la Cartera, y los de una plantilla fuera
    /// de alcance (OWASP API4:2023, consumo sin cota). No sustituye a la puerta: es un subconjunto de su criterio, con
    /// las mismas listas de alcance, y la puerta vuelve a decidir cada fila. Lo que no cabe en SQL es el Documento del
    /// que cuelga el registro, que vive en otro contexto de persistencia (<c>FronterasEntrePersistenciaDeFeaturesTests</c>)
    /// y se decide en memoria sobre lo que queda.
    /// </summary>
    public static async Task<IQueryable<DocumentoGenerado>> AcotarALaCarteraAsync(
        this IAlcanceDatosService alcance,
        IQueryable<DocumentoGenerado> consulta,
        IPlantillasQueryContext plantillasContext,
        CancellationToken cancellationToken = default)
    {
        if (await alcance.TieneAccesoTotalAsync(cancellationToken))
            return consulta;

        // Lista nula = sin restricción en esa coordenada; lista vacía = Cartera sin asignar (nada pasa salvo el nulo).
        if (await alcance.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken) is { } trabajadores)
            consulta = consulta.Where(d => d.TrabajadorId == null || trabajadores.Contains(d.TrabajadorId.Value));
        if (await alcance.ObtenerEmpresaIdsVisiblesAsync(cancellationToken) is { } empresas)
            consulta = consulta.Where(d => d.EmpresaId == null || empresas.Contains(d.EmpresaId.Value));
        if (await alcance.ObtenerCentroIdsVisiblesAsync(cancellationToken) is { } centros)
            consulta = consulta.Where(d => d.CentroId == null || centros.Contains(d.CentroId.Value));

        // La plantilla, con el único criterio de AlcanceDePlantillas en vez de repetirlo en SQL. Se leen solo las
        // plantillas atadas a un Centro o a un Cliente empresarial (las generales están siempre dentro) y solo sus tres
        // columnas: es el catálogo del Tenant propietario, no el registro de generación, y su tamaño no crece con él.
        var plantillasAtadas = await plantillasContext.PlantillasDocumento
            .CoordenadasDePlantillas()
            .Where(p => p.CentroId != null || p.ClienteEmpresarialId != null)
            .ToListAsync(cancellationToken);
        var plantillasFuera = new List<Guid>();
        foreach (var plantilla in plantillasAtadas)
        {
            if (!await alcance.PlantillaDentroDeAlcanceAsync(plantilla, cancellationToken))
                plantillasFuera.Add(plantilla.Id);
        }

        if (plantillasFuera.Count > 0)
        {
            var versionesFuera = await plantillasContext.PlantillasDocumentoVersion
                .Where(v => plantillasFuera.Contains(v.PlantillaDocumentoId))
                .Select(v => v.Id)
                .ToListAsync(cancellationToken);
            consulta = consulta.Where(d => !versionesFuera.Contains(d.PlantillaDocumentoVersionId));
        }

        return consulta;
    }

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

        // Solo las dos coordenadas que decide AlcanceDePlantillas, sin cargar la entidad.
        var idsPlantillas = versiones.Select(v => v.PlantillaDocumentoId).Distinct().ToList();
        var plantillas = await plantillasContext.PlantillasDocumento
            .Where(p => idsPlantillas.Contains(p.Id))
            .CoordenadasDePlantillas()
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

/// <summary>
/// Asignación de Cartera de un <see cref="LoteGeneracionDocumento"/> existente, por Id (OWASP ASVS 4.0.3 V4.2.1): el
/// mismo criterio con el que <c>IniciarLoteGeneracionDocumentosCommandHandler</c> deja nacer el lote, plantilla dentro
/// de alcance y todos sus Trabajadores visibles. Lo usan la lectura del progreso
/// (<c>ObtenerLoteGeneracionDocumentosQuery</c>) y el procesado de cada ítem
/// (<c>ProcesarItemLoteGeneracionCommand</c>), que tratan el «no» igual que un lote o un ítem que no existe.
/// </summary>
public static class AlcanceDeLotesGeneracion
{
    public static async Task<bool> LoteDentroDeAlcanceAsync(
        this IAlcanceDatosService alcance,
        Guid plantillaDocumentoVersionId,
        IReadOnlyCollection<Guid> trabajadorIds,
        IPlantillasQueryContext plantillasContext,
        CancellationToken cancellationToken = default)
    {
        if (await alcance.TieneAccesoTotalAsync(cancellationToken))
            return true;

        var plantillaId = await plantillasContext.PlantillasDocumentoVersion
            .Where(v => v.Id == plantillaDocumentoVersionId)
            .Select(v => (Guid?)v.PlantillaDocumentoId)
            .FirstOrDefaultAsync(cancellationToken);
        var plantilla = plantillaId is { } id
            ? await plantillasContext.PlantillasDocumento
                .Where(p => p.Id == id)
                .CoordenadasDePlantillas()
                .FirstOrDefaultAsync(cancellationToken)
            : null;
        // Una plantilla que ya no se lee no da alcance: falla cerrado.
        if (plantilla is null || !await alcance.PlantillaDentroDeAlcanceAsync(plantilla, cancellationToken))
            return false;

        var trabajadoresVisibles = await alcance.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken);
        if (trabajadoresVisibles is null)
            return true;
        var visibles = trabajadoresVisibles.ToHashSet();
        return trabajadorIds.All(visibles.Contains);
    }
}
