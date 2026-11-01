using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Commands.MarcarAcreditacionSubida;
using CaeManager.Application.Proyectos;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Documentos.Commands.MarcarAcreditacionRechazada;

/// <summary>"Marcar rechazado…" del drill-down por plataforma (mockup Documentos TALVEG: causa tipificada + detalle libre) — ver MarcarAcreditacionSubidaCommand, mismo motivo.</summary>
public record MarcarAcreditacionRechazadaCommand(Guid AcreditacionId, CausaRechazoAcreditacion Causa, string MotivoLiteral) : ICommand;

public class MarcarAcreditacionRechazadaCommandValidator : AbstractValidator<MarcarAcreditacionRechazadaCommand>
{
    public MarcarAcreditacionRechazadaCommandValidator()
    {
        RuleFor(c => c.AcreditacionId).NotEmpty();
        RuleFor(c => c.MotivoLiteral).NotEmpty().WithMessage("Describe el motivo del rechazo.");
    }
}

public class MarcarAcreditacionRechazadaCommandHandler(
    IAcreditacionDocumentoPlataformaRepository acreditacionRepositorio, IDocumentoRepository documentoRepositorio,
    IAlcanceDatosService alcanceDatos, IProyectosQueryContext proyectosContext, ICentrosQueryContext centrosContext, IUnitOfWork unitOfWork)
    : IRequestHandler<MarcarAcreditacionRechazadaCommand, Result>
{
    public async Task<Result> Handle(MarcarAcreditacionRechazadaCommand request, CancellationToken cancellationToken)
    {
        var acreditacion = await AcreditacionSubible.CargarVisibleAsync(
            request.AcreditacionId, acreditacionRepositorio, documentoRepositorio, alcanceDatos, proyectosContext,
            centrosContext, recargar: false, cancellationToken);
        if (acreditacion is null)
            return Result.Fallo(Error.Crear("Acreditacion.NoEncontrada", "No encontramos esta acreditación."));

        acreditacion.Rechazar(request.Causa, request.MotivoLiteral, DateTime.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
