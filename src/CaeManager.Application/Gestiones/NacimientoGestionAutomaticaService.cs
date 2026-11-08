using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Gestiones;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Gestiones;

/// <summary>
/// Hace nacer, dentro del mismo acto, la <see cref="GestionAutomatica"/> de un Documento que acaba de nacer con
/// archivo (§ 4.1 de la especificación «Gestión de varios pasos»): alta con archivo o renovación con archivo nuevo.
/// Una sola Gestión con un paso por cada acreditación en plataforma que ese mismo acto hizo nacer, y solo de accesos
/// de tipo Plataforma (los de correo nunca dan pasos, D-14).
///
/// <para>
/// <b>No guarda</b>: agrega al contexto para que el Command confirme el Documento, sus acreditaciones, la Gestión
/// y la sustitución de la anterior en el mismo <c>SaveChangesAsync</c>, o nada. Así no hay ventana entre el alta
/// confirmada y la Gestión (un fallo deshace las dos), los pasos son exactamente las acreditaciones de ese acto (las
/// agregadas sin guardar del Documento, no lo que haya en la base cuando se procese un evento) y la sustitución de
/// la Gestión anterior va en la transacción de la renovación, protegida por la versión del Documento anterior.
/// La marca de sustitución es la mejor información del acto (instante y enlace), no la garantía: si una acreditación
/// cambia a la vez, o si la renovación la hace la ranura anterior de un relevo, la lectura sigue tratando como
/// Sustituida la Gestión abierta de un Documento ya sustituido (<c>GestionAutomatica.ResolverEstado</c>).
/// </para>
///
/// <para>
/// Qué accesos dan pasos lo decide la regla única de alta de acreditaciones (<c>IAltaAcreditacionesPlataformaService</c>),
/// que el Command llama antes que a este servicio: la Gestión solo agrupa lo que ese acto creó, no recalcula el universo.
/// </para>
///
/// <para>
/// Sin archivo no nace (el disparador fijado es «alta y renovación con archivo»), y sin pasos tampoco: la Gestión
/// automática existe para actualizar plataformas, y sin accesos de plataforma no hay nada que gestionar (regla 2).
/// </para>
/// </summary>
public interface INacimientoGestionAutomaticaService
{
    /// <summary>
    /// Agrega, sin guardar, la Gestión de <paramref name="nacido"/> y, si es una renovación, cierra como Sustituida la
    /// de <paramref name="sustituido"/> si seguía abierta (§ 4.1 regla 4). Llamar después de dar de alta sus
    /// acreditaciones y, en una renovación, después de <c>Documento.SustituirPor</c>.
    /// </summary>
    /// <returns>La Gestión que nació, o null si no nació ninguna.</returns>
    Task<GestionAutomatica?> AgregarAsync(Documento nacido, Documento? sustituido, CancellationToken cancellationToken = default);

    /// <summary>
    /// Agrega, sin guardar, la Gestión de cada acto ya confirmado que nació sin ella (la conciliación de
    /// <c>IConciliacionGestionesAutomaticasService</c>), con la misma regla que <see cref="AgregarAsync"/>: con archivo,
    /// un paso por cada acreditación del acto ante un acceso de Plataforma, y sin pasos no nace. No marca la Gestión
    /// del Documento anterior: esa sustitución la deriva la lectura (<c>GestionAutomatica.ResolverEstado</c>), y la
    /// Gestión nace sin autor conocido, porque quien hizo el acto no queda en el Documento.
    /// </summary>
    /// <returns>Las Gestiones que nacieron, una por acto como mucho.</returns>
    Task<IReadOnlyList<GestionAutomatica>> AgregarDeActosSinGestionAsync(
        IReadOnlyCollection<ActoSinGestionAutomatica> actos, CancellationToken cancellationToken = default);
}

/// <summary>
/// Un acto ya confirmado (alta o renovación con archivo, o generación desde plantilla) que no hizo nacer su Gestión:
/// el Documento que nació, si nació renovando otro, y las acreditaciones que ese mismo acto hizo nacer.
/// </summary>
public sealed record ActoSinGestionAutomatica(
    Documento Nacido, bool NacioPorRenovacion, IReadOnlyCollection<AcreditacionDocumentoPlataforma> AcreditacionesDelActo);

public class NacimientoGestionAutomaticaService(
    IGestionAutomaticaRepository repositorio,
    IAcreditacionDocumentoPlataformaRepository acreditaciones,
    IDocumentosQueryContext documentosContext,
    ICentrosQueryContext centrosContext,
    ICurrentUserService currentUserService,
    TimeProvider reloj)
    : INacimientoGestionAutomaticaService
{
    public async Task<GestionAutomatica?> AgregarAsync(
        Documento nacido, Documento? sustituido, CancellationToken cancellationToken = default)
    {
        // Sin archivo no hay acto que haga nacer (NacerYAgregar) y tampoco se toca la Gestión anterior.
        if (string.IsNullOrWhiteSpace(nacido.ArchivoUrl))
            return null;

        var delActo = acreditaciones.AgregadasSinGuardar(nacido.Id);
        var canalesDePlataforma = await CanalesDePlataformaAsync(delActo, cancellationToken);
        var nueva = NacerYAgregar(
            nacido, sustituido is not null, delActo, canalesDePlataforma, await currentUserService.ObtenerUsuarioActualIdAsync());

        if (sustituido is not null)
            await SustituirLaAnteriorSiSigueAbiertaAsync(sustituido, nueva?.Id, cancellationToken);

        return nueva;
    }

    public async Task<IReadOnlyList<GestionAutomatica>> AgregarDeActosSinGestionAsync(
        IReadOnlyCollection<ActoSinGestionAutomatica> actos, CancellationToken cancellationToken = default)
    {
        // Una sola consulta de accesos para todos los actos del lote: la regla es la misma que la del acto en curso.
        var canalesDePlataforma = await CanalesDePlataformaAsync(
            actos.SelectMany(a => a.AcreditacionesDelActo).ToList(), cancellationToken);

        var nacidas = new List<GestionAutomatica>();
        foreach (var acto in actos)
        {
            var nueva = NacerYAgregar(
                acto.Nacido, acto.NacioPorRenovacion, acto.AcreditacionesDelActo, canalesDePlataforma, nacidaPorUsuarioId: null);
            if (nueva is not null)
                nacidas.Add(nueva);
        }

        return nacidas;
    }

    /// <summary>
    /// La regla de nacimiento (§ 4.1): sin archivo no nace; con él, un paso por cada acreditación del acto ante un acceso
    /// de Plataforma (los de correo nunca dan pasos, D-14), y sin pasos tampoco nace (regla 2).
    /// </summary>
    private GestionAutomatica? NacerYAgregar(
        Documento nacido, bool nacioPorRenovacion, IEnumerable<AcreditacionDocumentoPlataforma> delActo,
        IReadOnlySet<Guid> canalesDePlataforma, Guid? nacidaPorUsuarioId)
    {
        // El disparador es el acto con archivo: un alta sin archivo no hace nacer ninguna Gestión. Una renovación
        // siempre trae archivo nuevo (sin él es una corrección de fechas, que no llama aquí).
        if (string.IsNullOrWhiteSpace(nacido.ArchivoUrl))
            return null;

        var origen = nacioPorRenovacion ? OrigenGestionAutomatica.RenovacionDocumento : OrigenGestionAutomatica.AltaDocumento;
        var nueva = GestionAutomatica.Nacer(
            nacido.Id, origen,
            delActo.Where(a => a.DocumentoId == nacido.Id && canalesDePlataforma.Contains(a.CanalGestionDocumentalId))
                .Select(a => a.Id),
            nacidaPorUsuarioId);
        if (nueva is not null)
            repositorio.Agregar(nueva);
        return nueva;
    }

    private async Task<IReadOnlySet<Guid>> CanalesDePlataformaAsync(
        IReadOnlyCollection<AcreditacionDocumentoPlataforma> acreditacionesDelActo, CancellationToken cancellationToken)
    {
        var canalIds = acreditacionesDelActo.Select(a => a.CanalGestionDocumentalId).Distinct().ToArray();
        var canalesDePlataforma = new HashSet<Guid>();
        if (canalIds.Length > 0)
            canalesDePlataforma.UnionWith(await centrosContext.CanalesGestionDocumental
                .Where(c => canalIds.Contains(c.Id) && c.Tipo == TipoCanalGestion.Plataforma)
                .Select(c => c.Id)
                .ToListAsync(cancellationToken));
        return canalesDePlataforma;
    }

    private async Task SustituirLaAnteriorSiSigueAbiertaAsync(
        Documento sustituido, Guid? nuevaId, CancellationToken cancellationToken)
    {
        var anterior = await repositorio.ObtenerPorDocumentoIdAsync(sustituido.Id, cancellationToken);
        if (anterior is null || anterior.EstaSustituida)
            return;

        // El estado de la Gestión, no el de lo que ve quien renueva: todos sus pasos, leídos de la base. Esta lectura
        // puede quedar obsoleta antes del commit (otro guardado cambia una acreditación sin tocar el Documento) y no se
        // bloquea: la marca solo fija el instante y el enlace. Que la Gestión de un Documento sustituido no quede
        // abierta no depende de acertar aquí, sino de la lectura (ResolverEstado con documentoSustituido).
        var acreditacionIds = anterior.Pasos.Select(p => p.AcreditacionId).ToArray();
        var estados = await documentosContext.AcreditacionesDocumentoPlataforma
            .Where(a => acreditacionIds.Contains(a.Id))
            .Select(a => a.Estado)
            .ToListAsync(cancellationToken);
        if (GestionAutomatica.ResolverEstado(sustituida: false, documentoSustituido: false, estados) == EstadoGestionAutomatica.Cerrada)
            return;

        anterior.Sustituir(nuevaId, reloj.GetUtcNow().UtcDateTime);
    }
}
