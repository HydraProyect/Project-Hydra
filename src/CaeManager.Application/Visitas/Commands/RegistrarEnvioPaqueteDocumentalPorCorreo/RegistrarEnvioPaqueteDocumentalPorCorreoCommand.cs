using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Documentos.Presentaciones;
using CaeManager.Application.Proyectos;
using CaeManager.Application.Visitas.Queries.ObtenerSolicitudAccesoCorreo;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Visitas.Commands.RegistrarEnvioPaqueteDocumentalPorCorreo;

/// <summary>
/// Deja constancia de que el Gestor CAE <b>envió por correo</b>, desde el compositor de la Visita («Enviar por correo»), el paquete
/// documental con los Documentos dados: para cada uno registra una presentación al Centro de la Visita
/// (<see cref="OrigenPresentacionDocumentoEnCentro.EnvioPorCorreo"/>), ancla de la periodicidad especial de ese Centro. Solo cubre el
/// envío que el Gestor CAE confirma en el compositor; la descarga manual del ZIP y el adjunto automático al buzón
/// (<c>GenerarYEnviarAsync</c>) no son un envío confirmado y no registran nada.
///
/// <para>
/// Los identificadores llegan de la pantalla (los que entraron en el zip, <c>PaqueteDocumentalDescargaDto.DocumentoIds</c>), así que
/// el servidor no se fía de ellos: la Visita tiene que ser visible <b>para gestión</b>, no estar cancelada y ser de un Centro con
/// gestión CAE; y solo se registra un Documento que sea operativo, visible para el usuario y de los que el paquete de esa Visita
/// puede contener (de la Empresa propia del Centro o de un Trabajador de la Visita). El resto se ignora sin registrar nada.
/// </para>
/// </summary>
public record RegistrarEnvioPaqueteDocumentalPorCorreoCommand(Guid VisitaId, IReadOnlyList<Guid> DocumentoIds) : ICommand;

public class RegistrarEnvioPaqueteDocumentalPorCorreoCommandValidator : AbstractValidator<RegistrarEnvioPaqueteDocumentalPorCorreoCommand>
{
    /// <summary>Cota técnica: un paquete de una Visita no lleva ni de lejos tantos documentos.</summary>
    public const int MaximoDocumentos = 500;

    public RegistrarEnvioPaqueteDocumentalPorCorreoCommandValidator()
    {
        RuleFor(c => c.VisitaId).NotEmpty();
        RuleFor(c => c.DocumentoIds).NotNull().Must(ids => ids is { Count: > 0 and <= MaximoDocumentos })
            .WithMessage($"Indica entre 1 y {MaximoDocumentos} documentos.");
    }
}

public class RegistrarEnvioPaqueteDocumentalPorCorreoCommandHandler(
    IVisitasQueryContext visitasContext, ICentrosQueryContext centrosContext, IDocumentosQueryContext documentosContext,
    IAlcanceDatosService alcanceDatos, IProyectosQueryContext proyectosContext,
    IRegistroDePresentaciones presentaciones, IUnitOfWork unitOfWork)
    : IRequestHandler<RegistrarEnvioPaqueteDocumentalPorCorreoCommand, Result>
{
    public async Task<Result> Handle(RegistrarEnvioPaqueteDocumentalPorCorreoCommand request, CancellationToken cancellationToken)
    {
        var visita = await (
            from v in visitasContext.Visitas
            join centro in centrosContext.Centros on v.CentroId equals centro.Id
            where v.Id == request.VisitaId
            select new { CentroId = centro.Id, centro.EmpresaId, centro.GestionCae, v.EstaCancelada })
            .FirstOrDefaultAsync(cancellationToken);

        if (visita is null || !await alcanceDatos.CentroParaGestionVisibleAsync(visita.CentroId, cancellationToken))
            return Result.Fallo(ObtenerSolicitudAccesoCorreoQueryHandler.NoEncontrada);

        if (visita.EstaCancelada)
            return Result.Fallo(ObtenerSolicitudAccesoCorreoQueryHandler.VisitaCancelada);

        if (visita.GestionCae == ModalidadGestionCae.SinGestionCae)
            return Result.Fallo(ObtenerSolicitudAccesoCorreoQueryHandler.CentroSinGestionCae);

        var documentoIds = request.DocumentoIds.Distinct().ToList();
        var trabajadorIds = await visitasContext.VisitasTrabajadores
            .Where(vt => vt.VisitaId == request.VisitaId)
            .Select(vt => vt.TrabajadorId)
            .ToListAsync(cancellationToken);

        // Mismo conjunto de candidatos que el paquete de la Visita (PaqueteDocumentalVisitaService.ConstruirAsync).
        var candidatos = await documentosContext.Documentos.Operativos()
            .Where(d => documentoIds.Contains(d.Id)
                && (d.EmpresaId == visita.EmpresaId || (d.TrabajadorId != null && trabajadorIds.Contains(d.TrabajadorId.Value))))
            .ToListAsync(cancellationToken);

        foreach (var documento in candidatos)
        {
            if (!await alcanceDatos.DocumentoVisibleAsync(documento, proyectosContext, cancellationToken))
                continue;

            await presentaciones.RegistrarAsync(documento.Id, visita.CentroId, OrigenPresentacionDocumentoEnCentro.EnvioPorCorreo, cancellationToken);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito();
    }
}
