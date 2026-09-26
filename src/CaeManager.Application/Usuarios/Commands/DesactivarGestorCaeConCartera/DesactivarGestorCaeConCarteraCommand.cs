using CaeManager.Application.Clientes;
using CaeManager.Application.Clientes.Commands.ReasignarEjecutivoCliente;
using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Usuarios.Commands.DesactivarGestorCaeConCartera;

/// <summary>
/// Desactiva a un Gestor CAE pasando antes su cartera entera a otro Gestor CAE, <b>en una
/// sola transacción</b> (FS-25, revisión Codex de la PR #931, hallazgos 3 y 4). Antes la
/// pantalla de Usuarios enviaba un <see cref="ReasignarEjecutivoClienteCommand"/> por
/// Cliente empresarial y después desactivaba: sin atomicidad entre medias, y con la
/// cartera leída al abrir el diálogo, de modo que un Cliente empresarial asignado
/// mientras tanto se quedaba sin mover y la cuenta se desactivaba igual.
///
/// <para>
/// <see cref="ClienteIdsConfirmados"/> es lo que el Administrador vio y confirmó. Dentro
/// de la transacción se vuelve a leer la cartera: si no coincide, no se escribe nada y se
/// devuelve <see cref="CarteraCambiada"/> para que la pantalla vuelva a preguntar. Tras
/// pasarla y desactivar, se lee una última vez: un Cliente empresarial que llegara
/// durante la transacción también la deshace entera.
/// </para>
///
/// <para>
/// Autoriza quien gestiona cuentas (Administrador, Dirección CAE: ver
/// <see cref="AutoridadSobreCuentas"/>), que también puede reasignar cualquier Cliente
/// empresarial; el destino lo valida <see cref="ReasignadorCarteraCliente"/> con las
/// mismas reglas que una reasignación suelta. Sin destino no se usa este Command: la
/// cuenta se desactiva con <c>CambiarActivacionUsuarioCommand</c> y su Coordinador CAE
/// hereda la cartera (decisión C del 2026-09-24).
/// </para>
/// </summary>
public record DesactivarGestorCaeConCarteraCommand(
    Guid UsuarioId, Guid DestinoUsuarioId, IReadOnlyCollection<Guid> ClienteIdsConfirmados) : ICommand;

public class DesactivarGestorCaeConCarteraCommandHandler(
    IGestionCuentasUsuario cuentas,
    ICurrentUserService currentUserService,
    IDirectorioDestinosCartera directorio,
    ReasignadorCarteraCliente reasignador,
    IUnitOfWork unitOfWork,
    ITransaccionDeComando transaccion)
    : IRequestHandler<DesactivarGestorCaeConCarteraCommand, Result>
{
    public static readonly Error CarteraCambiada = Error.Crear(
        "Usuarios.CarteraCambiada",
        "La cartera de este Gestor CAE cambió mientras decidías. Revísala y vuelve a confirmar: no se ha pasado nada ni se ha desactivado la cuenta.");

    public static readonly Error DestinoEsElMismo = Error.Crear(
        "Usuarios.DestinoEsElMismo", "La cartera no puede pasar al mismo Gestor CAE que se desactiva.");

    public async Task<Result> Handle(DesactivarGestorCaeConCarteraCommand request, CancellationToken cancellationToken)
    {
        if (!await AutoridadSobreCuentas.PuedeGestionarCuentasAsync(currentUserService))
            return Result.Fallo(AutoridadSobreCuentas.SinAutoridad);

        if (await currentUserService.ObtenerUsuarioActualIdAsync() == request.UsuarioId)
            return Result.Fallo(Error.Crear("Usuarios.PropiaCuenta", "No puedes desactivar tu propia cuenta."));

        if (request.DestinoUsuarioId == request.UsuarioId)
            return Result.Fallo(DestinoEsElMismo);

        var cuenta = await cuentas.ObtenerAsync(request.UsuarioId, cancellationToken);
        if (cuenta is null)
            return Result.Fallo(AutoridadSobreCuentas.CuentaInexistente);
        if (!cuenta.EsPropiaDelTenantActual)
            return Result.Fallo(AutoridadSobreCuentas.NoEncontrado);

        var confirmados = request.ClienteIdsConfirmados.ToHashSet();

        try
        {
            return await transaccion.EjecutarAsync(async ct =>
            {
                var enCartera = await directorio.ObtenerClientesEnCarteraAsync(request.UsuarioId, ct);
                if (!confirmados.SetEquals(enCartera))
                    return Result.Fallo(CarteraCambiada);

                foreach (var clienteId in confirmados)
                {
                    var reasignado = await reasignador.ReasignarAsync(clienteId, request.DestinoUsuarioId, ct);
                    if (reasignado.EsFallido)
                        return Result.Fallo(reasignado.Error);
                }

                await unitOfWork.SaveChangesAsync(ct);

                var desactivada = await cuentas.CambiarActivacionAsync(request.UsuarioId, activar: false, ct);
                if (desactivada.EsFallido)
                    return Result.Fallo(Error.Crear(
                        desactivada.Error.Codigo, $"No pudimos desactivar esta cuenta. {desactivada.Error.Mensaje}"));

                // Un Cliente empresarial asignado a esta cuenta por otro circuito mientras
                // corría la transacción la deshace entera: nada se pasa a medias.
                if ((await directorio.ObtenerClientesEnCarteraAsync(request.UsuarioId, ct)).Count > 0)
                    return Result.Fallo(CarteraCambiada);

                return Result.Exito();
            }, cancellationToken);
        }
        catch (DbUpdateException)
        {
            // La transacción ya se deshizo y el contexto quedó vacío (ITransaccionDeComando).
            return Result.Fallo(ReasignarEjecutivoClienteCommandHandler.ConflictoDeReasignacion);
        }
    }
}
