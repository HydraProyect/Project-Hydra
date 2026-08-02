using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Trabajadores;
using MediatR;

namespace CaeManager.Application.Trabajadores.Commands.ResolverDeteccionAusente;

/// <summary>
/// Resuelve una DeteccionTrabajador de tipo Ausente: si Desactivar es true,
/// da de baja (soft delete) al trabajador que ya no aparece en el
/// documento; si es false, se mantiene activo tal cual — p. ej. porque solo
/// falta puntualmente en este documento concreto (ver DeteccionTrabajadoresService, Fase 36).
/// </summary>
public record ResolverDeteccionAusenteCommand(Guid DeteccionId, bool Desactivar) : ICommand;

public class ResolverDeteccionAusenteCommandHandler(
    IDeteccionTrabajadorRepository deteccionRepositorio,
    ITrabajadorRepository trabajadorRepositorio,
    IAlcanceDatosService alcanceDatos,
    ICurrentUserService currentUserService,
    IUnitOfWork unitOfWork)
    : IRequestHandler<ResolverDeteccionAusenteCommand, Result>
{
    public async Task<Result> Handle(ResolverDeteccionAusenteCommand request, CancellationToken cancellationToken)
    {
        var deteccion = await deteccionRepositorio.ObtenerPorIdAsync(request.DeteccionId, cancellationToken);
        if (deteccion is null || deteccion.Tipo != TipoDeteccion.Ausente || deteccion.TrabajadorExistenteId is null)
            return Result.Fallo(Error.Crear("Deteccion.NoEncontrada", "No encontramos esta detección."));

        if (deteccion.Resuelta)
            return Result.Fallo(Error.Crear("Deteccion.YaResuelta", "Esta detección ya fue gestionada."));

        if (!await alcanceDatos.EmpresaVisibleAsync(deteccion.EmpresaId, cancellationToken))
            return Result.Fallo(Error.Crear("Deteccion.NoEncontrada", "No encontramos esta detección."));

        if (!request.Desactivar)
        {
            deteccion.Resolver("Mantenido");
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return Result.Exito();
        }

        var trabajador = await trabajadorRepositorio.ObtenerPorIdAsync(deteccion.TrabajadorExistenteId.Value, cancellationToken);
        if (trabajador is null)
        {
            deteccion.Resolver("Mantenido");
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return Result.Exito();
        }

        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        trabajador.MarcarComoEliminado(usuarioId ?? Guid.Empty);
        deteccion.Resolver("Desactivado");
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
