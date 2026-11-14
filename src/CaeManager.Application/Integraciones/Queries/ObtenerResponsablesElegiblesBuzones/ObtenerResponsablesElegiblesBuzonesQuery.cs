using CaeManager.Application.Clientes;
using CaeManager.Application.Common;
using CaeManager.Domain.Integraciones;
using MediatR;

namespace CaeManager.Application.Integraciones.Queries.ObtenerResponsablesElegiblesBuzones;

/// <summary>
/// Para cada buzón compartido de Microsoft 365, cuáles de estas cuentas puede fijar el Administrador como su Responsable:
/// las mismas que aceptaría <c>FijarResponsableBuzonCommand</c>, porque la decide <see cref="ResponsableBuzonElegible"/>, la
/// regla que aplican también ese comando y la ingesta. Alimenta el selector de Buzones, que así no ofrece a nadie que el
/// servidor vaya a rechazar ni deja fuera, entre los candidatos que se le dan, a quien acepta (Administrador, Dirección CAE,
/// Coordinador CAE y Gestor CAE según su cartera). El universo de candidatos lo pone quien consulta, por rol de origen: una
/// cuenta delegada con otro rol de origen y un rol guardado de gestión no se ofrece. El comando sigue revalidando todo, incluida
/// la visibilidad del Cliente empresarial del buzón, que esta consulta no repite: solo decide qué se ofrece.
/// </summary>
/// <param name="ConexionIds">Buzones del listado que ya pidió quien consulta.</param>
/// <param name="CandidatoIds">Cuentas que quien consulta ya ve en su directorio: esta consulta no descubre cuentas nuevas.</param>
public record ObtenerResponsablesElegiblesBuzonesQuery(
    IReadOnlyCollection<Guid> ConexionIds, IReadOnlyCollection<Guid> CandidatoIds)
    : IRequest<IReadOnlyDictionary<Guid, IReadOnlySet<Guid>>>;

/// <remarks>
/// Un buzón que no existe, que no es de Microsoft 365, que es personal (responde siempre a su propietario) o que quien
/// consulta no ve queda fuera del resultado, igual que en el comando. Quien no es Administrador no obtiene nada: la pantalla
/// no es una frontera de autorización y el selector solo es suyo.
/// </remarks>
public class ObtenerResponsablesElegiblesBuzonesQueryHandler(
    IConexionIntegracionRepository conexionRepositorio,
    IAlcanceDatosService alcanceDatos,
    ResponsableBuzonElegible elegibilidad,
    ICurrentUserService currentUserService)
    : IRequestHandler<ObtenerResponsablesElegiblesBuzonesQuery, IReadOnlyDictionary<Guid, IReadOnlySet<Guid>>>
{
    private const string RolAdministrador = "Administrador";

    public async Task<IReadOnlyDictionary<Guid, IReadOnlySet<Guid>>> Handle(
        ObtenerResponsablesElegiblesBuzonesQuery request, CancellationToken cancellationToken)
    {
        var resultado = new Dictionary<Guid, IReadOnlySet<Guid>>();
        if (await currentUserService.ObtenerRolEfectivoAsync() != RolAdministrador) return resultado;

        foreach (var conexionId in request.ConexionIds.Distinct())
        {
            var conexion = await conexionRepositorio.ObtenerPorIdAsync(conexionId, cancellationToken);
            if (conexion is null
                || conexion.Proveedor != ProveedorIntegracion.Microsoft365
                || conexion.GestorPropietarioId is not null
                || !await alcanceDatos.ConexionIntegracionVisibleAsync(conexion.Id, cancellationToken))
                continue;

            var elegibles = new HashSet<Guid>();
            foreach (var candidato in request.CandidatoIds.Distinct())
            {
                if (await elegibilidad.EvaluarAsync(candidato, conexion, cancellationToken) is null)
                    elegibles.Add(candidato);
            }
            resultado[conexionId] = elegibles;
        }

        return resultado;
    }
}
