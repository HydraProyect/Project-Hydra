using CaeManager.Application.Asignaciones;
using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Common;
using CaeManager.Domain.Trabajadores;
using MediatR;

namespace CaeManager.Application.Trabajadores.Commands.ResolverDeteccionAusente;

/// <summary>
/// Resuelve una DeteccionTrabajador de tipo Ausente: si Desactivar es true,
/// da de baja (soft delete) al trabajador que ya no aparece en el
/// documento; si es false, se mantiene activo tal cual — p. ej. porque solo
/// falta puntualmente en este documento concreto (ver DeteccionTrabajadoresService, Fase 36).
/// Devuelve <see cref="ResultadoResolucionAusente"/>: lo que la operación hizo de verdad.
/// </summary>
[AreaDelEncargo("Trabajadores")]
public record ResolverDeteccionAusenteCommand(Guid DeteccionId, bool Desactivar) : ICommand<ResultadoResolucionAusente>;

/// <summary>
/// Lo que de verdad hizo la resolución. Existe para separar la baja aplicada
/// por esta operación de la del trabajador que ya no estaba activo: antes las
/// dos devolvían el mismo éxito, y la pantalla anunciaba «Trabajador dado de
/// baja» por una baja que esta operación no había hecho.
/// </summary>
public enum ResultadoResolucionAusente
{
    /// <summary>El trabajador sigue activo; la detección se da por resuelta sin cambios.</summary>
    Mantenido,

    /// <summary>Esta operación dio de baja al trabajador y le cerró las asignaciones.</summary>
    DadoDeBaja,

    /// <summary>
    /// Se pidió la baja, pero el trabajador ya no estaba activo: no había nada
    /// que dar de baja. La detección se cierra igualmente, porque ya no tiene sentido.
    /// </summary>
    YaNoEstabaActivo,
}

public class ResolverDeteccionAusenteCommandHandler(
    IDeteccionTrabajadorRepository deteccionRepositorio,
    ITrabajadorRepository trabajadorRepositorio,
    IAsignacionRepository asignaciones,
    IAlcanceDatosService alcanceDatos,
    ICurrentUserService currentUserService,
    IUnitOfWork unitOfWork)
    : IRequestHandler<ResolverDeteccionAusenteCommand, Result<ResultadoResolucionAusente>>
{
    public async Task<Result<ResultadoResolucionAusente>> Handle(ResolverDeteccionAusenteCommand request, CancellationToken cancellationToken)
    {
        var deteccion = await deteccionRepositorio.ObtenerPorIdAsync(request.DeteccionId, cancellationToken);
        if (deteccion is null || deteccion.Tipo != TipoDeteccion.Ausente || deteccion.TrabajadorExistenteId is null)
            return Result.Fallo<ResultadoResolucionAusente>(Error.Crear("Deteccion.NoEncontrada", "No encontramos esta detección."));

        if (deteccion.Resuelta)
            return Result.Fallo<ResultadoResolucionAusente>(Error.Crear("Deteccion.YaResuelta", "Esta detección ya fue gestionada."));

        // Defensa en profundidad (REC-149): inalcanzable para el rol Cliente
        // vía AutorizacionEscrituraBehavior; alcance de gestión como segunda
        // barrera independiente.
        if (!await alcanceDatos.EmpresaParaGestionVisibleAsync(deteccion.EmpresaId, cancellationToken))
            return Result.Fallo<ResultadoResolucionAusente>(Error.Crear("Deteccion.NoEncontrada", "No encontramos esta detección."));

        if (!request.Desactivar)
        {
            deteccion.Resolver("Mantenido");
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return Result.Exito(ResultadoResolucionAusente.Mantenido);
        }

        var trabajador = await trabajadorRepositorio.ObtenerPorIdAsync(deteccion.TrabajadorExistenteId.Value, cancellationToken);
        if (trabajador is null)
        {
            // El conjunto de trabajadores lleva el filtro global de borrado
            // lógico, así que si sale nulo lo normal es que ya estuviera dado de
            // baja —por otra persona, o por otra detección del mismo trabajador—.
            // Esta rama se registraba como "Mantenido", que dice que alguien
            // decidió conservarlo, y devolvía el mismo éxito que una baja real.
            deteccion.Resolver("YaNoActivo");
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return Result.Exito(ResultadoResolucionAusente.YaNoEstabaActivo);
        }

        // D-4 (#2005): la baja la decide una persona desde la pantalla de detecciones, y su autor es esa persona. Antes,
        // sin identidad se guardaba Guid.Empty como autor, una baja sin nadie detrás. Como en EliminarTrabajador, sin
        // identidad no se da de baja a nadie, y se falla antes de tocar nada. Aquí, además, un claim con el identificador
        // nulo (todo ceros) tampoco es una identidad (Codex r3, #2266); las demás bajas de persona no lo comprueban aún.
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null || usuarioId.Value == Guid.Empty)
            return Result.Fallo<ResultadoResolucionAusente>(Error.Crear("Deteccion.SinIdentidad", "No se pudo confirmar tu identidad. Vuelve a iniciar sesión e inténtalo de nuevo."));

        trabajador.MarcarComoEliminado(usuarioId.Value);
        await CierreDeAsignaciones.PorTrabajadorEliminadoAsync(asignaciones, trabajador.Id, cancellationToken);
        deteccion.Resolver("Desactivado");
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(ResultadoResolucionAusente.DadoDeBaja);
    }
}
