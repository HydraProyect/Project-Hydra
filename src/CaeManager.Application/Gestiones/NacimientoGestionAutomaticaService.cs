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
}

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
        // El disparador es el acto con archivo: un alta sin archivo no hace nacer ninguna Gestión. Una renovación
        // siempre trae archivo nuevo (sin él es una corrección de fechas, que no llama aquí).
        if (string.IsNullOrWhiteSpace(nacido.ArchivoUrl))
            return null;

        var delActo = acreditaciones.AgregadasSinGuardar(nacido.Id);
        var canalIds = delActo.Select(a => a.CanalGestionDocumentalId).Distinct().ToArray();
        var canalesDePlataforma = new HashSet<Guid>();
        if (canalIds.Length > 0)
            canalesDePlataforma.UnionWith(await centrosContext.CanalesGestionDocumental
                .Where(c => canalIds.Contains(c.Id) && c.Tipo == TipoCanalGestion.Plataforma)
                .Select(c => c.Id)
                .ToListAsync(cancellationToken));

        var origen = sustituido is null ? OrigenGestionAutomatica.AltaDocumento : OrigenGestionAutomatica.RenovacionDocumento;
        var nueva = GestionAutomatica.Nacer(
            nacido.Id, origen,
            delActo.Where(a => canalesDePlataforma.Contains(a.CanalGestionDocumentalId)).Select(a => a.Id),
            await currentUserService.ObtenerUsuarioActualIdAsync());
        if (nueva is not null)
            repositorio.Agregar(nueva);

        if (sustituido is not null)
            await SustituirLaAnteriorSiSigueAbiertaAsync(sustituido, nueva?.Id, cancellationToken);

        return nueva;
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
