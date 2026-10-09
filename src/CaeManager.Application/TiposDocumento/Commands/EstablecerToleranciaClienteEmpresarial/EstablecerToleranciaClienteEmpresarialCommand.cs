using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.TiposDocumento.Commands.EstablecerToleranciaClienteEmpresarial;

/// <summary>
/// Fija la tolerancia por defecto de un Cliente empresarial para un Tipo de documento: los días que el documento sigue
/// valiendo para acceder a los Centros de ese Cliente empresarial tras vencer. Sus Centros la heredan salvo que
/// personalicen la suya (<see cref="TipoDocumentoCentro.ToleranciaDias"/>). Ver
/// <see cref="ToleranciaDocumentoClienteEmpresarial"/> y <see cref="ReglaBloqueoDeAcceso"/>.
/// <c>null</c> o 0 = sin tolerancia (borra la fila: sin fila la tolerancia es 0).
/// </summary>
public record EstablecerToleranciaClienteEmpresarialCommand(Guid ClienteEmpresarialId, Guid TipoDocumentoId, int? ToleranciaDias) : ICommand;

public class EstablecerToleranciaClienteEmpresarialCommandValidator : AbstractValidator<EstablecerToleranciaClienteEmpresarialCommand>
{
    public EstablecerToleranciaClienteEmpresarialCommandValidator()
    {
        RuleFor(c => c.ClienteEmpresarialId).NotEmpty();
        RuleFor(c => c.TipoDocumentoId).NotEmpty();
        RuleFor(c => c.ToleranciaDias).InclusiveBetween(0, TipoDocumentoCentro.ToleranciaMaximaDias).When(c => c.ToleranciaDias is not null)
            .WithMessage($"La tolerancia debe ser un número entero de días entre 0 y {TipoDocumentoCentro.ToleranciaMaximaDias}.");
    }
}

public class EstablecerToleranciaClienteEmpresarialCommandHandler(
    IToleranciaDocumentoClienteEmpresarialRepository repositorio,
    IEmpresasQueryContext empresasContext,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    IAlcanceDatosService alcanceDatosService,
    IUnitOfWork unitOfWork)
    : IRequestHandler<EstablecerToleranciaClienteEmpresarialCommand, Result>
{
    public async Task<Result> Handle(EstablecerToleranciaClienteEmpresarialCommand request, CancellationToken cancellationToken)
    {
        // Mismo control de alcance que ActualizarLecturaIaClienteCommand: un Gestor CAE solo configura Clientes
        // empresariales de su cartera; con acceso total solo se verifica que el Id exista en este Tenant.
        if (!await alcanceDatosService.TieneAccesoTotalAsync(cancellationToken))
        {
            var visibles = await alcanceDatosService.ObtenerClienteIdsVisiblesAsync(cancellationToken);
            if (visibles is null || !visibles.Contains(request.ClienteEmpresarialId))
                return Result.Fallo(Error.Crear("ToleranciaCliente.SinAcceso", "No tienes acceso a este Cliente."));
        }
        else if (!await empresasContext.Empresas.AnyAsync(c => c.Id == request.ClienteEmpresarialId, cancellationToken))
        {
            return Result.Fallo(Error.Crear("ToleranciaCliente.ClienteNoEncontrado", "No encontramos este Cliente."));
        }

        if (!await tiposDocumentoContext.TiposDocumento.AnyAsync(t => t.Id == request.TipoDocumentoId, cancellationToken))
            return Result.Fallo(Error.Crear("ToleranciaCliente.TipoDocumentoNoEncontrado", "No encontramos este tipo de documento."));

        var existente = await repositorio.ObtenerAsync(request.ClienteEmpresarialId, request.TipoDocumentoId, cancellationToken);
        var dias = request.ToleranciaDias ?? 0;

        if (dias == 0)
        {
            // Sin fila = tolerancia 0 (hereda el valor por defecto); no se guardan ceros.
            if (existente is not null)
                repositorio.Eliminar(existente);
        }
        else if (existente is not null)
        {
            existente.Establecer(dias);
        }
        else
        {
            repositorio.Agregar(new ToleranciaDocumentoClienteEmpresarial(request.ClienteEmpresarialId, request.TipoDocumentoId, dias));
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito();
    }
}
