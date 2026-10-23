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
        // Un único error para «no existe», «está dado de baja» y «no lo ves», sea el Cliente empresarial, el Centro o la
        // Empresa contratista: la respuesta no sirve de oráculo de qué Empresas o Centros hay en el Tenant ni fuera del
        // alcance del actor.
        var noEncontrado = Error.Crear(
            "ContratoAcceso.CentroNoEncontrado", "No encontramos el Centro de trabajo o la Empresa contratista seleccionados.");

        if (!await ContratoAccesoAutorizacion.VisibleAsync(request.ClienteEmpresarialId, alcanceDatos, cancellationToken))
            return Result.Fallo<Guid>(noEncontrado);

        var centro = await centrosContext.Centros
            .Where(c => c.Id == request.CentroId)
            .Select(c => new { c.ClienteId })
            .FirstOrDefaultAsync(cancellationToken);

        if (centro is null)
            return Result.Fallo<Guid>(noEncontrado);

        if (centro.ClienteId != request.ClienteEmpresarialId)
        {
            // Decir «no pertenece» de un Centro cuyo Cliente empresarial no ves confirmaría que ese Centro existe.
            if (!await ContratoAccesoAutorizacion.VisibleAsync(centro.ClienteId, alcanceDatos, cancellationToken))
                return Result.Fallo<Guid>(noEncontrado);
            return Result.Fallo<Guid>(Error.Crear(
                "ContratoAcceso.CentroNoPerteneceACliente", "Este Centro de trabajo no pertenece al Cliente seleccionado."));
        }

        // Existencia en el Tenant actual, sin baja (también la filtran el filtro global y RLS debajo); la autoridad no
        // pasa por la Empresa contratista (ver ContratoAccesoAutorizacion). La contratista igual al Cliente empresarial
        // la rechaza antes el validador con su mensaje de campo; aquí cae en el mismo error genérico.
        var contratistaValida = request.EmpresaContratistaId != request.ClienteEmpresarialId
            && await empresasContext.Empresas
                .AnyAsync(e => e.Id == request.EmpresaContratistaId && !e.EstaEliminado, cancellationToken);
        if (!contratistaValida)
            return Result.Fallo<Guid>(noEncontrado);

        var contrato = ContratoAcceso.Crear(
            request.ClienteEmpresarialId, request.CentroId, request.EmpresaContratistaId, request.FechaInicio, request.FechaFin, request.Notas);
        repositorio.Agregar(contrato);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(contrato.Id);
    }
}
