using CaeManager.Application.AsistenteIa.Candidatos;
using CaeManager.Application.AsistenteIa.Ordenes;
using CaeManager.Application.Common;
using MediatR;

namespace CaeManager.Application.AsistenteIa.Edicion;

/// <summary>
/// Las opciones con las que el Gestor CAE completa a mano los datos de selección de
/// un plan, <b>solo en el Tenant efectivo</b> de la sesión (la Empresa gestionada que
/// tiene abierta): es el único Tenant en el que el plan se ejecuta. Cada lista sale
/// de <see cref="LectorCandidatosAsistente"/>, la misma lectura —y la misma
/// Asignación de Cartera— que los candidatos que ve el asistente.
/// <para>
/// <see cref="OpcionesPlanAsistenteDto.Tenant"/> es null cuando el Tenant efectivo no
/// es de la cartera de la persona (por ejemplo, el Tenant de origen de un Operador CAE
/// externo, sin ninguna Empresa gestionada abierta): entonces no hay opciones y el
/// plan no se puede completar aquí.
/// </para>
/// </summary>
public sealed record ObtenerOpcionesPlanAsistenteQuery(string OrdenId) : IRequest<OpcionesPlanAsistenteDto>;

public sealed record OpcionesPlanAsistenteDto(
    TenantDeCarteraDto? Tenant,
    IReadOnlyDictionary<string, IReadOnlyList<OpcionDatoPlanDto>> PorCampo)
{
    public static OpcionesPlanAsistenteDto Vacias { get; } =
        new(null, new Dictionary<string, IReadOnlyList<OpcionDatoPlanDto>>(StringComparer.Ordinal));

    public IReadOnlyList<OpcionDatoPlanDto> De(string campo) =>
        PorCampo.TryGetValue(campo, out var opciones) ? opciones : [];

    public OpcionDatoPlanDto? Buscar(string campo, Guid id) => De(campo).FirstOrDefault(o => o.Id == id);
}

public sealed class ObtenerOpcionesPlanAsistenteQueryHandler(
    IMediator mediator, IAlcanceDatosService alcanceDatos, ITenantActual tenantActual)
    : IRequestHandler<ObtenerOpcionesPlanAsistenteQuery, OpcionesPlanAsistenteDto>
{
    public async Task<OpcionesPlanAsistenteDto> Handle(ObtenerOpcionesPlanAsistenteQuery request, CancellationToken cancellationToken)
    {
        if (CatalogoOrdenesAsistente.PorId(request.OrdenId) is not { } orden
            || tenantActual.TenantId is not { } tenantId || tenantId == Guid.Empty)
            return OpcionesPlanAsistenteDto.Vacias;

        // La cartera la decide la misma Query que el asistente (Tenants autorizados
        // sin alcance cero): un Tenant que no está ahí no ofrece nada.
        var cartera = await mediator.Send(new ObtenerCandidatosAsistenteQuery([]), cancellationToken);
        if (cartera.Tenants.FirstOrDefault(t => t.TenantId == tenantId) is not { } tenant)
            return OpcionesPlanAsistenteDto.Vacias;

        var porCampo = new Dictionary<string, IReadOnlyList<OpcionDatoPlanDto>>(StringComparer.Ordinal);
        foreach (var campo in TiposDatoPlan.CamposConOpciones(orden))
        {
            porCampo[campo] = (await LectorCandidatosAsistente.LeerAsync(
                    mediator, alcanceDatos, campo, tenant.TenantId, tenant.Nombre, cancellationToken))
                .Select(c => new OpcionDatoPlanDto(c.Id, c.Nombre, c.GrupoId, c.EsSubcontrata))
                .OrderBy(o => o.Etiqueta, StringComparer.CurrentCulture)
                .ToList();
        }

        return new OpcionesPlanAsistenteDto(tenant, porCampo);
    }
}
