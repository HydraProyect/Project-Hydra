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
    IRegistroAccesoDocumentoSensibleRepository repositorio,
    IUnitOfWork unitOfWork)
    : IRegistroAccesoDocumentoSensibleService
{
    public async Task RegistrarSiSensibleAsync(
        Guid documentoId, TipoAccesoDocumentoSensible tipoAcceso, CancellationToken cancellationToken = default)
    {
        // Un solo roundtrip: el TipoDocumentoId del Documento y, con él, la
        // Sensibilidad del catálogo — el punto único de consulta de REC-132.
        // Sin navegación EF (Documento no expone TipoDocumento como
        // navegación cargable aquí); dos consultas encadenadas, no un join,
        // porque el segundo paso solo hace falta cuando el primero resuelve.
        var tipoDocumentoId = await documentosContext.Documentos
            .Where(d => d.Id == documentoId)
            .Select(d => (Guid?)d.TipoDocumentoId)
            .FirstOrDefaultAsync(cancellationToken);

        SensibilidadDocumental sensibilidad;

        if (tipoDocumentoId is { } id)
        {
            var sensibilidadResuelta = await tiposDocumentoContext.TiposDocumento
                .Where(t => t.Id == id)
                .Select(t => (SensibilidadDocumental?)t.Sensibilidad)
                .FirstOrDefaultAsync(cancellationToken);

            // El TipoDocumento tampoco resuelve (catálogo editado entre
            // medias, caso raro): mismo criterio protector que el Documento
            // no resuelto, ver más abajo.
            sensibilidad = sensibilidadResuelta ?? SensibilidadDocumental.CategoriaEspecialSalud;
        }
        else
        {
            // El Documento ya no se pudo resolver (ver el comentario de
            // RegistrarSiSensibleAsync en la interfaz): no hay categoría que
            // consultar, así que se asume la más protectora en vez de omitir
            // el registro.
            sensibilidad = SensibilidadDocumental.CategoriaEspecialSalud;
        }

        // "Nunca registrar de más" (DEC-36): un documento sin datos
        // personales no genera fila.
        if (sensibilidad == SensibilidadDocumental.SinDatosPersonales)
            return;

        var actor = await actorAuditoria.ObtenerAsync();

        repositorio.Agregar(new RegistroAccesoDocumentoSensible(
            documentoId,
            sensibilidad,
            tipoAcceso,
            actor.UsuarioSimuladoId ?? actor.ActorRealUsuarioId,
            actor.ActorRealUsuarioId,
            (TipoViaAccesoAuditoria)actor.Via,
            actor.ViaAccesoId));

        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
