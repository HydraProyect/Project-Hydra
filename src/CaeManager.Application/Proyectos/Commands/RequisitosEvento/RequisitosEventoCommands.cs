using CaeManager.Application.Common;
using CaeManager.Application.TiposDocumento;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Proyectos;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Proyectos.Commands.RequisitosEvento;

/// <summary>Añade un Tipo de Documento a los requisitos de un evento. Misma autoridad que el resto de escrituras de Proyecto.</summary>
public record AnadirRequisitoEventoCommand(Guid ProyectoId, Guid TipoDocumentoId) : ICommand;

/// <summary>Quita un Tipo de Documento de los requisitos de un evento (baja física, como <c>TipoDocumentoCentro</c>).</summary>
public record QuitarRequisitoEventoCommand(Guid ProyectoId, Guid TipoDocumentoId) : ICommand;

/// <summary>
/// Guarda los requisitos actuales de un evento como plantilla del Cliente empresarial del evento, para copiarlos
/// al crear el siguiente. Es una foto: los cambios posteriores del evento o de la plantilla no se propagan.
/// </summary>
public record GuardarPlantillaRequisitosEventoCommand(Guid ProyectoId, string Nombre) : ICommand<Guid>;

public class AnadirRequisitoEventoCommandValidator : AbstractValidator<AnadirRequisitoEventoCommand>
{
    public AnadirRequisitoEventoCommandValidator()
    {
        RuleFor(c => c.ProyectoId).NotEmpty();
        RuleFor(c => c.TipoDocumentoId).NotEmpty().WithMessage("Selecciona un tipo de documento.");
    }
}

public class GuardarPlantillaRequisitosEventoCommandValidator : AbstractValidator<GuardarPlantillaRequisitosEventoCommand>
{
    public GuardarPlantillaRequisitosEventoCommandValidator()
    {
        RuleFor(c => c.ProyectoId).NotEmpty();
        RuleFor(c => c.Nombre).NotEmpty().MaximumLength(PlantillaRequisitosEvento.LongitudMaximaNombre);
    }
}

internal static class EventoVisible
{
    public static readonly Error NoEncontrado = Error.Crear("Proyecto.NoEncontrado", "El proyecto no existe o no tienes acceso.");
    public static readonly Error NoEsEvento = Error.Crear("Proyecto.NoEsEvento", "Solo un evento tiene requisitos propios.");

    /// <summary>El Proyecto, si existe, lo ve el actor (<see cref="ProyectoAutorizacion"/>) y es un evento.</summary>
    public static async Task<(Proyecto? Proyecto, Error? Error)> ObtenerAsync(
        IProyectoRepository repositorio, IAlcanceDatosService alcanceDatos, Guid proyectoId, CancellationToken cancellationToken)
    {
        var proyecto = await repositorio.ObtenerPorIdAsync(proyectoId, cancellationToken);
        if (proyecto is null || !await ProyectoAutorizacion.VisibleAsync(proyecto.ClienteId, alcanceDatos, cancellationToken))
            return (null, NoEncontrado);
        return proyecto.EsEvento ? (proyecto, null) : (null, NoEsEvento);
    }
}

public class AnadirRequisitoEventoCommandHandler(
    IProyectoRepository proyectos, IProyectoRequisitoRepository requisitos, ITiposDocumentoQueryContext tiposDocumento,
    IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
    : IRequestHandler<AnadirRequisitoEventoCommand, Result>
{
    public async Task<Result> Handle(AnadirRequisitoEventoCommand request, CancellationToken cancellationToken)
    {
        var (proyecto, error) = await EventoVisible.ObtenerAsync(proyectos, alcanceDatos, request.ProyectoId, cancellationToken);
        if (proyecto is null) return Result.Fallo(error!);

        if (!proyecto.EstaAbierto)
            return Result.Fallo(Error.Crear("Proyecto.YaCerrado", "Este proyecto ya está cerrado."));

        var tipo = await tiposDocumento.TiposDocumento
            .Where(t => t.Id == request.TipoDocumentoId)
            .Select(t => new { t.AmbitoAplicacion, t.Archivado })
            .FirstOrDefaultAsync(cancellationToken);
        if (tipo is null)
            return Result.Fallo(Error.Crear("Proyecto.TipoDocumentoNoEncontrado", "No encontramos este tipo de documento."));
        // Un tipo archivado no se pide de nuevo: se pide su sustituto (catálogo revisado el 2026-10-17).
        if (tipo.Archivado)
            return Result.Fallo(Error.Crear("Proyecto.TipoDocumentoArchivado", "Ese tipo de documento está archivado; pide su sustituto."));
        var ambito = tipo.AmbitoAplicacion;
        // Lo que pide un evento lo aporta cada participante: un documento de Empresa o de Vehículo no lo cumple nadie.
        if (ambito != AmbitoAplicacion.Trabajador)
            return Result.Fallo(Error.Crear("Proyecto.RequisitoNoEsDeTrabajador", "Un evento solo puede pedir documentos de trabajador."));

        var actuales = await requisitos.ListarDeProyectoAsync(proyecto.Id, cancellationToken);
        if (actuales.Any(r => r.TipoDocumentoId == request.TipoDocumentoId))
            return Result.Fallo(Error.Crear("Proyecto.RequisitoYaIncluido", "Este documento ya figura entre los requisitos del evento."));

        requisitos.Agregar(new ProyectoRequisito(proyecto.Id, request.TipoDocumentoId));
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Índice único (Tenant, Proyecto, Tipo): otra alta concurrente del mismo requisito terminó primero.
            return Result.Fallo(Error.Crear("Proyecto.RequisitoYaIncluido", "Este documento ya figura entre los requisitos del evento."));
        }

        return Result.Exito();
    }
}

public class QuitarRequisitoEventoCommandHandler(
    IProyectoRepository proyectos, IProyectoRequisitoRepository requisitos, IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
    : IRequestHandler<QuitarRequisitoEventoCommand, Result>
{
    public async Task<Result> Handle(QuitarRequisitoEventoCommand request, CancellationToken cancellationToken)
    {
        var (proyecto, error) = await EventoVisible.ObtenerAsync(proyectos, alcanceDatos, request.ProyectoId, cancellationToken);
        if (proyecto is null) return Result.Fallo(error!);

        if (!proyecto.EstaAbierto)
            return Result.Fallo(Error.Crear("Proyecto.YaCerrado", "Este proyecto ya está cerrado."));

        var requisito = (await requisitos.ListarDeProyectoAsync(proyecto.Id, cancellationToken))
            .FirstOrDefault(r => r.TipoDocumentoId == request.TipoDocumentoId);
        if (requisito is null) return Result.Exito();

        requisitos.Eliminar(requisito);
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Otra pestaña lo quitó antes: el estado pedido ya es el de la base.
        }
        return Result.Exito();
    }
}

public class GuardarPlantillaRequisitosEventoCommandHandler(
    IProyectoRepository proyectos, IProyectoRequisitoRepository requisitos, IPlantillaRequisitosEventoRepository plantillas,
    IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
    : IRequestHandler<GuardarPlantillaRequisitosEventoCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(GuardarPlantillaRequisitosEventoCommand request, CancellationToken cancellationToken)
    {
        var (proyecto, error) = await EventoVisible.ObtenerAsync(proyectos, alcanceDatos, request.ProyectoId, cancellationToken);
        if (proyecto is null) return Result.Fallo<Guid>(error!);

        var tipos = (await requisitos.ListarDeProyectoAsync(proyecto.Id, cancellationToken)).Select(r => r.TipoDocumentoId).ToList();
        if (tipos.Count == 0)
            return Result.Fallo<Guid>(Error.Crear("Proyecto.PlantillaSinRequisitos", "El evento no tiene requisitos que guardar."));

        if (await plantillas.ExisteNombreParaClienteAsync(proyecto.ClienteId, request.Nombre, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("Proyecto.PlantillaNombreDuplicado", "Ya existe una plantilla con este nombre para el Cliente del evento."));

        var plantilla = PlantillaRequisitosEvento.Crear(proyecto.ClienteId, request.Nombre, tipos);
        plantillas.Agregar(plantilla);
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return Result.Fallo<Guid>(Error.Crear("Proyecto.PlantillaNombreDuplicado", "Ya existe una plantilla con este nombre para el Cliente del evento."));
        }

        return Result.Exito(plantilla.Id);
    }
}
