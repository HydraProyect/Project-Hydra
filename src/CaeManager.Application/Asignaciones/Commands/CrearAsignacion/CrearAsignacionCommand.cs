using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Acreditacion;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Common;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Asignaciones.Commands.CrearAsignacion;

public record CrearAsignacionCommand(Guid TrabajadorId, Guid CentroId, DateOnly FechaAlta) : ICommand<Guid>;

public class CrearAsignacionCommandValidator : AbstractValidator<CrearAsignacionCommand>
{
    public CrearAsignacionCommandValidator()
    {
        RuleFor(c => c.TrabajadorId).NotEmpty().WithMessage("Selecciona un trabajador.");
        RuleFor(c => c.CentroId).NotEmpty().WithMessage("Selecciona un centro.");
    }
}

public class CrearAsignacionCommandHandler(
    IAsignacionRepository repositorio, IAutoridadAsignacionesService autoridad,
    IAltaAcreditacionesPlataformaService altaAcreditaciones, IUnitOfWork unitOfWork,
    ISerializacionAltasAcreditacionEmpresa serializacionAltas,
    // Opcional solo mientras la PR #2104 cambia los tests que construyen este handler: el contenedor siempre lo resuelve
    // (InvalidadorAlcanceInerte por defecto, el AlcanceDatosService del circuito en la Web). Issue #2118.
    IInvalidadorAlcance? invalidadorAlcance = null)
    : IRequestHandler<CrearAsignacionCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CrearAsignacionCommand request, CancellationToken cancellationToken)
    {
        if (await ComprobarAutoridadAsync(request, cancellationToken) is { } rechazo)
            return Result.Fallo<Guid>(rechazo);

        var asignacion = new Asignacion(request.TrabajadorId, request.CentroId, request.FechaAlta);

        // Estar asignado es estar de alta en la plataforma del Centro: los
        // Documentos del Trabajador y de su Empresa que ese Centro exige nacen
        // pendientes de acreditar ante cada acceso de plataforma del Centro.
        // Mismo SaveChangesAsync que la Asignación. Se serializa con el alta de
        // un Documento de Empresa (ver ISerializacionAltasAcreditacionEmpresa):
        // sin ello, el Documento no ve la Asignación, la Asignación no ve el
        // Documento y el par del Centro de otra Empresa se pierde (revisión
        // Codex de la PR #2074, ronda 3, hallazgo 4). La Asignación se agrega
        // dentro: un reintento empieza con el contexto vacío. Las comprobaciones
        // de existencia y de solape también van dentro, con consultas a la base:
        // otra alta o reapertura del mismo trío confirmada mientras esta
        // esperaba se ve (revisión Codex de la PR #2074, ronda 4).
        var guardado = await serializacionAltas.EjecutarAsync(async ct =>
        {
            // La autoridad (Asignación de Cartera) también se vuelve a decidir: pudo revocarse mientras se esperaba, y
            // el alcance memoizado (en Blazor Server, el del circuito) es de antes; se descarta, igual que en
            // BloqueoRespuestaSugerida.ComprobarEnvioPorBuzonAsync (issue #2118).
            invalidadorAlcance?.Invalidar();
            if (await ComprobarAutoridadAsync(request, ct) is { } rechazoBajoCandado)
                return Result.Fallo(rechazoBajoCandado);

            if (await repositorio.ExisteActivaAsync(request.TrabajadorId, request.CentroId, ct))
                return Result.Fallo(Error.Crear(
                    "Asignacion.YaActiva", "Este trabajador ya está dado de alta en este centro."));

            // DEC-19: dos vigencias solapadas del mismo trío son una
            // contradicción de datos, no solo dos filas simultáneamente
            // abiertas. Distinto de "YaActiva" (arriba): esto también atrapa un
            // alta nueva cuyo rango pisa el de una asignación YA CERRADA.
            if (await repositorio.ExisteSolapeAsync(request.TrabajadorId, request.CentroId, request.FechaAlta, null, ct))
                return Result.Fallo(Error.Crear(
                    "Asignacion.SolapaConOtra",
                    "Este trabajador ya tuvo una asignación a este centro cuyo periodo se solapa con esta fecha de alta."));

            repositorio.Agregar(asignacion);
            await altaAcreditaciones.AgregarPendientesAsync(new AltasConAcreditacion { Asignaciones = [asignacion] }, ct);
            await unitOfWork.SaveChangesAsync(ct);
            return Result.Exito();
        }, cancellationToken);

        return guardado.EsExitoso ? Result.Exito(asignacion.Id) : Result.Fallo<Guid>(guardado.Error);
    }

    private async Task<Error?> ComprobarAutoridadAsync(CrearAsignacionCommand request, CancellationToken cancellationToken)
    {
        // Autoridad sobre el trabajador, no solo existencia (auditoría Módulo
        // 5, hallazgo crítico 6/9): antes bastaba con que el trabajador
        // existiera en el tenant, así que un trabajador fuera de la cartera
        // del actor quedaba "secuestrado" hacia ella en cuanto se le
        // asignaba, exponiendo DNI, contacto y documentación médica. Mismo
        // error que "no encontrado" a propósito — ver PuedeModificarAsignacionesDelCentroAsync.
        if (!await autoridad.PuedeModificarAsignacionesDelTrabajadorAsync(request.TrabajadorId, cancellationToken))
            return Error.Crear("Asignacion.TrabajadorNoEncontrado", "No encontramos este trabajador.");

        // Autoridad sobre el centro, no solo existencia (decision del
        // propietario, 2026-08-29): un gestor no asigna a un centro fuera de su
        // arbol. Antes bastaba con que el centro existiera en el tenant, asi
        // que cualquier gestor podia dar de alta a un trabajador en el centro
        // de otro con solo conocer su Id.
        //
        // Mismo error que "no encontrado" a proposito: distinguir <<no existe>>
        // de <<no es tuyo>> confirma la existencia de un centro ajeno.
        if (!await autoridad.PuedeModificarAsignacionesDelCentroAsync(request.CentroId, cancellationToken))
            return Error.Crear("Asignacion.CentroNoEncontrado", "No encontramos este centro.");

        return null;
    }
}
