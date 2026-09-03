using CaeManager.Application.Clientes.Commands.EliminarClientes;
using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Empresas.Commands.EliminarEmpresas;

/// <summary>Borrado en lote — ver EliminarClientesCommand para el criterio de éxito parcial.</summary>
public record EliminarEmpresasCommand(IReadOnlyList<Guid> Ids) : ICommand<ResultadoEliminacionLoteDto>;

public class EliminarEmpresasCommandValidator : AbstractValidator<EliminarEmpresasCommand>
{
    public EliminarEmpresasCommandValidator() => RuleFor(c => c.Ids).NotEmpty();
}

public class EliminarEmpresasCommandHandler(
    IEmpresaRepository repositorio, IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork,
    ICurrentUserService currentUserService)
    : IRequestHandler<EliminarEmpresasCommand, Result<ResultadoEliminacionLoteDto>>
{
    public async Task<Result<ResultadoEliminacionLoteDto>> Handle(EliminarEmpresasCommand request, CancellationToken cancellationToken)
    {
        // Auditoría Módulo 5, hallazgo crítico 7/9 — ver EliminarCentroCommand.
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo<ResultadoEliminacionLoteDto>(Error.Crear("Empresa.SinIdentidad", "No se pudo confirmar tu identidad. Vuelve a iniciar sesión e inténtalo de nuevo."));

        var eliminados = 0;
        var errores = new List<string>();

        foreach (var id in request.Ids)
        {
            var empresa = await repositorio.ObtenerPorIdAsync(id, cancellationToken);
            // Defensa en profundidad (REC-149): inalcanzable para el rol
            // Cliente vía AutorizacionEscrituraBehavior; alcance de gestión
            // como segunda barrera independiente.
            if (empresa is null || !await alcanceDatos.EmpresaParaGestionVisibleAsync(empresa.Id, cancellationToken))
            {
                errores.Add("Una empresa ya no existía.");
                continue;
            }

            if (await repositorio.TieneTrabajadoresAsync(id, cancellationToken))
            {
                errores.Add($"{empresa.RazonSocial}: tiene trabajadores.");
                continue;
            }

            empresa.MarcarComoEliminado(usuarioId.Value);
            eliminados++;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(new ResultadoEliminacionLoteDto(eliminados, errores));
    }
}
