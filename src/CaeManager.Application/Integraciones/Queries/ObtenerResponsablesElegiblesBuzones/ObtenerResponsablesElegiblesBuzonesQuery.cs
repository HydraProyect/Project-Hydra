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
/// Coordinador CAE y Gestor CAE según su cartera). El universo de candidatos lo pone quien consulta, sin filtrar por rol: todas
/// las cuentas visibles de su directorio, de modo que también entra la cuenta de un Operador CAE externo que la regla acepta por
/// el rol guardado de la vía heredada. El buzón se acota igual que en el listado y en el comando: Microsoft 365, compartido,
/// visible y con su Cliente empresarial dentro del alcance de quien consulta (su Asignación de Cartera, salvo quien ve el Tenant
/// entero); fuera de ese alcance el buzón no aparece en el resultado. El comando sigue revalidando todo: esto solo decide qué
/// se ofrece.
/// </summary>
/// <param name="ConexionIds">Buzones del listado que ya pidió quien consulta.</param>
/// <param name="CandidatoIds">Cuentas que quien consulta ya ve en su directorio: esta consulta no descubre cuentas nuevas.</param>
public record ObtenerResponsablesElegiblesBuzonesQuery(
    IReadOnlyCollection<Guid> ConexionIds, IReadOnlyCollection<Guid> CandidatoIds)
    : IRequest<IReadOnlyDictionary<Guid, IReadOnlySet<Guid>>>;

/// <remarks>
/// Un buzón que no existe, que no es de Microsoft 365, que es personal (responde siempre a su propietario), que quien
/// consulta no ve o cuyo Cliente empresarial queda fuera de su alcance queda fuera del resultado, igual que en el comando: no
/// se distingue de uno inexistente. Quien no es Administrador no obtiene nada: la pantalla
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

        var buzones = new List<ConexionIntegracion>();
        foreach (var conexionId in request.ConexionIds.Distinct())
        {
            var conexion = await conexionRepositorio.ObtenerPorIdAsync(conexionId, cancellationToken);
            if (conexion is null
                || conexion.Proveedor != ProveedorIntegracion.Microsoft365
                || conexion.GestorPropietarioId is not null
                || !await alcanceDatos.ClienteOpcionalVisibleAsync(conexion.ClienteId, cancellationToken)
                || !await alcanceDatos.ConexionIntegracionVisibleAsync(conexion.Id, cancellationToken))
                continue;

            buzones.Add(conexion);
        }

        if (buzones.Count == 0) return resultado;

        var elegibles = buzones.ToDictionary(b => b.Id, _ => new HashSet<Guid>());
        // Una evaluación por cuenta para todos los buzones: la cuenta y su acceso se leen una vez, no una por buzón.
        foreach (var candidato in request.CandidatoIds.Distinct())
        {
            foreach (var (buzonId, motivo) in await elegibilidad.EvaluarAsync(candidato, buzones, cancellationToken))
            {
                if (motivo is null) elegibles[buzonId].Add(candidato);
            }
        }

        foreach (var (buzonId, cuentas) in elegibles) resultado[buzonId] = cuentas;
        return resultado;
    }
}
