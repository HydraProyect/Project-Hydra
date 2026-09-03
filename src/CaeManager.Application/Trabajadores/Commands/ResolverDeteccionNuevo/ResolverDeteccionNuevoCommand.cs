using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Trabajadores;
using MediatR;

namespace CaeManager.Application.Trabajadores.Commands.ResolverDeteccionNuevo;

/// <summary>
/// Resuelve una DeteccionTrabajador de tipo Nuevo: si Crear es true, da de
/// alta al trabajador con los datos extraídos por IA (ver
/// DeteccionTrabajadoresService); si es false, simplemente se descarta —
/// p. ej. porque es un administrativo que no hace falta registrar en el
/// sistema (ver la petición original del usuario, Fase 36).
/// </summary>
public record ResolverDeteccionNuevoCommand(Guid DeteccionId, bool Crear) : ICommand;

public class ResolverDeteccionNuevoCommandHandler(
    IDeteccionTrabajadorRepository deteccionRepositorio,
    ITrabajadorRepository trabajadorRepositorio,
    IAlcanceDatosService alcanceDatos,
    IUnitOfWork unitOfWork)
    : IRequestHandler<ResolverDeteccionNuevoCommand, Result>
{
    public async Task<Result> Handle(ResolverDeteccionNuevoCommand request, CancellationToken cancellationToken)
    {
        var deteccion = await deteccionRepositorio.ObtenerPorIdAsync(request.DeteccionId, cancellationToken);
        if (deteccion is null || deteccion.Tipo != TipoDeteccion.Nuevo)
            return Result.Fallo(Error.Crear("Deteccion.NoEncontrada", "No encontramos esta detección."));

        if (deteccion.Resuelta)
            return Result.Fallo(Error.Crear("Deteccion.YaResuelta", "Esta detección ya fue gestionada."));

        // Defensa en profundidad (REC-149): inalcanzable para el rol Cliente
        // vía AutorizacionEscrituraBehavior; alcance de gestión como segunda
        // barrera independiente.
        if (!await alcanceDatos.EmpresaParaGestionVisibleAsync(deteccion.EmpresaId, cancellationToken))
            return Result.Fallo(Error.Crear("Deteccion.NoEncontrada", "No encontramos esta detección."));

        if (!request.Crear)
        {
            deteccion.Resolver("Descartado");
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return Result.Exito();
        }

        if (await trabajadorRepositorio.ExisteConDniAsync(deteccion.Dni, cancellationToken: cancellationToken))
            return Result.Fallo(Error.Crear("Deteccion.DniDuplicado", "Ya existe un trabajador con este DNI — revisa el listado de trabajadores."));

        Trabajador trabajador;
        try
        {
            trabajador = Trabajador.DeEmpresa(deteccion.EmpresaId, deteccion.Nombre, deteccion.Apellidos, deteccion.Dni);
        }
        catch (ArgumentException ex)
        {
            return Result.Fallo(Error.Crear("Deteccion.DatosInvalidos", $"Los datos detectados no son válidos ({ex.Message}) — puedes darlo de alta manualmente desde Trabajadores."));
        }

        trabajadorRepositorio.Agregar(trabajador);
        deteccion.Resolver("Creado");
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
