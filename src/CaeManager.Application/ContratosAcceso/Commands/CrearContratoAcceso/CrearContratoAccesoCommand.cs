using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Domain.Common;
using CaeManager.Domain.ContratosAcceso;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.ContratosAcceso.Commands.CrearContratoAcceso;

/// <param name="ClienteEmpresarialId">El Cliente empresarial titular del Centro; tiene que ser el <c>ClienteId</c> del Centro.</param>
/// <param name="EmpresaContratistaId">La Empresa contraparte local que representa a la Empresa contratista en este Tenant.</param>
/// <param name="FechaFin">Fin contratado; null = indefinido.</param>
public record CrearContratoAccesoCommand(
    Guid ClienteEmpresarialId, Guid CentroId, Guid EmpresaContratistaId, DateOnly FechaInicio, DateOnly? FechaFin, string? Notas)
    : ICommand<Guid>;

public class CrearContratoAccesoCommandValidator : AbstractValidator<CrearContratoAccesoCommand>
{
    public CrearContratoAccesoCommandValidator()
    {
        RuleFor(c => c.ClienteEmpresarialId).NotEmpty().WithMessage("Selecciona un Cliente.");
        RuleFor(c => c.CentroId).NotEmpty().WithMessage("Selecciona un Centro de trabajo.");
        RuleFor(c => c.EmpresaContratistaId).NotEmpty().WithMessage("Selecciona una Empresa contratista.");
        RuleFor(c => c.EmpresaContratistaId).NotEqual(c => c.ClienteEmpresarialId)
            .WithMessage("La Empresa contratista no puede ser el propio Cliente.");
        RuleFor(c => c.FechaFin).GreaterThanOrEqualTo(c => c.FechaInicio).When(c => c.FechaFin is not null)
            .WithMessage("La fecha de fin no puede ser anterior a la de inicio.");
        RuleFor(c => c.Notas).MaximumLength(ContratoAcceso.LongitudMaximaNotas);
    }
}

/// <summary>
/// Alta de un contrato de acceso. Sin regla de solape: dos contratos de la misma Empresa contratista en el mismo Centro
/// pueden coexistir mientras DN-6 siga abierta (ver <see cref="ContratoAcceso"/>).
/// </summary>
public class CrearContratoAccesoCommandHandler(
    IContratoAccesoRepository repositorio, ICentrosQueryContext centrosContext, IEmpresasQueryContext empresasContext,
    IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
    : IRequestHandler<CrearContratoAccesoCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CrearContratoAccesoCommand request, CancellationToken cancellationToken)
    {
        // Mismo error para «no existe» y «no lo ves»: la respuesta no revela qué Clientes empresariales hay fuera del
        // alcance del actor.
        var noEncontrado = Error.Crear("ContratoAcceso.CentroNoEncontrado", "No encontramos este Centro de trabajo.");

        if (!await ContratoAccesoAutorizacion.VisibleAsync(request.ClienteEmpresarialId, alcanceDatos, cancellationToken))
            return Result.Fallo<Guid>(noEncontrado);

        var centro = await centrosContext.Centros
            .Where(c => c.Id == request.CentroId)
            .Select(c => new { c.ClienteId })
            .FirstOrDefaultAsync(cancellationToken);

        if (centro is null)
            return Result.Fallo<Guid>(noEncontrado);

        if (centro.ClienteId != request.ClienteEmpresarialId)
            return Result.Fallo<Guid>(Error.Crear(
                "ContratoAcceso.CentroNoPerteneceACliente", "Este Centro de trabajo no pertenece al Cliente seleccionado."));

        if (request.EmpresaContratistaId == request.ClienteEmpresarialId)
            return Result.Fallo<Guid>(Error.Crear(
                "ContratoAcceso.ContratistaEsElCliente", "La Empresa contratista no puede ser el propio Cliente."));

        // Existencia en el Tenant actual (filtro de tenant y soft delete, y RLS debajo); la autoridad no pasa por la
        // Empresa contratista (ver ContratoAccesoAutorizacion).
        var contratistaExiste = await empresasContext.Empresas
            .AnyAsync(e => e.Id == request.EmpresaContratistaId, cancellationToken);
        if (!contratistaExiste)
            return Result.Fallo<Guid>(Error.Crear(
                "ContratoAcceso.ContratistaNoEncontrada", "No encontramos esta Empresa contratista."));

        var contrato = ContratoAcceso.Crear(
            request.ClienteEmpresarialId, request.CentroId, request.EmpresaContratistaId, request.FechaInicio, request.FechaFin, request.Notas);
        repositorio.Agregar(contrato);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(contrato.Id);
    }
}
