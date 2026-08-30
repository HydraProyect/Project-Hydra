using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Clientes.Commands.EliminarClientes;

/// <summary>
/// Borrado en lote (P3-31) — misma regla de negocio que
/// <see cref="EliminarCliente.EliminarClienteCommand"/> aplicada fila a fila,
/// una única transacción. Éxito parcial no es fallo del Command: se reporta
/// en <see cref="ResultadoEliminacionLoteDto"/> para que la UI lo resuma.
/// </summary>
public record EliminarClientesCommand(IReadOnlyList<Guid> Ids) : ICommand<ResultadoEliminacionLoteDto>;

public record ResultadoEliminacionLoteDto(int Eliminados, IReadOnlyList<string> Errores);

public class EliminarClientesCommandValidator : AbstractValidator<EliminarClientesCommand>
{
    public EliminarClientesCommandValidator() => RuleFor(c => c.Ids).NotEmpty();
}

public class EliminarClientesCommandHandler(
    IEmpresaRepository repositorio, IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork,
    ICurrentUserService currentUserService)
    : IRequestHandler<EliminarClientesCommand, Result<ResultadoEliminacionLoteDto>>
{
    public async Task<Result<ResultadoEliminacionLoteDto>> Handle(EliminarClientesCommand request, CancellationToken cancellationToken)
    {
        // Auditoría Módulo 5, hallazgo crítico 7/9 — ver EliminarCentroCommand.
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo<ResultadoEliminacionLoteDto>(Error.Crear("Cliente.SinIdentidad", "No se pudo confirmar tu identidad. Vuelve a iniciar sesión e inténtalo de nuevo."));

        var eliminados = 0;
        var errores = new List<string>();

        foreach (var id in request.Ids)
        {
            var empresa = await repositorio.ObtenerPorIdAsync(id, cancellationToken);
            if (empresa is null || !await alcanceDatos.ClienteVisibleAsync(empresa.Id, cancellationToken))
            {
                errores.Add("Un cliente ya no existía.");
                continue;
            }

            if (await repositorio.TieneCentrosComoTitularAsync(id, cancellationToken))
            {
                errores.Add($"{empresa.RazonSocial}: tiene centros activos.");
                continue;
            }

            empresa.MarcarComoEliminado(usuarioId.Value);
            eliminados++;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(new ResultadoEliminacionLoteDto(eliminados, errores));
    }
}
