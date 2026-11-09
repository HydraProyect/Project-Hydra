using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Application.Subcontratas.Queries.ObtenerSubcontratasParaSelector;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using MediatR;

namespace CaeManager.Application.AsistenteIa.Candidatos;

/// <summary>
/// Un valor de la cartera para un dato de una orden, leído en el Tenant del ámbito
/// en curso. <paramref name="GrupoId"/> es el Cliente empresarial de un Centro (para
/// acotar los Centros al Cliente elegido); <paramref name="EsSubcontrata"/> distingue,
/// en el dato «empleador», una Subcontrata de una Empresa.
/// </summary>
public sealed record CandidatoLeidoAsistente(Guid Id, string Nombre, Guid? GrupoId = null, bool EsSubcontrata = false);

/// <summary>
/// La única lectura de los valores que el asistente puede ofrecer para cada dato:
/// la usan los candidatos que se mandan al modelo
/// (<see cref="ObtenerCandidatosAsistenteQueryHandler"/>) y las opciones con las que
/// el Gestor CAE completa el plan a mano. Tenerla en un solo sitio es lo que garantiza
/// que el plan no ofrezca a mano nada que el asistente no habría podido elegir.
/// <para>
/// Lee en el Tenant del ámbito en curso con las mismas Queries de selector que las
/// pantallas, que aplican la Asignación de Cartera (<see cref="IAlcanceDatosService"/>).
/// </para>
/// </summary>
public static class LectorCandidatosAsistente
{
    public static async Task<List<CandidatoLeidoAsistente>> LeerAsync(
        IMediator mediator, IAlcanceDatosService alcanceDatos, string campo, Guid tenantId, string tenantNombre,
        CancellationToken cancellationToken)
    {
        switch (campo)
        {
            case ObtenerCandidatosAsistenteQueryHandler.CampoTenant:
                return [new(tenantId, tenantNombre)];

            case ObtenerCandidatosAsistenteQueryHandler.CampoClienteEmpresarial:
                return (await mediator.Send(new ObtenerClientesParaSelectorQuery(), cancellationToken))
                    .Select(c => new CandidatoLeidoAsistente(c.Id, c.RazonSocial)).ToList();

            case ObtenerCandidatosAsistenteQueryHandler.CampoCentro:
                return (await mediator.Send(new ObtenerCentrosParaSelectorQuery(), cancellationToken))
                    .Select(c => new CandidatoLeidoAsistente(
                        c.Id, $"{c.Nombre} · {c.ClienteRazonSocial}", c.ClienteEmpresarialId == Guid.Empty ? null : c.ClienteEmpresarialId))
                    .ToList();

            case ObtenerCandidatosAsistenteQueryHandler.CampoTrabajadores:
                // Sin DNI por contrato del selector, y con la etiqueta única que
                // ya usan las pantallas: un candidato no puede describirse por su
                // documento (PlanDecisionCerrada lo rechazaría).
                var trabajadores = await mediator.Send(
                    new ObtenerTrabajadoresParaSelectorQuery(AlcanceSelectorTrabajadores.Cartera), cancellationToken);
                return EtiquetasSelectorTrabajador.Construir(trabajadores)
                    .Select(e => new CandidatoLeidoAsistente(e.Id, e.Texto)).ToList();

            case ObtenerCandidatosAsistenteQueryHandler.CampoEmpresa:
                return (await mediator.Send(new ObtenerEmpresasParaSelectorQuery(), cancellationToken))
                    .Select(e => new CandidatoLeidoAsistente(e.Id, e.RazonSocial)).ToList();

            case ObtenerCandidatosAsistenteQueryHandler.CampoEmpleador:
                var empresas = await mediator.Send(new ObtenerEmpresasParaSelectorQuery(), cancellationToken);
                // El selector de Subcontratas no aplica la cartera (sirve a
                // pantallas de configuración): aquí se acota con el mismo
                // alcance «para gestión» que usa el selector de Empresas.
                var subcontrataIds = await alcanceDatos.ObtenerSubcontrataIdsParaGestionAsync(cancellationToken);
                var subcontratas = (await mediator.Send(new ObtenerSubcontratasParaSelectorQuery(), cancellationToken))
                    .Where(s => subcontrataIds is null || subcontrataIds.Contains(s.Id));
                return empresas.Select(e => new CandidatoLeidoAsistente(e.Id, e.RazonSocial))
                    .Concat(subcontratas.Select(s => new CandidatoLeidoAsistente(s.Id, $"{s.RazonSocial} (subcontrata)", EsSubcontrata: true)))
                    .ToList();

            default:
                throw new ArgumentOutOfRangeException(nameof(campo), campo, null);
        }
    }
}
