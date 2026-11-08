using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Domain.Documentos;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Gestiones;

/// <summary>
/// Conciliación de las Gestiones automáticas sobre lo ya guardado: hace nacer la Gestión de los actos con archivo (alta,
/// renovación o generación desde plantilla) que se confirmaron sin ella. El caso que cubre es el relevo azul/verde del
/// despliegue que introduce la función: mientras la ranura anterior sigue atendiendo (durante la migración y el
/// drenaje), sus actos crean el Documento y sus acreditaciones, pero no la Gestión, y la lectura derivada no puede
/// mostrar una fila que no existe.
///
/// <para>
/// <b>Qué es un acto sin Gestión.</b> Un Documento operativo (no sustituido ni eliminado) con archivo, creado desde
/// <c>desdeUtc</c>, sin ninguna Gestión automática, y cuyas acreditaciones del mismo acto (las creadas como mucho
/// <see cref="ToleranciaDelActo"/> después o antes que el Documento) incluyen alguna ante un acceso de Plataforma. La
/// Gestión se hace nacer con <see cref="INacimientoGestionAutomaticaService.AgregarDeActosSinGestionAsync"/>, la misma
/// regla que en el acto: aquí no se decide qué es un paso. Una acreditación que llegó después (un acceso nuevo, una
/// asignación nueva) no es del acto, igual que no habría entrado en la Gestión nacida con él. <c>desdeUtc</c> acota el
/// histórico anterior a la función: lo fija quien invoca (<c>ConciliacionGestionesAutomaticasOptions.Ventana</c>).
/// </para>
///
/// <para>
/// <b>Sustitución.</b> Si el Documento sin Gestión renovó otro cuya Gestión sigue abierta, esa Gestión ya se lee como
/// Sustituida (<c>GestionAutomatica.ResolverEstado</c> con el Documento sustituido) y su detalle apunta a la que nace
/// aquí. La conciliación no la marca: no repite esa regla.
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
    /// <summary>Los Documentos operativos con archivo, creados desde <paramref name="desdeUtc"/>, que siguen sin Gestión.</summary>
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
    INacimientoGestionAutomaticaService nacimiento,
    IUnitOfWork unitOfWork)
    : IConciliacionGestionesAutomaticasService
{
    /// <summary>Documentos por guardado: acota el rastreo y las listas de las consultas. Cada lote se guarda entero o nada.</summary>
    public const int DocumentosPorLote = 100;

    /// <summary>
    /// Distancia máxima entre la creación del Documento y la de una acreditación para que sea del mismo acto. El acto
    /// construye las dos en la misma petición, con el archivo ya subido y a milisegundos de distancia; una acreditación
    /// posterior es de otro acto (una Asignación o un acceso de Plataforma nuevos sobre un Documento ya existente) y no
    /// entra en la Gestión, como en el flujo normal. Un minuto deja margen a una petición lenta sin abarcar el gesto
    /// siguiente de la persona.
    /// </summary>
    internal static readonly TimeSpan ToleranciaDelActo = TimeSpan.FromMinutes(1);

    public async Task<IReadOnlyList<Guid>> BuscarDocumentosSinGestionAsync(
        DateTime desdeUtc, CancellationToken cancellationToken = default)
    {
        if (desdeUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("El inicio de la ventana debe estar en UTC.", nameof(desdeUtc));

        var candidatos = await documentosContext.Documentos.Operativos()
            .Where(d => d.ArchivoUrl != null && d.CreadoEnUtc >= desdeUtc)
            .OrderBy(d => d.CreadoEnUtc).ThenBy(d => d.Id)
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

        var documentos = await documentosContext.Documentos.Operativos()
            .Where(d => ids.Contains(d.Id))
            .ToListAsync(cancellationToken);
        documentos.RemoveAll(d => conGestion.Contains(d.Id));
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

        var actos = documentos
            .Select(d => new ActoSinGestionAutomatica(
                d,
                nacidosPorRenovacion.Contains(d.Id),
                acreditaciones.Where(a => a.DocumentoId == d.Id && SonDelMismoActo(d, a)).ToList()))
            .ToList();

        var nacidas = await nacimiento.AgregarDeActosSinGestionAsync(actos, cancellationToken);
        if (nacidas.Count == 0)
            return 0;

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return nacidas.Count;
    }

    private static bool SonDelMismoActo(Documento documento, AcreditacionDocumentoPlataforma acreditacion) =>
        (acreditacion.CreadoEnUtc - documento.CreadoEnUtc).Duration() <= ToleranciaDelActo;
}
