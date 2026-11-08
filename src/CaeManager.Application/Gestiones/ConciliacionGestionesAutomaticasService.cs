using CaeManager.Application.Auditoria;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Domain.Documentos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Gestiones;

/// <summary>
/// Conciliación de las Gestiones automáticas sobre lo ya guardado: hace nacer la Gestión de los actos con archivo (alta,
/// renovación o generación desde plantilla) que se confirmaron sin ella. El caso que cubre es el relevo azul/verde del
/// despliegue que introduce la función: mientras la ranura anterior sigue atendiendo (durante la migración y el
/// drenaje), sus actos crean el Documento y sus acreditaciones, pero no la Gestión, y la lectura derivada no puede
/// mostrar una fila que no existe.
///
/// <para>
/// <b>Qué es un acto sin Gestión.</b> Un Documento con archivo, no eliminado, creado desde <c>desdeUtc</c> y sin ninguna
/// Gestión automática, operativo o ya sustituido: si la ranura anterior lo creó y lo renovó antes de la conciliación, su
/// Gestión histórica habría nacido igual en el acto. <c>desdeUtc</c> acota el histórico anterior a la función: lo fija
/// quien despliega (<c>ConciliacionGestionesAutomaticasOptions.DesdeUtc</c>), no una ventana móvil.
/// </para>
///
/// <para>
/// <b>Qué acreditaciones son del acto.</b> Las del Documento creadas, como mucho, en el instante de su alta auditada:
/// el <c>FechaUtc</c> más antiguo de los registros de auditoría «Creado» del Documento. Ese registro lo construye el
/// interceptor de auditoría dentro del <c>SaveChangesAsync</c> del acto, después de que el acto construyera sus
/// acreditaciones (todos los caminos de alta con archivo las agregan antes de su único guardado) y antes de confirmar.
/// Una acreditación de otro acto (un acceso de Plataforma, una Asignación o un requisito nuevos,
/// <c>IAltaAcreditacionesPlataformaService</c>) solo puede construirse después de leer el Documento ya confirmado, así
/// que es estrictamente posterior. No hay tolerancia que confunda actos cercanos ni deje fuera uno lento: es el mismo
/// conjunto que el acto toma de lo agregado sin guardar (<c>NacimientoGestionAutomaticaService.AgregarAsync</c>), con
/// el supuesto de un reloj común y que no retrocede entre las ranuras (las dos corren en el mismo servidor). Un
/// Documento sin alta auditada no se concilia: sin ese dato no hay forma de separar su acto de los posteriores, y se
/// registra como aviso.
/// </para>
///
/// <para>
/// La Gestión se hace nacer con <see cref="INacimientoGestionAutomaticaService.AgregarDeActosSinGestionAsync"/>, la misma
/// regla que en el acto: aquí no se decide qué es un paso ni cuándo una renovación la cierra.
/// </para>
///
/// <para>
/// <b>Sustitución.</b> Si el Documento sin Gestión ya se renovó, su Gestión nace ya Sustituida (con la regla de la
/// renovación, en <see cref="INacimientoGestionAutomaticaService"/>) y con enlace a la del sucesor: la búsqueda devuelve
/// primero los más recientes para que el sucesor, si también la necesita, la tenga antes. Si es el Documento sin Gestión
/// el que renovó a otro cuya Gestión sigue abierta, esa Gestión ya guardada no se toca: se lee como Sustituida
/// (<c>GestionAutomatica.ResolverEstado</c> con el Documento sustituido).
/// </para>
///
/// <para>
/// <b>Idempotente.</b> Cada lote vuelve a comprobar qué Documentos siguen sin Gestión, y el índice único
/// <c>(TenantId, DocumentoId)</c> de <c>GestionesAutomaticas</c> impide dos Gestiones del mismo Documento aunque dos
/// pasadas coincidieran: el lote que choca no se confirma, y la pasada siguiente lo ve ya conciliado.
/// </para>
///
/// <para>
/// <b>Un solo Tenant cada vez</b>, con los contextos del Tenant en curso y bajo la RLS normal, sin
/// <c>IgnoreQueryFilters</c> ni identidad que se salte la RLS. Quien la invoca abre el ámbito de cada Tenant
/// propietario y un ámbito de servicios nuevo por lote (<c>ConciliacionGestionesAutomaticasHostedService</c>), para que
/// un lote fallido no deje entidades rastreadas que arrastren al siguiente.
/// </para>
/// </summary>
public interface IConciliacionGestionesAutomaticasService
{
    /// <summary>
    /// Los Documentos con archivo, no eliminados (operativos o sustituidos), creados desde <paramref name="desdeUtc"/>,
    /// que siguen sin Gestión; los más recientes primero.
    /// </summary>
    Task<IReadOnlyList<Guid>> BuscarDocumentosSinGestionAsync(DateTime desdeUtc, CancellationToken cancellationToken = default);

    /// <summary>
    /// Hace nacer y guarda, en un solo <c>SaveChangesAsync</c>, la Gestión de cada Documento del lote que siga sin ella
    /// y tenga pasos. Como mucho <see cref="ConciliacionGestionesAutomaticasService.DocumentosPorLote"/> Documentos.
    /// </summary>
    /// <returns>Número de Gestiones creadas.</returns>
    Task<int> ConciliarLoteAsync(IReadOnlyCollection<Guid> documentoIds, CancellationToken cancellationToken = default);
}

public class ConciliacionGestionesAutomaticasService(
    IDocumentosQueryContext documentosContext,
    IGestionesQueryContext gestionesContext,
    IAuditoriaQueryContext auditoriaContext,
    INacimientoGestionAutomaticaService nacimiento,
    IUnitOfWork unitOfWork,
    ILogger<ConciliacionGestionesAutomaticasService> logger)
    : IConciliacionGestionesAutomaticasService
{
    /// <summary>Documentos por guardado: acota el rastreo y las listas de las consultas. Cada lote se guarda entero o nada.</summary>
    public const int DocumentosPorLote = 100;

    /// <summary>La acción con que el interceptor de auditoría registra un alta (<c>AuditoriaInterceptor</c>).</summary>
    internal const string AccionAlta = "Creado";

    public async Task<IReadOnlyList<Guid>> BuscarDocumentosSinGestionAsync(
        DateTime desdeUtc, CancellationToken cancellationToken = default)
    {
        if (desdeUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("El inicio de la ventana debe estar en UTC.", nameof(desdeUtc));

        // Sustituidos incluidos: su Gestión histórica también habría nacido en el acto. Eliminados no.
        var candidatos = await documentosContext.Documentos
            .Where(d => !d.EstaEliminado && d.ArchivoUrl != null && d.CreadoEnUtc >= desdeUtc)
            .OrderByDescending(d => d.CreadoEnUtc).ThenBy(d => d.Id)
            .Select(d => d.Id)
            .ToListAsync(cancellationToken);

        var conGestion = new HashSet<Guid>();
        foreach (var trozo in candidatos.Chunk(DocumentosPorLote))
            conGestion.UnionWith(await gestionesContext.GestionesAutomaticas
                .Where(g => trozo.Contains(g.DocumentoId))
                .Select(g => g.DocumentoId)
                .ToListAsync(cancellationToken));

        return candidatos.Where(id => !conGestion.Contains(id)).ToList();
    }

    public async Task<int> ConciliarLoteAsync(
        IReadOnlyCollection<Guid> documentoIds, CancellationToken cancellationToken = default)
    {
        if (documentoIds.Count > DocumentosPorLote)
            throw new ArgumentException($"Como mucho {DocumentosPorLote} Documentos por lote.", nameof(documentoIds));

        var ids = documentoIds.Distinct().ToArray();
        if (ids.Length == 0)
            return 0;

        // Se vuelve a mirar aquí, no solo al buscar: otra pasada pudo conciliarlos entre medias.
        var conGestion = (await gestionesContext.GestionesAutomaticas
            .Where(g => ids.Contains(g.DocumentoId))
            .Select(g => g.DocumentoId)
            .ToListAsync(cancellationToken)).ToHashSet();

        var documentos = await documentosContext.Documentos
            .Where(d => ids.Contains(d.Id) && !d.EstaEliminado)
            .ToListAsync(cancellationToken);
        documentos.RemoveAll(d => conGestion.Contains(d.Id));
        if (documentos.Count == 0)
            return 0;

        var candidatos = documentos.Select(d => d.Id).ToArray();
        var altaAuditada = (await auditoriaContext.RegistrosAuditoria
                .Where(r => r.EntidadTipo == nameof(Documento) && r.Accion == AccionAlta && candidatos.Contains(r.EntidadId))
                .GroupBy(r => r.EntidadId)
                .Select(g => new { DocumentoId = g.Key, AltaUtc = g.Min(r => r.FechaUtc) })
                .ToListAsync(cancellationToken))
            .ToDictionary(a => a.DocumentoId, a => a.AltaUtc);

        var sinAlta = documentos.Where(d => !altaAuditada.ContainsKey(d.Id)).Select(d => d.Id).ToList();
        if (sinAlta.Count > 0)
            logger.LogWarning(
                "Conciliación de Gestiones automáticas: {Documentos} Documentos sin alta auditada no se concilian, porque no se puede separar su acto de los posteriores: {DocumentoIds}.",
                sinAlta.Count, sinAlta);
        documentos.RemoveAll(d => !altaAuditada.ContainsKey(d.Id));
        if (documentos.Count == 0)
            return 0;

        var pendientes = documentos.Select(d => d.Id).ToArray();
        var acreditaciones = await documentosContext.AcreditacionesDocumentoPlataforma
            .Where(a => pendientes.Contains(a.DocumentoId))
            .ToListAsync(cancellationToken);

        // Si nació renovando otro: el anterior es historial y solo dice el origen, no decide estado ni cifras.
        var comoSustituto = pendientes.Select(id => (Guid?)id).ToArray();
        var nacidosPorRenovacion = (await documentosContext.Documentos
            .Where(d => comoSustituto.Contains(d.SustituidoPorDocumentoId))
            .Select(d => d.SustituidoPorDocumentoId)
            .ToListAsync(cancellationToken)).OfType<Guid>().ToHashSet();

        // Si ya se renovó: la Gestión ya guardada de su sucesor, para el enlace de la sustitución.
        var sucesores = documentos.Select(d => d.SustituidoPorDocumentoId).OfType<Guid>().Distinct().ToArray();
        var gestionDelSucesor = sucesores.Length == 0
            ? []
            : (await gestionesContext.GestionesAutomaticas
                    .Where(g => sucesores.Contains(g.DocumentoId))
                    .Select(g => new { g.DocumentoId, g.Id })
                    .ToListAsync(cancellationToken))
                .ToDictionary(g => g.DocumentoId, g => g.Id);

        var actos = documentos
            .Select(d => new ActoSinGestionAutomatica(
                d,
                nacidosPorRenovacion.Contains(d.Id),
                acreditaciones.Where(a => a.DocumentoId == d.Id && a.CreadoEnUtc <= altaAuditada[d.Id]).ToList(),
                d.SustituidoPorDocumentoId is { } sucesorId && gestionDelSucesor.TryGetValue(sucesorId, out var delSucesor)
                    ? delSucesor
                    : null))
            .ToList();

        var nacidas = await nacimiento.AgregarDeActosSinGestionAsync(actos, cancellationToken);
        if (nacidas.Count == 0)
            return 0;

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return nacidas.Count;
    }
}
