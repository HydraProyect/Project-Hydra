using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Proyectos;
using CaeManager.Domain.Plantillas;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Plantillas;

/// <summary>Las coordenadas de un <see cref="DocumentoGenerado"/> que deciden su Asignación de Cartera, ya proyectadas.</summary>
public sealed record CoordenadasDocumentoGenerado(
    Guid Id, Guid DocumentoId, Guid PlantillaDocumentoVersionId, Guid? TrabajadorId, Guid? EmpresaId, Guid? CentroId);

/// <summary>Un <see cref="DocumentoGenerado"/> que pasó la puerta, con lo que la lista de trazabilidad enseña de él.</summary>
public sealed record DocumentoGeneradoVisible(
    Guid Id, Guid DocumentoId, Guid PlantillaDocumentoVersionId, Guid? TrabajadorId, Guid? EmpresaId, Guid? CentroId,
    DateTime GeneradoEnUtc, EstadoDocumentoGenerado Estado);

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

    /// <summary>
    /// El universo visible del registro de generación: los registros de <paramref name="universo"/> que pasan la puerta,
    /// del más reciente al más antiguo, con lo que la lista de trazabilidad enseña de cada uno. Es la única definición
    /// de «qué generados ve quien mira»: la lista (<c>ObtenerDocumentosGeneradosQuery</c>) la pinta y el contador de
    /// «con avisos» (<c>ObtenerTotalDocumentosGeneradosConAvisosQuery</c>) la cuenta, en vez de repetir el recorte y la
    /// puerta en una consulta propia. Así el contador no puede contar lo que la lista no enseña, y su coste queda acotado
    /// por el de la lista: hace el mismo recorte en SQL, materializa las mismas columnas y pasa la misma puerta, sobre un
    /// subconjunto del mismo universo. Sin paginación de servidor, igual que en main: con un registro grande este es el
    /// coste que crece (hueco declarado en la PR #2222).
    /// </summary>
    public static async Task<List<DocumentoGeneradoVisible>> GeneradosVisiblesAsync(
        this IAlcanceDatosService alcance,
        IQueryable<DocumentoGenerado> universo,
        IPlantillasQueryContext plantillasContext,
        IDocumentosQueryContext documentosContext,
        IProyectosQueryContext proyectosContext,
        CancellationToken cancellationToken = default)
    {
        // Recorte en la base de lo que la puerta va a rechazar seguro, para no materializar el registro entero del Tenant
        // propietario (API4:2023). La puerta de abajo sigue decidiendo cada fila.
        var consulta = await alcance.AcotarALaCarteraAsync(universo, plantillasContext, cancellationToken);

        // Proyecta a propósito: Soporte TALVEG no tiene CodigoSeguroVerificacion ni DatosUtilizadosJson, y materializar
        // la entidad daría 42501 en Sesión Privilegiada.
        var generados = await consulta
            .OrderByDescending(d => d.GeneradoEnUtc)
            .Select(d => new DocumentoGeneradoVisible(
                d.Id, d.DocumentoId, d.PlantillaDocumentoVersionId, d.TrabajadorId, d.EmpresaId, d.CentroId, d.GeneradoEnUtc, d.Estado))
            .ToListAsync(cancellationToken);

        // La Asignación de Cartera se decide con todas las coordenadas del registro y con el Documento del que cuelga,
        // no solo con las no nulas: un generado de ámbito Cliente sin Centro no guarda Trabajador ni Empresa (V4.2.1).
        var visibles = await alcance.DocumentosGeneradosVisiblesAsync(
            generados.Select(d => new CoordenadasDocumentoGenerado(
                d.Id, d.DocumentoId, d.PlantillaDocumentoVersionId, d.TrabajadorId, d.EmpresaId, d.CentroId)).ToList(),
            plantillasContext, documentosContext, proyectosContext, cancellationToken);
        return generados.Where(d => visibles.Contains(d.Id)).ToList();
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

        // Cada lista de alcance se lee una vez y como conjunto (CarteraEnMemoria): la puerta cuesta O(N) comprobaciones
        // para N registros, sin recorrer la Cartera por cada uno (Codex, ronda 4 de la PR #2222). Las consultas de arriba
        // son un número fijo por llamada, ninguna por registro.
        var cartera = new CarteraEnMemoria(alcance);
        var visibles = new HashSet<Guid>();
        foreach (var generado in generados)
        {
            if (!documentosLegibles.Contains(generado.DocumentoId))
                continue;
            if (generado.TrabajadorId is { } trabajadorId && !await cartera.TrabajadorVisibleAsync(trabajadorId, cancellationToken))
                continue;
            if (generado.EmpresaId is { } empresaId && !await cartera.EmpresaVisibleAsync(empresaId, cancellationToken))
                continue;
            if (generado.CentroId is { } centroId && !await cartera.CentroVisibleAsync(centroId, cancellationToken))
                continue;
            if (plantillaDentroPorVersion.TryGetValue(generado.PlantillaDocumentoVersionId, out var plantillaDentro) && !plantillaDentro)
                continue;

            visibles.Add(generado.Id);
        }

        return visibles;
    }
}

/// <summary>
/// Asignación de Cartera de un <see cref="LoteGeneracionDocumento"/> (OWASP ASVS 4.0.3 V4.2.1).
/// <see cref="LoteDentroDeAlcanceAsync"/> reproduce, sobre un lote que ya existe, el criterio con el que
/// <c>IniciarLoteGeneracionDocumentosCommandHandler</c> deja nacer el lote: plantilla dentro de alcance y todos sus
/// Trabajadores visibles. Iniciar no la llama (decide antes de que el lote exista, en línea, y además exige el Centro),
/// así que nada aquí sujeta que los dos criterios sigan iguales: si cambia uno, hay que cambiar el otro.
/// <see cref="LoteVisibleEnteroAsync"/> es el de un lote que ya existe, el único que usan la lectura del progreso
/// (<c>ObtenerLoteGeneracionDocumentosQuery</c>) y el procesado de cada ítem (<c>ProcesarItemLoteGeneracionCommand</c>),
/// que tratan el «no» igual que un lote o un ítem que no existe.
/// </summary>
public static class AlcanceDeLotesGeneracion
{
    /// <summary>
    /// ¿Ve quien mira el lote <paramref name="loteId"/> entero? Sin acceso total, el lote es todo o nada: pasa la puerta
    /// de <see cref="LoteDentroDeAlcanceAsync"/> y, además, cada documento generado por uno de sus ítems pasa la puerta
    /// de la lista de trazabilidad (<see cref="AlcanceDeDocumentosGenerados.DocumentosGeneradosVisiblesAsync"/>: Centro,
    /// Empresa, plantilla y Documento legible). Basta un ítem cuyo generado no se vea, o que ya no exista, para que el
    /// lote entero no se vea.
    ///
    /// <para>
    /// Por qué todo o nada y no ocultar el ítem: el estado y los recuentos del lote son del lote entero. Un lote de un
    /// solo ítem completado y oculto devolvía cero ítems con estado Completado, que delata que hubo una generación
    /// (hallazgo de Codex, ronda 2 de la PR #2222); y recontar solo lo visible mezclaba un estado global con recuentos
    /// filtrados. Así quien lo ve lo ve con su estado y sus recuentos reales, y quien no, recibe lo mismo que por un lote
    /// que no existe. Con acceso total no cambia nada.
    /// </para>
    ///
    /// <para>
    /// <paramref name="instantanea"/> son los ítems que el llamador ya leyó y va a usar (devolver o procesar), leídos
    /// ANTES de llamar a esta puerta. La puerta vuelve a leer los ítems del lote, en su estado actual, y decide sobre la
    /// unión de los dos. Por qué basta (hallazgo TOCTOU de Codex, ronda 3 de la PR #2222): un ítem solo avanza
    /// (Pendiente a Completado, CompletadoConAvisos o Fallido) y un <c>DocumentoGeneradoId</c> asignado no se retira
    /// ni cambia, así que todo generado de la instantánea sigue en los ítems actuales cuando la puerta los mira y queda
    /// comprobado; si se dio de baja entretanto, ya no se lee y la puerta falla. Lo que se completó después de la
    /// instantánea también se comprueba, aunque el llamador no lo devuelva. Al revés (la puerta primero y la instantánea
    /// después) un ítem completado entre las dos lecturas llevaría a la respuesta un generado que nadie miró. Además, por
    /// defensa, cada generado de la instantánea tiene que estar en el conjunto comprobado como visible: si no, niega.
    /// </para>
    ///
    /// <para>
    /// Coste (hallazgo de Codex, ronda 4 de la PR #2222): O(N) por llamada para un lote de N ítems. Las consultas son un
    /// número fijo, sin ninguna por ítem: los ítems, la versión y la plantilla, los generados del lote en una sola consulta
    /// acotada a sus Ids, sus Documentos, sus versiones y sus plantillas. Las comprobaciones en memoria van contra
    /// conjuntos (<see cref="CarteraEnMemoria"/>, <see cref="HashSet{T}"/>), no contra listas. La UI llama a Procesar y al
    /// progreso una vez por ítem, así que un lote entero cuesta O(N²), lo mismo que ya costaba la carga de ítems de ese
    /// bucle. El lote no tiene tope de Trabajadores: es una decisión de producto, declarada en la PR.
    /// </para>
    /// </summary>
    public static async Task<bool> LoteVisibleEnteroAsync(
        this IAlcanceDatosService alcance,
        Guid loteId,
        Guid plantillaDocumentoVersionId,
        IReadOnlyCollection<ItemDeLoteLeido> instantanea,
        IPlantillasQueryContext plantillasContext,
        IDocumentosQueryContext documentosContext,
        IProyectosQueryContext proyectosContext,
        CancellationToken cancellationToken = default)
    {
        if (await alcance.TieneAccesoTotalAsync(cancellationToken))
            return true;

        var actuales = await plantillasContext.ItemsGeneracionDocumento
            .Where(i => i.LoteGeneracionDocumentoId == loteId)
            .Select(i => new ItemDeLoteLeido(i.TrabajadorId, i.DocumentoGeneradoId))
            .ToListAsync(cancellationToken);
        var items = actuales.Concat(instantanea).ToList();

        if (!await alcance.LoteDentroDeAlcanceAsync(
                plantillaDocumentoVersionId, items.Select(i => i.TrabajadorId).Distinct().ToList(), plantillasContext, cancellationToken))
            return false;

        var idsGenerados = items
            .Where(i => i.DocumentoGeneradoId is not null)
            .Select(i => i.DocumentoGeneradoId!.Value)
            .Distinct()
            .ToList();
        if (idsGenerados.Count == 0)
            return true;

        // Proyecta a propósito, como la lista: sin CodigoSeguroVerificacion ni DatosUtilizadosJson.
        var generados = await plantillasContext.DocumentosGenerados
            .Where(d => idsGenerados.Contains(d.Id))
            .Select(d => new CoordenadasDocumentoGenerado(
                d.Id, d.DocumentoId, d.PlantillaDocumentoVersionId, d.TrabajadorId, d.EmpresaId, d.CentroId))
            .ToListAsync(cancellationToken);
        var visibles = await alcance.DocumentosGeneradosVisiblesAsync(
            generados, plantillasContext, documentosContext, proyectosContext, cancellationToken);

        // Un generado que ya no se lee (inexistente u oculto) no está en «visibles»: falla cerrado. idsGenerados
        // incluye los de la instantánea, así que ninguno sale sin haber pasado por aquí.
        return idsGenerados.All(visibles.Contains);
    }

    // Privada a propósito: un lote que ya existe se decide solo con LoteVisibleEnteroAsync, para que la lectura del
    // progreso y el procesado de un ítem no puedan volver a divergir entre sí.
    private static async Task<bool> LoteDentroDeAlcanceAsync(
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

/// <summary>
/// Lo que decide la Asignación de Cartera de un <see cref="ItemGeneracionDocumento"/>: su Trabajador y, si ya generó,
/// su documento. Es la instantánea que el llamador pasa a <see cref="AlcanceDeLotesGeneracion.LoteVisibleEnteroAsync"/>.
/// </summary>
public sealed record ItemDeLoteLeido(Guid TrabajadorId, Guid? DocumentoGeneradoId);
