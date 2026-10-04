using CaeManager.Application.Common;
using CaeManager.Application.Operaciones.IncorporacionCartera;
using MediatR;

namespace CaeManager.Application.Comunicaciones.Queries.ObtenerCompanerosGestorCae;

/// <summary>
/// Un Gestor CAE del mismo Operador CAE que quien pregunta, con los dos datos que sirven para contactarlo:
/// correo y teléfono de su cuenta. Cualquiera de los dos puede faltar (la cuenta no los declara).
/// </summary>
public record CompaneroGestorCaeDto(Guid UsuarioId, string Nombre, string? Correo, string? Telefono);

/// <summary>
/// Los Gestores CAE activos de un Operador CAE. Puerto porque <c>ApplicationUser</c> vive en
/// Infrastructure.Identity. <b>No decide quién puede preguntar</b> ni de qué Operador CAE: eso lo decide
/// <see cref="ObtenerCompanerosGestorCaeQueryHandler"/>, que le pasa el Tenant de origen ya verificado.
/// La implementación falla cerrada (lista vacía) si el ámbito del Tenant activo no es ese Operador CAE.
/// </summary>
public interface IDirectorioCompanerosGestorCae
{
    Task<IReadOnlyList<CompaneroGestorCaeDto>> ObtenerGestoresCaeDelOperadorAsync(
        Guid operadorTenantId, Guid excluirUsuarioId, CancellationToken cancellationToken = default);
}

/// <summary>
/// «Contactar con un compañero»: los demás Gestores CAE de <b>mi mismo Operador CAE</b>, para que un Gestor CAE
/// pueda escribir o llamar a quien lleva un tema cuando no sabe su número (decisión de producto del 2026-10-04).
///
/// <para>
/// <b>Autorización, aquí y no en la interfaz.</b> Solo contesta si quien pregunta es un Gestor CAE activo de su
/// Operador CAE, leído en Identity sobre su Tenant de origen y no en el claim (dentro de un Workspace operativo derivado
/// el claim es el de la cartera, y una Sesión Privilegiada de plataforma no trae rol de negocio:
/// <see cref="ContextoOperadorCae.ResolverAsync"/> falla cerrado en ambos). El Operador CAE sale de ese contexto
/// verificado, nunca de un parámetro de la petición, así que la petición no puede apuntar a otra organización.
/// Un Coordinador CAE, un Administrador o cualquier otro rol reciben la lista vacía.
/// </para>
///
/// <para>
/// No es la lectura del Actor de Plataforma TALVEG ni la reutiliza: el Soporte TALVEG no es Gestor CAE ni Operador
/// CAE y esa lectura no pasa por aquí. Tampoco ensancha la RLS de <c>AspNetUsers</c>: la lectura se hace dentro del
/// ámbito del propio Tenant de origen (<see cref="ContextoOperadorCae.EnOrigen"/>), la rama que la política ya permite.
/// </para>
/// </summary>
public record ObtenerCompanerosGestorCaeQuery : IRequest<IReadOnlyList<CompaneroGestorCaeDto>>;

public class ObtenerCompanerosGestorCaeQueryHandler(
    ICurrentUserService currentUserService,
    IDirectorioUsuariosService directorioUsuarios,
    IDirectorioCompanerosGestorCae companeros)
    : IRequestHandler<ObtenerCompanerosGestorCaeQuery, IReadOnlyList<CompaneroGestorCaeDto>>
{
    public async Task<IReadOnlyList<CompaneroGestorCaeDto>> Handle(
        ObtenerCompanerosGestorCaeQuery request, CancellationToken cancellationToken)
    {
        var contexto = await ContextoOperadorCae.ResolverAsync(currentUserService, directorioUsuarios, cancellationToken);
        if (contexto.EsFallido || !contexto.Valor.EsGestorCae)
            return [];

        using (contexto.Valor.EnOrigen())
        {
            return await companeros.ObtenerGestoresCaeDelOperadorAsync(
                contexto.Valor.OperadorTenantId, contexto.Valor.UsuarioId, cancellationToken);
        }
    }
}
