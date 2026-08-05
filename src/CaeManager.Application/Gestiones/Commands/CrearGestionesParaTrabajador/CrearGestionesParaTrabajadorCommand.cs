using CaeManager.Application.Asignaciones;
using CaeManager.Application.Common;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Gestiones;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Gestiones.Commands.CrearGestionesParaTrabajador;

/// <summary>
/// Genera una Gestion por cada Centro donde el Trabajador tenga una
/// Asignación activa — "para que el gestor pueda irlo gestionando uno a
/// uno" (petición original). Es la confirmación explícita del Gestor tras
/// una <see cref="SugerenciaGestionCorreo"/> (o un alta manual sin correo de
/// por medio, <paramref name="SugerenciaGestionCorreoId"/> es opcional):
/// nunca se llama automáticamente desde la detección IA.
/// </summary>
public record CrearGestionesParaTrabajadorCommand(
    Guid TrabajadorId, Guid TipoDocumentoId, Guid? SugerenciaGestionCorreoId = null, Guid? MensajeCorreoOrigenId = null)
    : ICommand<ResultadoCrearGestionesDto>;

public record ResultadoCrearGestionesDto(int Creadas);

public class CrearGestionesParaTrabajadorCommandValidator : AbstractValidator<CrearGestionesParaTrabajadorCommand>
{
    public CrearGestionesParaTrabajadorCommandValidator()
    {
        RuleFor(c => c.TrabajadorId).NotEmpty();
        RuleFor(c => c.TipoDocumentoId).NotEmpty();
    }
}

public class CrearGestionesParaTrabajadorCommandHandler(
    IAsignacionesQueryContext asignacionesContext,
    ITipoDocumentoRepository tipoDocumentoRepositorio,
    IGestionRepository gestionRepositorio,
    ISugerenciaGestionCorreoRepository sugerenciaRepositorio,
    IAlcanceDatosService alcanceDatos,
    IUnitOfWork unitOfWork)
    : IRequestHandler<CrearGestionesParaTrabajadorCommand, Result<ResultadoCrearGestionesDto>>
{
    public async Task<Result<ResultadoCrearGestionesDto>> Handle(
        CrearGestionesParaTrabajadorCommand request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.TrabajadorVisibleAsync(request.TrabajadorId, cancellationToken))
            return Result.Fallo<ResultadoCrearGestionesDto>(Error.Crear("Trabajador.NoEncontrado", "El trabajador no existe o no tienes acceso."));

        var tipoDocumento = await tipoDocumentoRepositorio.ObtenerPorIdAsync(request.TipoDocumentoId, cancellationToken);
        if (tipoDocumento is null)
            return Result.Fallo<ResultadoCrearGestionesDto>(Error.Crear("TipoDocumento.NoEncontrado", "El tipo de documento no existe."));

        var centroIds = await asignacionesContext.Asignaciones
            .Where(a => a.TrabajadorId == request.TrabajadorId && a.FechaBaja == null)
            .Select(a => a.CentroId)
            .Distinct()
            .ToListAsync(cancellationToken);

        if (centroIds.Count == 0)
        {
            return Result.Fallo<ResultadoCrearGestionesDto>(Error.Crear(
                "Gestion.SinCentros", "Este trabajador no tiene ninguna asignación activa a un centro."));
        }

        foreach (var centroId in centroIds)
        {
            gestionRepositorio.Agregar(new Gestion(
                request.TrabajadorId, centroId, request.TipoDocumentoId, request.MensajeCorreoOrigenId));
        }

        if (request.SugerenciaGestionCorreoId is { } sugerenciaId)
        {
            var sugerencia = await sugerenciaRepositorio.ObtenerPorIdAsync(sugerenciaId, cancellationToken);
            sugerencia?.Resolver();
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(new ResultadoCrearGestionesDto(centroIds.Count));
    }
}
