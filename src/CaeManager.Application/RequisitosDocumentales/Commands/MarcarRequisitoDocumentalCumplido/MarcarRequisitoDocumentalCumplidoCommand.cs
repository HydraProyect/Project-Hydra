using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.RequisitosDocumentales;
using MediatR;

namespace CaeManager.Application.RequisitosDocumentales.Commands.MarcarRequisitoDocumentalCumplido;

public record MarcarRequisitoDocumentalCumplidoCommand(Guid Id, bool Cumplido) : ICommand;

public class MarcarRequisitoDocumentalCumplidoCommandHandler(
    IRequisitoDocumentalRepository repositorio, IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
    : IRequestHandler<MarcarRequisitoDocumentalCumplidoCommand, Result>
{
    public async Task<Result> Handle(MarcarRequisitoDocumentalCumplidoCommand request, CancellationToken cancellationToken)
    {
        var requisito = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (requisito is null || !await alcanceDatos.CentroVisibleAsync(requisito.CentroId, cancellationToken))
            return Result.Fallo(Error.Crear("RequisitoDocumental.NoEncontrado", "El requisito no existe o no tienes acceso."));

        if (request.Cumplido) requisito.MarcarCumplido(DateOnly.FromDateTime(DateTime.UtcNow));
        else requisito.DesmarcarCumplido();

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
