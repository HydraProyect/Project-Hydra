using CaeManager.Application.Clientes;
using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using CaeManager.Domain.Common;
using MediatR;

namespace CaeManager.Application.Usuarios.Commands.CambiarActivacionUsuario;

/// <summary>
/// Desactiva o reactiva una cuenta del Tenant activo (P1-I2: antes lo hacía
/// <c>Usuarios.razor.cs</c> contra <c>UserManager</c>). Desactivar bloquea la
/// cuenta y renueva su sello de seguridad en la misma escritura, para que la
/// cookie y el circuito ya abiertos dejen de valer.
///
/// <para>
/// Nadie cambia la activación de su propia cuenta. Una cuenta que ya no existe y
/// una que existe pero es de otra organización (la fila de un Operador CAE externo
/// delegado) dan errores distintos: la primera pide recargar la lista; la
/// segunda es una fila legítima que no se puede tocar desde aquí.
/// </para>
///
/// <para>
/// Desactivar toma el candado exclusivo de cartera de la cuenta dentro de una transacción
/// (<see cref="IBloqueoCarteraUsuario"/>, revisión Codex de FS-25): una reasignación o el
/// alta de un Cliente empresarial en curso para esa cuenta termina antes, y los que lleguen
/// durante esperan y ven la cuenta ya desactivada. Sin esto, la cuenta podía quedar
/// desactivada con un Cliente empresarial recién puesto en su cartera.
/// </para>
///
/// <para>
/// <b>Relevo del principal</b> (ADR-011 § 2.7, enmienda 2026-10-08, punto 3): si la cuenta
/// desactivada llevaba la marca de principal en alguna Asignación de Operación externa de su
/// Operador CAE, en la misma transacción la marca pasa a su Coordinador CAE
/// (<see cref="RelevoDePrincipalDeCartera"/>). Su cartera <b>sigue viva</b> (opción C,
/// 2026-09-24): pierde la marca, no el acceso. Sin Coordinador CAE a quien relevar se escala
/// al único Coordinador CAE, Dirección CAE o Administrador del Operador CAE
/// (<see cref="EscaladoDePrincipalDeCartera"/>, punto 4); si no hay uno solo, la operación
/// queda sin principal. Reactivar la cuenta no le devuelve la marca.
/// </para>
///
/// <para>
/// <b>Primer usuario elegible</b> (mismo punto 4): si la cuenta reactivada es la única activa
/// del Operador CAE con perfil Coordinador CAE, Dirección CAE o Administrador, recibe en la
/// misma transacción la cartera principal de cada operación que siga sin principal.
/// </para>
/// </summary>
public record CambiarActivacionUsuarioCommand(Guid UsuarioId, bool Activar) : ICommand;

public class CambiarActivacionUsuarioCommandHandler(
    IGestionCuentasUsuario cuentas,
    ICurrentUserService currentUserService,
    ITenantActual tenantActual,
    ITransaccionDeComando transaccion,
    IBloqueoCarteraUsuario bloqueoCartera,
    ICatalogoIncorporacionCartera catalogo,
    IDirectorioDestinosCartera directorioDestinos,
    IDirectorioUsuariosService directorioUsuarios)
    : IRequestHandler<CambiarActivacionUsuarioCommand, Result>
{
    public static readonly Error PrincipalNoRelevado = Error.Crear(
        "Usuarios.PrincipalNoRelevado",
        "Su cartera cambió mientras tanto. No se ha cambiado nada; vuelve a intentarlo.");

    public async Task<Result> Handle(CambiarActivacionUsuarioCommand request, CancellationToken cancellationToken)
    {
        if (!await AutoridadSobreCuentas.PuedeGestionarCuentasAsync(currentUserService))
            return Result.Fallo(AutoridadSobreCuentas.SinAutoridad);

        if (await currentUserService.ObtenerUsuarioActualIdAsync() == request.UsuarioId)
            return Result.Fallo(Error.Crear("Usuarios.PropiaCuenta", "No puedes desactivar tu propia cuenta."));

        var cuenta = await cuentas.ObtenerAsync(request.UsuarioId, cancellationToken);
        if (cuenta is null)
            return Result.Fallo(AutoridadSobreCuentas.CuentaInexistente);
        if (!cuenta.EsPropiaDelTenantActual)
            return Result.Fallo(AutoridadSobreCuentas.NoEncontrado);

        // La cuenta destino tiene rol de Propiedad: solo la toca quien actúa en su propio
        // Tenant de origen (primer acto excluido del Encargo de administración, D-8).
        var destinoIntocable = CuentasConRolDePropiedad.VerificarDestino(
            cuenta.Roles, await currentUserService.ObtenerTenantOrigenIdAsync(), tenantActual.TenantId);
        if (destinoIntocable.EsFallido)
            return destinoIntocable;

        var resultado = await transaccion.EjecutarAsync(async ct =>
        {
            if (!request.Activar)
                await bloqueoCartera.BloquearExclusivoAsync(request.UsuarioId, ct);
            var cambio = await cuentas.CambiarActivacionAsync(request.UsuarioId, request.Activar, ct);
            if (cambio.EsFallido)
                return cambio;

            return request.Activar
                ? await AsignarSiEsElPrimerElegibleAsync(request.UsuarioId, ct)
                : await CederMarcaDePrincipalAsync(request.UsuarioId, ct);
        }, cancellationToken);
        if (resultado.EsExitoso) return resultado;

        return resultado.Error.Codigo == AutoridadSobreCuentas.NoEncontrado.Codigo
            ? Result.Fallo(AutoridadSobreCuentas.CuentaInexistente)
            : Result.Fallo(Error.Crear(
                resultado.Error.Codigo,
                $"No pudimos {(request.Activar ? "reactivar" : "desactivar")} esta cuenta. {resultado.Error.Mensaje}"));
    }

    /// <summary>
    /// La cuenta recién reactivada recibe las operaciones sin principal de su Operador CAE solo
    /// si es su única cuenta elegible. El Operador CAE es el Tenant de origen de quien reactiva,
    /// por lo mismo que en <see cref="CederMarcaDePrincipalAsync"/>; una cuenta de otro Tenant
    /// no aparece entre las elegibles de ese y no recibe nada.
    /// </summary>
    private async Task<Result> AsignarSiEsElPrimerElegibleAsync(Guid usuarioId, CancellationToken cancellationToken)
    {
        if (await currentUserService.ObtenerTenantOrigenIdAsync() is not { } operadorTenantId)
            return Result.Exito();

        return await EscaladoDePrincipalDeCartera.AsignarAlPrimerElegibleAsync(
                catalogo, directorioUsuarios, bloqueoCartera, usuarioId, operadorTenantId, cancellationToken)
            ? Result.Exito()
            : Result.Fallo(PrincipalNoRelevado);
    }

    /// <summary>
    /// Apaga la marca de principal de la cuenta recién desactivada en cada operación externa de
    /// su Operador CAE y la pasa a su Coordinador CAE o, si no lo hay, a quien corresponda por
    /// escalado. El Operador CAE es el Tenant de origen de
    /// quien desactiva: la cuenta es de su mismo Tenant (se comprobó arriba) y la política RLS
    /// de las carteras solo deja leer las del operador de la sesión.
    /// </summary>
    private async Task<Result> CederMarcaDePrincipalAsync(Guid usuarioId, CancellationToken cancellationToken)
    {
        if (await currentUserService.ObtenerTenantOrigenIdAsync() is not { } operadorTenantId)
            return Result.Exito();

        var principales = await catalogo.ObtenerOperacionesDondeEsPrincipalAsync(operadorTenantId, usuarioId, cancellationToken);
        if (principales.Count == 0)
            return Result.Exito();

        var relevo = await RelevoDePrincipalDeCartera.ResolverRelevoAsync(
            usuarioId, operadorTenantId, directorioDestinos, directorioUsuarios, bloqueoCartera, cancellationToken);

        return await RelevoDePrincipalDeCartera.ApagarYRelevarAsync(
                catalogo, principales, operadorTenantId, usuarioId, relevo, cancellationToken)
            ? Result.Exito()
            : Result.Fallo(PrincipalNoRelevado);
    }
}
