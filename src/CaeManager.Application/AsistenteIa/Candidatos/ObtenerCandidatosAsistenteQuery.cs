using CaeManager.Application.AsistenteIa.Decisiones;
using CaeManager.Application.Bandeja.Queries.ObtenerMiTrabajoAgregado;
using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Application.Subcontratas.Queries.ObtenerSubcontratasParaSelector;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using MediatR;

namespace CaeManager.Application.AsistenteIa.Candidatos;

/// <summary>
/// Los valores entre los que el asistente puede elegir para cada dato de una
/// orden, reunidos entre <b>todos los Tenants de la cartera</b> del Gestor CAE y
/// sellados con el Tenant del que salen. Es el paso previo a
/// <see cref="IDecisionesCerradasAsistenteService.SeleccionarCandidatosAsync"/>,
/// que no amplía ni comprueba alcance: lo que no esté aquí, el modelo no lo
/// puede elegir.
/// <para>
/// Mismo fan-out que <see cref="ObtenerMiTrabajoAgregadoQueryHandler"/>: los
/// Tenants salen de <see cref="ObtenerClientesAutorizadosQuery"/> y cada uno se
/// consulta sellado por <see cref="AmbitoTenantExplicito"/>, con su RLS normal.
/// Dentro, los candidatos los dan las mismas Queries de selector que ya usan las
/// pantallas, que aplican la cartera (<see cref="IAlcanceDatosService"/>). Un
/// Tenant con alcance cero (<see cref="ObtenerMiTrabajoAgregadoQueryHandler.EsAlcanceCeroAsync"/>)
/// no entra: no es de la cartera aunque haya una delegación hacia él, y no se
/// ofrecerá nunca como Tenant donde ejecutar.
/// </para>
/// <para>
/// El Tenant de cada candidato es el del ámbito en el que se leyó, nunca uno
/// deducido del texto de la orden: el Tenant que nombre la orden es una
/// coordenada, no una autoridad. <see cref="ResolucionTenantDestino"/> decide
/// después en qué Tenant se ejecuta.
/// </para>
/// </summary>
/// <param name="Campos">Nombres de <see cref="Ordenes.CampoDeOrden"/>; ver <see cref="CamposConCandidatos"/>.</param>
public record ObtenerCandidatosAsistenteQuery(IReadOnlyList<string> Campos) : IRequest<CandidatosAsistenteDto>;

/// <summary>Un Tenant de la cartera: donde el Gestor CAE puede pedir que se ejecute una orden.</summary>
public record TenantDeCarteraDto(Guid TenantId, string Nombre, bool EsOrigen);

/// <summary>
/// Un candidato y el Tenant del que sale. <paramref name="Nombre"/> es lo que lee
/// el modelo: con más de un Tenant en la cartera lleva el nombre del Tenant, para
/// que dos Centros homónimos de Tenants distintos no se confundan.
/// </summary>
public record CandidatoSelladoDto(Guid Id, string Nombre, Guid TenantId)
{
    public CandidatoDecisionDto ParaDecision() => new(Id, Nombre);
}

public record CandidatosAsistenteDto(
    IReadOnlyList<TenantDeCarteraDto> Tenants,
    IReadOnlyDictionary<string, IReadOnlyList<CandidatoSelladoDto>> PorCampo)
{
    /// <summary>
    /// Los mismos candidatos, reducidos a un Tenant de la cartera: lo que se
    /// vuelve a preguntar cuando el Gestor CAE ya ha elegido dónde se ejecuta.
    /// Un Tenant fuera de la cartera no deja ningún candidato.
    /// </summary>
    public CandidatosAsistenteDto SoloDelTenant(Guid tenantId)
    {
        var tenants = Tenants.Where(t => t.TenantId == tenantId).ToList();
        return new CandidatosAsistenteDto(
            tenants,
            PorCampo.ToDictionary(
                c => c.Key,
                c => (IReadOnlyList<CandidatoSelladoDto>)c.Value.Where(x => tenants.Count > 0 && x.TenantId == tenantId).ToList()));
    }

    /// <summary>De qué Tenant sale un candidato; null si no está entre estos.</summary>
    public Guid? TenantDe(string campo, Guid candidatoId) =>
        PorCampo.TryGetValue(campo, out var candidatos)
            ? candidatos.FirstOrDefault(c => c.Id == candidatoId)?.TenantId
            : null;
}

public class ObtenerCandidatosAsistenteQueryHandler(IMediator mediator, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerCandidatosAsistenteQuery, CandidatosAsistenteDto>
{
    public const string CampoTenant = "tenant";
    public const string CampoClienteEmpresarial = "cliente_empresarial";
    public const string CampoCentro = "centro";
    public const string CampoTrabajadores = "trabajadores";
    public const string CampoEmpresa = "empresa";
    public const string CampoEmpleador = "empleador";

    /// <summary>
    /// Los campos de selección para los que TALVEG sabe construir candidatos. Uno
    /// que no esté aquí se rechaza en vez de devolverlo vacío: una lista vacía
    /// haría que el modelo se abstuviera y parecería que la orden no lo nombra.
    /// </summary>
    public static readonly IReadOnlySet<string> CamposConCandidatos = new HashSet<string>(StringComparer.Ordinal)
    {
        CampoTenant, CampoClienteEmpresarial, CampoCentro, CampoTrabajadores, CampoEmpresa, CampoEmpleador,
    };

    public async Task<CandidatosAsistenteDto> Handle(ObtenerCandidatosAsistenteQuery request, CancellationToken cancellationToken)
    {
        var desconocidos = request.Campos.Where(c => !CamposConCandidatos.Contains(c)).ToList();
        if (desconocidos.Count > 0)
            throw new ArgumentException(
                $"Sin constructor de candidatos para: {string.Join(", ", desconocidos)}.", nameof(request));

        var autorizados = await mediator.Send(new ObtenerClientesAutorizadosQuery(), cancellationToken);

        var tenants = new List<TenantDeCarteraDto>();
        var porTenant = new List<(TenantDeCarteraDto Tenant, Dictionary<string, List<(Guid Id, string Nombre)>> Campos)>();
        foreach (var tenant in autorizados)
        {
            // Sellado por Tenant, un Tenant cada vez, como Mi trabajo agregada:
            // el alcance y las Queries de selector de esta vuelta solo ven este
            // Tenant, y el sello de cada candidato es este TenantId.
            using (AmbitoTenantExplicito.Establecer(tenant.TenantId))
            {
                if (await ObtenerMiTrabajoAgregadoQueryHandler.EsAlcanceCeroAsync(alcanceDatos, cancellationToken))
                    continue;

                var deCartera = new TenantDeCarteraDto(tenant.TenantId, tenant.Nombre, tenant.EsOrigen);
                tenants.Add(deCartera);

                var campos = new Dictionary<string, List<(Guid, string)>>(StringComparer.Ordinal);
                foreach (var campo in request.Campos.Distinct(StringComparer.Ordinal))
                    campos[campo] = await LeerAsync(campo, deCartera, cancellationToken);
                porTenant.Add((deCartera, campos));
            }
        }

        var variosTenants = tenants.Count > 1;
        var porCampo = request.Campos.Distinct(StringComparer.Ordinal).ToDictionary(
            campo => campo,
            campo => (IReadOnlyList<CandidatoSelladoDto>)porTenant
                .SelectMany(t => t.Campos[campo].Select(c => new CandidatoSelladoDto(
                    c.Id,
                    variosTenants && campo != CampoTenant ? $"{c.Nombre} · en {t.Tenant.Nombre}" : c.Nombre,
                    t.Tenant.TenantId)))
                .ToList(),
            StringComparer.Ordinal);

        return new CandidatosAsistenteDto(tenants, porCampo);
    }

    private async Task<List<(Guid, string)>> LeerAsync(string campo, TenantDeCarteraDto tenant, CancellationToken cancellationToken)
    {
        switch (campo)
        {
            case CampoTenant:
                return [(tenant.TenantId, tenant.Nombre)];

            case CampoClienteEmpresarial:
                return (await mediator.Send(new ObtenerClientesParaSelectorQuery(), cancellationToken))
                    .Select(c => (c.Id, c.RazonSocial)).ToList();

            case CampoCentro:
                return (await mediator.Send(new ObtenerCentrosParaSelectorQuery(), cancellationToken))
                    .Select(c => (c.Id, $"{c.Nombre} · {c.ClienteRazonSocial}")).ToList();

            case CampoTrabajadores:
                // Sin DNI por contrato del selector, y con la etiqueta única que
                // ya usan las pantallas: un candidato no puede describirse por su
                // documento (PlanDecisionCerrada lo rechazaría).
                var trabajadores = await mediator.Send(
                    new ObtenerTrabajadoresParaSelectorQuery(AlcanceSelectorTrabajadores.Cartera), cancellationToken);
                return EtiquetasSelectorTrabajador.Construir(trabajadores).Select(e => (e.Id, e.Texto)).ToList();

            case CampoEmpresa:
                return (await mediator.Send(new ObtenerEmpresasParaSelectorQuery(), cancellationToken))
                    .Select(e => (e.Id, e.RazonSocial)).ToList();

            case CampoEmpleador:
                var empresas = await mediator.Send(new ObtenerEmpresasParaSelectorQuery(), cancellationToken);
                // El selector de Subcontratas no aplica la cartera (sirve a
                // pantallas de configuración): aquí se acota con el mismo
                // alcance «para gestión» que usa el selector de Empresas.
                var subcontrataIds = await alcanceDatos.ObtenerSubcontrataIdsParaGestionAsync(cancellationToken);
                var subcontratas = (await mediator.Send(new ObtenerSubcontratasParaSelectorQuery(), cancellationToken))
                    .Where(s => subcontrataIds is null || subcontrataIds.Contains(s.Id));
                return empresas.Select(e => (e.Id, e.RazonSocial))
                    .Concat(subcontratas.Select(s => (s.Id, $"{s.RazonSocial} (subcontrata)")))
                    .ToList();

            default:
                throw new ArgumentOutOfRangeException(nameof(campo), campo, null);
        }
    }
}
