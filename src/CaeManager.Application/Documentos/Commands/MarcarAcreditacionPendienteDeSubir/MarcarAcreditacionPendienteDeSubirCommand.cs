using CaeManager.Application.Common;
using CaeManager.Application.Proyectos;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Documentos.Commands.MarcarAcreditacionPendienteDeSubir;

/// <summary>
/// «Pendiente de subir» del panel «Gestionar acreditación en plataforma» (Centro 360, Cliente 360 y listas por Centro o
/// plataforma): cambia solo el estado de la acreditación en esa plataforma, nunca el Documento ni su vigencia en TALVEG.
/// Mismo alcance que <c>MarcarAcreditacionSubidaCommand</c>: la acreditación se ve si su Documento se ve, que es la
/// comprobación de cartera del Gestor CAE. Una Rechazada no vuelve a pendiente sin versión nueva (ver la entidad).
/// </summary>
public record MarcarAcreditacionPendienteDeSubirCommand(Guid AcreditacionId) : ICommand;

public class MarcarAcreditacionPendienteDeSubirCommandValidator : AbstractValidator<MarcarAcreditacionPendienteDeSubirCommand>
{
    public MarcarAcreditacionPendienteDeSubirCommandValidator()
    {
        RuleFor(c => c.AcreditacionId).NotEmpty();
    }
}

public class MarcarAcreditacionPendienteDeSubirCommandHandler(
    IAcreditacionDocumentoPlataformaRepository acreditacionRepositorio, IDocumentoRepository documentoRepositorio,
    IAlcanceDatosService alcanceDatos, IProyectosQueryContext proyectosContext, IUnitOfWork unitOfWork)
    : IRequestHandler<MarcarAcreditacionPendienteDeSubirCommand, Result>
{
    public const string CodigoTransicionNoPermitida = "Acreditacion.NoVuelveAPendiente";

    public async Task<Result> Handle(MarcarAcreditacionPendienteDeSubirCommand request, CancellationToken cancellationToken)
    {
        var acreditacion = await acreditacionRepositorio.ObtenerPorIdActualizadoAsync(request.AcreditacionId, cancellationToken);
        if (acreditacion is null)
            return Result.Fallo(Error.Crear("Acreditacion.NoEncontrada", "No encontramos esta acreditación."));

        var documento = await documentoRepositorio.ObtenerPorIdAsync(acreditacion.DocumentoId, cancellationToken);
        if (documento is null || !await alcanceDatos.DocumentoVisibleAsync(documento, proyectosContext, cancellationToken))
            return Result.Fallo(Error.Crear("Acreditacion.NoEncontrada", "No encontramos esta acreditación."));

        if (acreditacion.Estado == EstadoAcreditacion.PendienteDeSubir)
            return Result.Exito();

        if (!acreditacion.PuedeDevolverAPendienteDeSubir)
            return Result.Fallo(Error.Crear(
                CodigoTransicionNoPermitida,
                acreditacion.Estado == EstadoAcreditacion.Rechazada
                    ? "La plataforma rechazó esta versión. Sube una versión corregida del documento: al guardarla volverá a quedar pendiente de subir."
                    : "Esta plataforma no exige este documento."));

        acreditacion.DevolverAPendienteDeSubir();
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
