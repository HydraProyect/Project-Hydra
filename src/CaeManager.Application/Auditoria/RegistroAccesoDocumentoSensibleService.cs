using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.TiposDocumento;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Documentos;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Auditoria;

/// <inheritdoc cref="IRegistroAccesoDocumentoSensibleService"/>
public class RegistroAccesoDocumentoSensibleService(
    IDocumentosQueryContext documentosContext,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    IActorAuditoria actorAuditoria,
    IRegistroAccesoDocumentoSensibleRepository repositorio)
    : IRegistroAccesoDocumentoSensibleService
{
    public async Task RegistrarSiSensibleAsync(
        Guid documentoId, TipoAccesoDocumentoSensible tipoAcceso, CancellationToken cancellationToken = default)
    {
        // Un solo roundtrip adicional: el TipoDocumentoId del Documento y, con
        // él, la Sensibilidad del catálogo — el punto único de consulta de
        // REC-132. Sin navegación EF (Documento no expone TipoDocumento como
        // navegación cargable aquí); dos consultas encadenadas, no un join,
        // porque el segundo paso solo hace falta cuando el primero resuelve.
        var tipoDocumentoId = await documentosContext.Documentos
            .Where(d => d.Id == documentoId)
            .Select(d => (Guid?)d.TipoDocumentoId)
            .FirstOrDefaultAsync(cancellationToken);

        // El Documento ya no se pudo resolver (baja física, caso raro fuera
        // de la retención ordinaria): no hay categoría que consultar, así que
        // se asume la más protectora en vez de omitir el registro — ver
        // RegistrarSiSensibleAsync en la interfaz.
        if (tipoDocumentoId is not { } id)
        {
            await RegistrarAsync(documentoId, SensibilidadDocumental.CategoriaEspecialSalud, tipoAcceso, cancellationToken);
            return;
        }

        await RegistrarSiSensibleAsync(documentoId, id, tipoAcceso, cancellationToken);
    }

    public async Task RegistrarSiSensibleAsync(
        Guid recursoId, Guid tipoDocumentoId, TipoAccesoDocumentoSensible tipoAcceso, CancellationToken cancellationToken = default)
    {
        var sensibilidadResuelta = await tiposDocumentoContext.TiposDocumento
            .Where(t => t.Id == tipoDocumentoId)
            .Select(t => (SensibilidadDocumental?)t.Sensibilidad)
            .FirstOrDefaultAsync(cancellationToken);

        // El TipoDocumento tampoco resuelve (catálogo editado entre medias,
        // caso raro): mismo criterio protector que el Documento no resuelto.
        var sensibilidad = sensibilidadResuelta ?? SensibilidadDocumental.CategoriaEspecialSalud;

        await RegistrarAsync(recursoId, sensibilidad, tipoAcceso, cancellationToken);
    }

    private async Task RegistrarAsync(
        Guid recursoId, SensibilidadDocumental sensibilidad, TipoAccesoDocumentoSensible tipoAcceso, CancellationToken cancellationToken)
    {
        // "Nunca registrar de más" (DEC-36): un documento sin datos
        // personales no genera fila.
        if (sensibilidad == SensibilidadDocumental.SinDatosPersonales)
            return;

        var actor = await actorAuditoria.ObtenerAsync();

        var registro = new RegistroAccesoDocumentoSensible(
            recursoId,
            sensibilidad,
            tipoAcceso,
            actor.UsuarioSimuladoId ?? actor.ActorRealUsuarioId,
            actor.ActorRealUsuarioId,
            (TipoViaAccesoAuditoria)actor.Via,
            actor.ViaAccesoId);

        // El repositorio decide si el fallo de guardado se propaga o se
        // tolera (sesión de soporte sin privilegio de escritura) — Application
        // no puede distinguirlo sin conocer el proveedor de EF concreto, ver
        // el comentario de IRegistroAccesoDocumentoSensibleRepository.
        await repositorio.GuardarAsync(registro, cancellationToken);
    }
}
