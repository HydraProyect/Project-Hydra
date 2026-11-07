using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Documentos.Eventos;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Gestiones;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Gestiones.Eventos;

/// <summary>
/// Hace nacer la <see cref="GestionAutomatica"/> de un Documento recién nacido (alta o renovación con archivo
/// nuevo, § 4.1): una sola Gestión con un paso por cada acreditación en plataforma que el mismo acto creó, y
/// solo de accesos de tipo Plataforma (los de correo nunca dan pasos, D-14). Sin acreditaciones no nace nada
/// (regla 2). Si el Documento sustituye a otro cuya Gestión sigue abierta, la cierra como Sustituida con
/// enlace a la nueva (regla 4).
///
/// <para>
/// Qué accesos dan pasos lo decide la regla única de alta de acreditaciones
/// (<c>IAltaAcreditacionesPlataformaService</c>, A-1), que ya corrió en el mismo guardado que el Documento: la
/// Gestión solo agrupa lo que ese acto creó, no recalcula el universo.
/// </para>
///
/// <para>
/// Idempotente: una por Documento (índice único), así que un evento repetido no crea otra. Traga sus propios
/// errores, como <c>EvaluarExpedienteAlCambiarDocumentacionHandler</c>: el Documento ya está guardado y el
/// alta no debe fallar porque la Gestión no pudiera nacer. Lee y escribe con los contextos del Tenant en curso,
/// bajo RLS: la Gestión nace en el Tenant propietario del Documento.
/// </para>
/// </summary>
public class CrearGestionAutomaticaAlNacerDocumentoHandler(
    IGestionAutomaticaRepository repositorio,
    IDocumentosQueryContext documentosContext,
    ICentrosQueryContext centrosContext,
    ICurrentUserService currentUserService,
    IUnitOfWork unitOfWork,
    TimeProvider reloj,
    ILogger<CrearGestionAutomaticaAlNacerDocumentoHandler> logger)
    : INotificationHandler<DocumentoNacidoEvent>
{
    public async Task Handle(DocumentoNacidoEvent notification, CancellationToken cancellationToken)
    {
        try
        {
            await CrearAsync(notification, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudo crear la gestión automática del documento {DocumentoId}.", notification.DocumentoId);
        }
    }

    private async Task CrearAsync(DocumentoNacidoEvent notification, CancellationToken cancellationToken)
    {
        if (await repositorio.ExisteParaDocumentoAsync(notification.DocumentoId, cancellationToken))
            return;

        var acreditacionIds = await (
                from acreditacion in documentosContext.AcreditacionesDocumentoPlataforma
                join canal in centrosContext.CanalesGestionDocumental on acreditacion.CanalGestionDocumentalId equals canal.Id
                where acreditacion.DocumentoId == notification.DocumentoId && canal.Tipo == TipoCanalGestion.Plataforma
                select acreditacion.Id)
            .ToListAsync(cancellationToken);

        var origen = notification.SustituyeADocumentoId is null
            ? OrigenGestionAutomatica.AltaDocumento
            : OrigenGestionAutomatica.RenovacionDocumento;
        var nueva = GestionAutomatica.Nacer(
            notification.DocumentoId, origen, acreditacionIds, await currentUserService.ObtenerUsuarioActualIdAsync());
        if (nueva is not null)
            repositorio.Agregar(nueva);

        var sustituida = notification.SustituyeADocumentoId is { } anteriorId
                         && await SustituirLaAnteriorSiSigueAbiertaAsync(anteriorId, nueva?.Id, cancellationToken);

        if (nueva is not null || sustituida)
            await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    private async Task<bool> SustituirLaAnteriorSiSigueAbiertaAsync(Guid documentoAnteriorId, Guid? nuevaId, CancellationToken cancellationToken)
    {
        var anterior = await repositorio.ObtenerPorDocumentoIdAsync(documentoAnteriorId, cancellationToken);
        if (anterior is null || anterior.EstaSustituida)
            return false;

        // El estado de la Gestión, no el de lo que ve quien renueva: todos sus pasos.
        var acreditacionIds = anterior.Pasos.Select(p => p.AcreditacionId).ToArray();
        var estados = await documentosContext.AcreditacionesDocumentoPlataforma
            .Where(a => acreditacionIds.Contains(a.Id))
            .Select(a => a.Estado)
            .ToListAsync(cancellationToken);
        if (GestionAutomatica.ResolverEstado(sustituida: false, estados) == EstadoGestionAutomatica.Cerrada)
            return false;

        anterior.Sustituir(nuevaId, reloj.GetUtcNow().UtcDateTime);
        return true;
    }
}
