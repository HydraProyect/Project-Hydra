using CaeManager.Application.Common;
using CaeManager.Domain.Configuracion;

namespace CaeManager.Infrastructure.Configuracion;

/// <summary>
/// Envoltorio de conveniencia sobre <see cref="IEstadoAutomatizacionRepository"/>
/// para los hosted services (Configuración, Parte XVI PROMPT 08) — mismo
/// motivo que <c>IEleccionLiderService</c> vive en Infrastructure y no en
/// Application: solo lo usan trabajos de fondo, nunca un caso de uso
/// disparado por el usuario (eso lo cubre directamente
/// <c>ActualizarEstadoAutomatizacionCommand</c>). Resuelto por ámbito
/// (scoped) dentro del bucle por tenant de cada hosted service, igual que
/// el resto de dependencias de ese bucle.
/// </summary>
public interface IRegistroAutomatizacionesService
{
    Task<bool> EstaActivoAsync(string trabajoId, CancellationToken cancellationToken);

    Task RegistrarEjecucionAsync(string trabajoId, bool exitosa, CancellationToken cancellationToken);
}

public class RegistroAutomatizacionesService(IEstadoAutomatizacionRepository repositorio, IUnitOfWork unitOfWork)
    : IRegistroAutomatizacionesService
{
    public async Task<bool> EstaActivoAsync(string trabajoId, CancellationToken cancellationToken)
    {
        var estado = await repositorio.ObtenerPorTrabajoAsync(trabajoId, cancellationToken);
        return estado?.Activo ?? true;
    }

    public async Task RegistrarEjecucionAsync(string trabajoId, bool exitosa, CancellationToken cancellationToken)
    {
        var estado = await repositorio.ObtenerPorTrabajoAsync(trabajoId, cancellationToken);
        if (estado is null)
        {
            estado = new EstadoAutomatizacion(trabajoId);
            repositorio.Agregar(estado);
        }

        estado.RegistrarEjecucion(DateTime.UtcNow, exitosa);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
