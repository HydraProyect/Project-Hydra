using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using MediatR;

namespace CaeManager.Application.Usuarios.Queries.ObtenerEmpresasAsignablesEnAlta;

/// <summary>Un Tenant beneficiario que el alta de un Gestor CAE puede poner en su cartera. Solo el nombre.</summary>
public record EmpresaAsignableEnAlta(Guid TenantId, string Nombre);

/// <summary>
/// Los Tenants beneficiarios que el formulario de alta de /usuarios ofrece para la
/// cartera de un Gestor CAE: los asignables del Operador CAE de quien da el alta
/// (<see cref="ICatalogoIncorporacionCartera.ObtenerAsignablesAsync"/>, el mismo
/// predicado que la solicitud de incorporación a cartera).
///
/// <para>
/// Vacía si quien pregunta no gestiona cuentas o si el Context Workspace activo no es
/// su Tenant de origen: son los dos casos en que <c>CrearUsuarioCommand</c> rechazaría
/// la cartera. Es UX, no enforcement: el Command vuelve a comprobarlo todo al guardar.
/// </para>
/// </summary>
public record ObtenerEmpresasAsignablesEnAltaQuery : IRequest<IReadOnlyList<EmpresaAsignableEnAlta>>;

public class ObtenerEmpresasAsignablesEnAltaQueryHandler(
    ICurrentUserService currentUserService,
    ITenantActual tenantActual,
    ICatalogoIncorporacionCartera catalogoCartera)
    : IRequestHandler<ObtenerEmpresasAsignablesEnAltaQuery, IReadOnlyList<EmpresaAsignableEnAlta>>
{
    public async Task<IReadOnlyList<EmpresaAsignableEnAlta>> Handle(
        ObtenerEmpresasAsignablesEnAltaQuery request, CancellationToken cancellationToken)
    {
        if (!await AutoridadSobreCuentas.PuedeGestionarCuentasAsync(currentUserService))
            return [];

        if (await currentUserService.ObtenerTenantOrigenIdAsync() is not { } operadorTenantId
            || tenantActual.TenantId != operadorTenantId)
            return [];

        return (await catalogoCartera.ObtenerAsignablesAsync(operadorTenantId, cancellationToken))
            .Select(a => new EmpresaAsignableEnAlta(a.PropietarioTenantId, a.Nombre))
            .ToList();
    }
}
