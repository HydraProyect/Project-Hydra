using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Configuracion.Commands.GuardarVistaRecordada;

/// <summary>
/// Recuerda la vista de un listado —filtros y agrupación— para el Usuario actual
/// en el Tenant actual (decisión del 2026-10-08): la próxima vez que entre en esa
/// pantalla sin parámetros en la URL, la encuentra como la dejó. Sustituye a la
/// que hubiera: hay una por Tenant, Usuario y pantalla, y vive en la fila
/// reservada de <see cref="FiltroGuardado"/>.
///
/// <para>
/// <see cref="ValoresJson"/> es opaco, como en los filtros con nombre: lo escribe
/// y lo entiende la página. Lleva identificadores del Tenant en el que se guardó,
/// y por eso no se recuerda sin Tenant activo ni se lee desde otro.
/// </para>
///
/// <para>
/// Dos pestañas del mismo Usuario pueden guardar a la vez: gana la última. El
/// choque contra el índice único de la que llega segunda no sube como excepción;
/// se reintenta una vez como sustitución.
/// </para>
/// </summary>
public record GuardarVistaRecordadaCommand(string Pantalla, string ValoresJson) : ICommand, IComandoDeAutoservicio;

public class GuardarVistaRecordadaCommandValidator : AbstractValidator<GuardarVistaRecordadaCommand>
{
    public GuardarVistaRecordadaCommandValidator()
    {
        RuleFor(c => c.Pantalla).Must(p => PantallasConVistaRecordada.Admitidas.Contains(p))
            .WithMessage("Esa pantalla no recuerda la vista.");
        RuleFor(c => c.ValoresJson).NotEmpty().WithMessage("No hay ninguna vista que recordar.")
            .MaximumLength(PantallasConVistaRecordada.LongitudMaximaValoresJson)
            .WithMessage("Esa vista es demasiado grande para recordarla.");
    }
}

public class GuardarVistaRecordadaCommandHandler(
    ICurrentUserService currentUserService, ITenantActual tenantActual,
    IFiltroGuardadoRepository repositorio, IUnitOfWork unitOfWork,
    IDescarteCambiosPendientes descarteCambios, ILogger<GuardarVistaRecordadaCommandHandler> logger)
    : IRequestHandler<GuardarVistaRecordadaCommand, Result>
{
    private const int IntentosMaximos = 2;

    public async Task<Result> Handle(GuardarVistaRecordadaCommand request, CancellationToken cancellationToken)
    {
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo(Error.Crear("VistaRecordada.SinUsuario", "No pudimos identificarte. Vuelve a iniciar sesión."));

        // Sin Tenant activo no se recuerda nada: la vista lleva identificadores de un Tenant
        // y la fila se sella con él. Fallo legible aquí, antes de que lo rechace el sellado.
        if (tenantActual.TenantId is not { } tenantId || tenantId == Guid.Empty)
            return Result.Fallo(Error.Crear(
                "VistaRecordada.SinTenant",
                "No pudimos determinar en qué organización estás trabajando. Vuelve a iniciar sesión."));

        for (var intento = 1; ; intento++)
        {
            var vista = await repositorio.ObtenerVistaRecordadaAsync(usuarioId.Value, request.Pantalla, cancellationToken);
            if (vista is null)
                repositorio.Agregar(FiltroGuardado.CrearVistaRecordada(usuarioId.Value, request.Pantalla, request.ValoresJson));
            else
                vista.RecordarVista(request.ValoresJson);

            try
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
                return Result.Exito();
            }
            catch (DbUpdateException excepcion)
            {
                // Otra pestaña del mismo Usuario se adelantó: insertó la fila (y aquí choca el
                // índice único) o la borró (y la sustitución no encuentra a quién). La vuelta
                // siguiente la lee como ha quedado y decide de nuevo. Lo que el guardado fallido
                // dejó rastreado se suelta, para que no lo guarde el siguiente comando del circuito.
                descarteCambios.DescartarCambiosPendientes();

                if (intento < IntentosMaximos) continue;

                // Dos fallos seguidos ya no son esa carrera. Se deja escrito y se responde con un
                // fallo legible: recordar la vista es una comodidad, no rompe la pantalla.
                logger.LogWarning(excepcion,
                    "No se pudo recordar la vista de {Pantalla} tras {Intentos} intentos.", request.Pantalla, IntentosMaximos);
                return Result.Fallo(Error.Crear(
                    "VistaRecordada.NoGuardada", "No pudimos recordar esta vista. Tus filtros siguen aplicados."));
            }
        }
    }
}
