using CaeManager.Application.Asignaciones;
using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones;
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
/// un ítem de <see cref="SugerenciaGestionCorreo"/> (o un alta manual sin
/// correo de por medio, <paramref name="DetalleSugerenciaGestionCorreoId"/>
/// es opcional): nunca se llama automáticamente desde la detección IA. Un
/// correo puede sugerir varios ítems a la vez (ronda de reducción de ruido
/// en Comunicaciones) — este Command resuelve uno solo, el que el Gestor
/// confirmó desde la Bandeja; el resto de ítems del mismo correo siguen
/// pendientes hasta que se confirmen o descarten por separado.
/// </summary>
public record CrearGestionesParaTrabajadorCommand(
    Guid TrabajadorId, Guid TipoDocumentoId, Guid? DetalleSugerenciaGestionCorreoId = null, Guid? MensajeOrigenId = null)
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
    IDetalleSugerenciaGestionCorreoRepository detalleSugerenciaRepositorio,
    IComunicacionesQueryContext comunicacionesContext,
    IAlcanceDatosService alcanceDatos,
    ICurrentUserService currentUserService,
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

        // Asignación de Cartera: que el Trabajador sea visible no basta, porque puede estar asignado
        // también a Centros de otro Cliente empresarial. Solo nace una Gestión en los Centros del
        // alcance de gestión (null = sin restricción); fuera de ella, el Centro no cuenta.
        if (await alcanceDatos.ObtenerCentroIdsParaGestionAsync(cancellationToken) is { } centrosDeGestion)
            centroIds = centroIds.Where(centrosDeGestion.Contains).ToList();

        if (centroIds.Count == 0)
        {
            return Result.Fallo<ResultadoCrearGestionesDto>(Error.Crear(
                "Gestion.SinCentros", "Este trabajador no tiene ninguna asignación activa a un centro."));
        }

        // El mensaje de origen solo se enlaza si su Conversación está al alcance de quien mira: una
        // Gestión de esta cartera no puede quedar atada a un hilo de otra. Fuera de alcance, o si no
        // existe, la Gestión nace sin origen.
        var mensajeOrigenId = request.MensajeOrigenId is { } mensajeId
            && await alcanceDatos.MensajeVisibleAsync(comunicacionesContext, mensajeId, cancellationToken)
                ? mensajeId
                : (Guid?)null;

        foreach (var centroId in centroIds)
        {
            gestionRepositorio.Agregar(new Gestion(
                request.TrabajadorId, centroId, request.TipoDocumentoId, mensajeOrigenId));
        }

        // Asignación de Cartera del ítem sugerido: solo se marca resuelto si quien mira alcanza la
        // Conversación del mensaje que lo originó, el mismo criterio con el que
        // ObtenerConversacionPorIdQuery lo enseña. Fuera de alcance se trata igual que un Id que no
        // existe: las Gestiones del Trabajador nacen, pero el ítem de otra cartera no se toca.
        if (request.DetalleSugerenciaGestionCorreoId is { } detalleId
            && await DetalleDentroDeAlcanceAsync(detalleId, cancellationToken))
        {
            var detalle = await detalleSugerenciaRepositorio.ObtenerPorIdAsync(detalleId, cancellationToken);

            if (detalle is not null)
            {
                // Mismo criterio que en CrearVisitaCommand: si el Gestor cambió el
                // trabajador o el tipo de documento propuestos, la sugerencia no se
                // aceptó de un clic.
                var huboEdicion = detalle.TrabajadorId != request.TrabajadorId
                               || detalle.TipoDocumentoId != request.TipoDocumentoId;

                detalle.Resolver(
                    huboEdicion ? ResolucionSugerencia.ConfirmadaConEdicion : ResolucionSugerencia.Confirmada,
                    await currentUserService.ObtenerUsuarioActualIdAsync());
            }
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(new ResultadoCrearGestionesDto(centroIds.Count));
    }

    private async Task<bool> DetalleDentroDeAlcanceAsync(Guid detalleId, CancellationToken cancellationToken)
    {
        var mensajeId = await (
            from detalle in comunicacionesContext.DetallesSugerenciaGestionCorreo
            where detalle.Id == detalleId
            join sugerencia in comunicacionesContext.SugerenciasGestionCorreo on detalle.SugerenciaGestionCorreoId equals sugerencia.Id
            select (Guid?)sugerencia.MensajeId)
            .FirstOrDefaultAsync(cancellationToken);

        return mensajeId is { } id && await alcanceDatos.MensajeVisibleAsync(comunicacionesContext, id, cancellationToken);
    }
}
