using CaeManager.Application.Clientes.Commands.EliminarClientes;
using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Empresas.Commands.EliminarEmpresas;

/// <summary>Borrado en lote — ver EliminarClientesCommand para el criterio de éxito parcial.</summary>
public record EliminarEmpresasCommand(IReadOnlyList<Guid> Ids, Guid UsuarioId) : ICommand<ResultadoEliminacionLoteDto>;

public class EliminarEmpresasCommandValidator : AbstractValidator<EliminarEmpresasCommand>
{
    public EliminarEmpresasCommandValidator() => RuleFor(c => c.Ids).NotEmpty();
}

public class EliminarEmpresasCommandHandler(IEmpresaRepository repositorio, IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
    : IRequestHandler<EliminarEmpresasCommand, Result<ResultadoEliminacionLoteDto>>
{
    public async Task<Result<ResultadoEliminacionLoteDto>> Handle(EliminarEmpresasCommand request, CancellationToken cancellationToken)
    {
        var eliminados = 0;
        var errores = new List<string>();

        foreach (var id in request.Ids)
        {
            var empresa = await repositorio.ObtenerPorIdAsync(id, cancellationToken);
            if (empresa is null || !await alcanceDatos.EmpresaVisibleAsync(empresa.Id, cancellationToken))
            {
                errores.Add("Una empresa ya no existía.");
                continue;
            }

            if (await repositorio.TieneTrabajadoresAsync(id, cancellationToken))
            {
                errores.Add($"{empresa.RazonSocial}: tiene trabajadores.");
                continue;
            }

            empresa.MarcarComoEliminado(request.UsuarioId);
            eliminados++;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(new ResultadoEliminacionLoteDto(eliminados, errores));
    }
}
