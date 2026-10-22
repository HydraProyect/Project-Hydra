using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.TiposDocumento;
using CaeManager.Domain.Common;
using CaeManager.Domain.Proyectos;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Proyectos.Commands.CrearProyecto;

/// <param name="Tipo">Obra (el Proyecto de siempre) o Evento; un evento exige fecha de fin.</param>
/// <param name="CopiarRequisitosDeProyectoId">Solo para un Evento: copia los requisitos de ese otro Proyecto.</param>
/// <param name="PlantillaRequisitosId">Solo para un Evento: copia los requisitos de esa plantilla.</param>
public record CrearProyectoCommand(
    Guid ClienteId, Guid CentroId, string Nombre, DateOnly FechaInicio, DateOnly? FechaFinPrevista, string? Notas,
    TipoProyecto Tipo = TipoProyecto.Obra, Guid? CopiarRequisitosDeProyectoId = null, Guid? PlantillaRequisitosId = null)
    : ICommand<Guid>;

public class CrearProyectoCommandValidator : AbstractValidator<CrearProyectoCommand>
{
    public CrearProyectoCommandValidator()
    {
        RuleFor(c => c.ClienteId).NotEmpty().WithMessage("Selecciona un Cliente.");
        RuleFor(c => c.CentroId).NotEmpty().WithMessage("Selecciona un centro.");
        RuleFor(c => c.Nombre).NotEmpty().MaximumLength(Proyecto.LongitudMaximaNombre);
        RuleFor(c => c.Notas).MaximumLength(Proyecto.LongitudMaximaNotas);
        RuleFor(c => c.Tipo).IsInEnum();
        RuleFor(c => c.FechaFinPrevista).NotNull().When(c => c.Tipo == TipoProyecto.Evento)
            .WithMessage("Un evento necesita fecha de fin.");
        RuleFor(c => c).Must(c => c.CopiarRequisitosDeProyectoId is null || c.PlantillaRequisitosId is null)
            .WithMessage("Copia los requisitos de otro evento o de una plantilla, no de los dos.");
        RuleFor(c => c).Must(c => c.Tipo == TipoProyecto.Evento || (c.CopiarRequisitosDeProyectoId is null && c.PlantillaRequisitosId is null))
            .WithMessage("Solo un evento copia requisitos.");
    }
}

public class CrearProyectoCommandHandler(
    IProyectoRepository repositorio, ICentrosQueryContext dbContext,
    IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork,
    IProyectosQueryContext proyectosContext, IRequisitosEventoQueryContext requisitosContext, IProyectoRequisitoRepository requisitos,
    ITiposDocumentoQueryContext tiposDocumentoContext)
    : IRequestHandler<CrearProyectoCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CrearProyectoCommand request, CancellationToken cancellationToken)
    {
        var centro = await dbContext.Centros
            .Where(c => c.Id == request.CentroId)
            .Select(c => new { c.ClienteId })
            .FirstOrDefaultAsync(cancellationToken);

        if (centro is null)
            return Result.Fallo<Guid>(Error.Crear("Proyecto.CentroNoEncontrado", "No encontramos este centro."));

        if (centro.ClienteId != request.ClienteId)
            return Result.Fallo<Guid>(Error.Crear("Proyecto.CentroNoPerteneceACliente", "Este centro no pertenece al Cliente seleccionado."));

        // Autoridad sobre el cliente, no solo existencia (auditoría Módulo 5,
        // hallazgo crítico 5/9) — mismo criterio que usan ya las escrituras
        // de Proyecto existentes (ver ProyectoAutorizacion).
        if (!await ProyectoAutorizacion.VisibleAsync(request.ClienteId, alcanceDatos, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("Proyecto.CentroNoEncontrado", "No encontramos este centro."));

        if (await repositorio.ExisteNombreParaClienteAsync(request.ClienteId, request.Nombre, cancellationToken: cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("Proyecto.NombreDuplicado", "Ya existe un proyecto con este nombre para el Cliente seleccionado."));

        if (request.Tipo == TipoProyecto.Evento && request.FechaFinPrevista is null)
            return Result.Fallo<Guid>(Error.Crear("Proyecto.EventoSinFin", "Un evento necesita fecha de fin."));

        // El origen de los requisitos se resuelve con la misma autoridad que el Proyecto: un evento o una
        // plantilla de un Cliente empresarial que el actor no ve no existe para él (sin revelar cuál falla).
        var tipoDocumentoIds = await RequisitosDeOrigenAsync(request, cancellationToken);
        if (tipoDocumentoIds is null)
            return Result.Fallo<Guid>(Error.Crear("Proyecto.OrigenRequisitosNoEncontrado", "No encontramos el evento o la plantilla de origen."));

        tipoDocumentoIds = await VigentesAsync(tipoDocumentoIds, cancellationToken);

        var proyecto = Proyecto.Crear(request.ClienteId, request.CentroId, request.Nombre, request.FechaInicio, request.FechaFinPrevista, request.Notas, request.Tipo);
        repositorio.Agregar(proyecto);
        foreach (var tipoDocumentoId in tipoDocumentoIds)
            requisitos.Agregar(new ProyectoRequisito(proyecto.Id, tipoDocumentoId));

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(proyecto.Id);
    }

    /// <summary>
    /// Lo que se copia se pide hoy: un tipo archivado (catálogo del 2026-10-17) se sustituye por su sustituto, que es
    /// del mismo Tenant propietario, y uno archivado sin sustituto no se copia (ya no se puede pedir ni dar de alta).
    /// </summary>
    private async Task<IReadOnlyList<Guid>> VigentesAsync(IReadOnlyList<Guid> tipoDocumentoIds, CancellationToken cancellationToken)
    {
        if (tipoDocumentoIds.Count == 0)
            return tipoDocumentoIds;

        var tipos = await tiposDocumentoContext.TiposDocumento
            .Where(t => tipoDocumentoIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Archivado, t.SustitutoId })
            .ToListAsync(cancellationToken);

        return tipos
            .Select(t => t.Archivado ? t.SustitutoId : t.Id)
            .OfType<Guid>()
            .Distinct()
            .ToList();
    }

    /// <returns>Los Tipos de Documento a copiar (vacío si no hay origen), o null si el origen no es visible.</returns>
    private async Task<IReadOnlyList<Guid>?> RequisitosDeOrigenAsync(CrearProyectoCommand request, CancellationToken cancellationToken)
    {
        if (request.CopiarRequisitosDeProyectoId is { } origenId)
        {
            var origen = await proyectosContext.Proyectos
                .Where(p => p.Id == origenId && p.Tipo == TipoProyecto.Evento).Select(p => new { p.ClienteId }).FirstOrDefaultAsync(cancellationToken);
            if (origen is null || !await ProyectoAutorizacion.VisibleAsync(origen.ClienteId, alcanceDatos, cancellationToken))
                return null;

            return await requisitosContext.ProyectosRequisitos
                .Where(r => r.ProyectoId == origenId).Select(r => r.TipoDocumentoId).Distinct().ToListAsync(cancellationToken);
        }

        if (request.PlantillaRequisitosId is { } plantillaId)
        {
            var plantilla = await requisitosContext.PlantillasRequisitosEvento
                .Where(p => p.Id == plantillaId).Select(p => new { p.ClienteId }).FirstOrDefaultAsync(cancellationToken);
            if (plantilla is null || !await ProyectoAutorizacion.VisibleAsync(plantilla.ClienteId, alcanceDatos, cancellationToken))
                return null;

            return await requisitosContext.PlantillasRequisitosEventoTipos
                .Where(t => t.PlantillaId == plantillaId).Select(t => t.TipoDocumentoId).Distinct().ToListAsync(cancellationToken);
        }

        return [];
    }
}
