using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using MediatR;

namespace CaeManager.Application.Usuarios.Queries.ObtenerCuentaUsuario;

/// <summary>
/// La cuenta que /usuarios abre para editar (P1-I2: antes la página la leía con
/// <c>UserManager.FindByIdAsync</c>, que no filtra por Tenant). Misma autoridad que
/// los Commands que la editan (<see cref="AutoridadSobreCuentas"/>).
///
/// <para>
/// Propiedad, no visibilidad: la fila de un Operador CAE externo delegado aparece
/// en la lista, pero abrir su ficha desde aquí sería editar la cuenta de otra
/// organización. Para ella el error es <c>Usuarios.NoEncontrado</c>; para una
/// cuenta que ya no existe, <c>Usuarios.CuentaInexistente</c>, que pide recargar.
/// </para>
/// </summary>
public record ObtenerCuentaUsuarioQuery(Guid UsuarioId) : IRequest<Result<CuentaUsuario>>;

public class ObtenerCuentaUsuarioQueryHandler(
    IGestionCuentasUsuario cuentas,
    ICurrentUserService currentUserService)
    : IRequestHandler<ObtenerCuentaUsuarioQuery, Result<CuentaUsuario>>
{
    public async Task<Result<CuentaUsuario>> Handle(ObtenerCuentaUsuarioQuery request, CancellationToken cancellationToken)
    {
        if (!await AutoridadSobreCuentas.PuedeGestionarCuentasAsync(currentUserService))
            return Result.Fallo<CuentaUsuario>(AutoridadSobreCuentas.SinAutoridad);

        var cuenta = await cuentas.ObtenerAsync(request.UsuarioId, cancellationToken);
        if (cuenta is null)
            return Result.Fallo<CuentaUsuario>(AutoridadSobreCuentas.CuentaInexistente);

        return cuenta.EsPropiaDelTenantActual
            ? Result.Exito(cuenta)
            : Result.Fallo<CuentaUsuario>(AutoridadSobreCuentas.NoEncontrado);
    }
}
