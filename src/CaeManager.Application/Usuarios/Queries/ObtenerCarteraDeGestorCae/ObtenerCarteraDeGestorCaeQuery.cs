using CaeManager.Application.Clientes;
using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using CaeManager.Application.Usuarios.Commands.AsignarCarteraGestorCae;
using MediatR;

namespace CaeManager.Application.Usuarios.Queries.ObtenerCarteraDeGestorCae;

/// <summary>
/// Un Tenant beneficiario para el diálogo «Asignar empresas» de un Gestor CAE. Solo el nombre.
/// <paramref name="EnCartera"/>: el Gestor CAE ya lo tiene entero. <paramref name="Asignable"/>: el
/// Operador CAE puede asignarlo hoy (una cartera cuya operación caducó sale con
/// <c>EnCartera</c> y sin <c>Asignable</c>: solo se puede retirar).
/// </summary>
public record EmpresaDeCarteraDeGestor(Guid TenantId, string Nombre, bool EnCartera, bool Asignable);

/// <summary>
/// Las empresas del diálogo «Asignar empresas»: las que el Operador CAE puede asignar más las
/// que el Gestor CAE ya tiene enteras. Vacía si quien pregunta no tiene autoridad sobre esa
/// cartera (<see cref="AutoridadSobreCarteraDeGestorCae"/>). Es UX, no enforcement:
/// <c>AsignarCarteraGestorCaeCommand</c> vuelve a comprobarlo todo al guardar.
/// </summary>
public record ObtenerCarteraDeGestorCaeQuery(Guid GestorUsuarioId) : IRequest<IReadOnlyList<EmpresaDeCarteraDeGestor>>;

public class ObtenerCarteraDeGestorCaeQueryHandler(
    ICurrentUserService currentUserService,
    IDirectorioUsuariosService directorioUsuarios,
    IDirectorioDestinosCartera directorioDestinos,
    ICatalogoIncorporacionCartera catalogo)
    : IRequestHandler<ObtenerCarteraDeGestorCaeQuery, IReadOnlyList<EmpresaDeCarteraDeGestor>>
{
    public async Task<IReadOnlyList<EmpresaDeCarteraDeGestor>> Handle(
        ObtenerCarteraDeGestorCaeQuery request, CancellationToken cancellationToken)
    {
        var contexto = await AutoridadSobreCarteraDeGestorCae.ResolverAsync(
            request.GestorUsuarioId, currentUserService, directorioUsuarios, directorioDestinos, cancellationToken);
        if (contexto.EsFallido)
            return [];
        var ctx = contexto.Valor;

        var asignables = await catalogo.ObtenerAsignablesAsync(ctx.OperadorTenantId, cancellationToken);
        var enCartera = await catalogo.ObtenerCarteraUniversalAsync(ctx.OperadorTenantId, ctx.GestorUsuarioId, cancellationToken);
        var enCarteraIds = enCartera.Select(e => e.PropietarioTenantId).ToHashSet();
        var asignablesIds = asignables.Select(a => a.PropietarioTenantId).ToHashSet();

        return asignables
            .Select(a => new EmpresaDeCarteraDeGestor(a.PropietarioTenantId, a.Nombre, enCarteraIds.Contains(a.PropietarioTenantId), true))
            .Concat(enCartera
                .Where(e => !asignablesIds.Contains(e.PropietarioTenantId))
                .Select(e => new EmpresaDeCarteraDeGestor(e.PropietarioTenantId, e.Nombre, true, false)))
            .OrderBy(e => e.Nombre)
            .ToList();
    }
}
