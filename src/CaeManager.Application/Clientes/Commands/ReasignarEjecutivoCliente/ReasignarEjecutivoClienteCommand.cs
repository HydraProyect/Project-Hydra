using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Clientes.Commands.ReasignarEjecutivoCliente;

/// <summary>
/// Reasigna el Gestor CAE dueño de un Cliente (Cliente.EjecutivoUsuarioId) —
/// solo para roles por encima de Gestor CAE (ver Roles.cs). Dispara los dos
/// avisos pedidos por el usuario: cambio de cartera a los Gestores
/// afectados, y — si el Cliente tiene algún TipoDocumento con lectura IA
/// desactivada — un aviso aparte al nuevo Gestor con enlace a la pantalla
/// de configuración, porque la configuración de IA se conserva tal cual al
/// reasignar (ver ConfiguracionIaDocumentoCliente).
///
/// <b>CoordinadorCae queda acotado a su ámbito de supervisión</b> (decisión de
/// dominio D-001, 2026-08-24): solo puede reasignar Clientes de la cartera
/// derivada de los Gestores que le reportan — la misma jerarquía que ya
/// acotaba su lectura (<see cref="IAlcanceDatosService"/>), que hasta esta
/// decisión no se comprobaba aquí. Administrador y DireccionCae conservan
/// alcance total, coherente con su rol.
///
/// <b>El destino también se valida aquí, no solo en la pantalla</b> (revisión
/// Codex de la PR #931): tiene que ser un Gestor CAE activo alcanzable desde el
/// Tenant activo y, para un Coordinador CAE, uno de los que le reportan — ver
/// <see cref="ReglaDestinoCarteraCliente"/>. Quitar el Gestor CAE (destino
/// <c>null</c>) no necesita destino que validar. Todo eso lo aplica
/// <see cref="ReasignadorCarteraCliente"/>, que comparte con la desactivación de un
/// Gestor CAE con traspaso de cartera.
/// </summary>
public record ReasignarEjecutivoClienteCommand(Guid ClienteId, Guid? NuevoEjecutivoUsuarioId) : ICommand;

public class ReasignarEjecutivoClienteCommandHandler(
    ReasignadorCarteraCliente reasignador,
    IUnitOfWork unitOfWork,
    ICurrentUserService currentUserService,
    IDescarteCambiosPendientes descarteCambios)
    : IRequestHandler<ReasignarEjecutivoClienteCommand, Result>
{
    // Application no puede referenciar Infrastructure.Identity.Roles — mismo motivo que en AutorizacionEscrituraBehavior.
    private static readonly string[] RolesPermitidos = ["Administrador", "DireccionCae", "CoordinadorCae"];

    public async Task<Result> Handle(ReasignarEjecutivoClienteCommand request, CancellationToken cancellationToken)
    {
        var rol = await currentUserService.ObtenerRolEfectivoAsync();
        if (rol is null || !RolesPermitidos.Contains(rol))
            return Result.Fallo(Error.Crear("Cliente.SinPermisoReasignar", "Tu rol no puede reasignar la cartera de un cliente."));

        var reasignado = await reasignador.ReasignarAsync(request.ClienteId, request.NuevoEjecutivoUsuarioId, cancellationToken);
        if (reasignado.EsFallido)
            return Result.Fallo(reasignado.Error);
        if (!reasignado.Valor)
            return Result.Exito();

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // El DbContext del circuito conserva la Empresa modificada, las
            // notificaciones y las carteras añadidas: sin descartarlas, el
            // siguiente Command del mismo circuito las guardaría por su cuenta.
            descarteCambios.DescartarCambiosPendientes();

            // El índice único global de responsable vigente (auditoría Módulo 5,
            // hallazgo crítico 3/9) puede chocar si otra reasignación concurrente
            // sobre este mismo cliente terminó primero — la traducción evita un
            // error de base de datos sin explicación en pantalla.
            return Result.Fallo(ConflictoDeReasignacion);
        }

        return Result.Exito();
    }

    public static readonly Error ConflictoDeReasignacion = Error.Crear(
        "Cliente.ConflictoDeReasignacion",
        "Otro cambio sobre la cartera de este cliente se completó primero. Vuelve a intentarlo.");
}
