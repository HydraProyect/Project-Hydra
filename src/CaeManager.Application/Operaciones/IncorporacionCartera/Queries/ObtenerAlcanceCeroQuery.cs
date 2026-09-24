using CaeManager.Application.Bandeja.Queries.ObtenerMiTrabajoAgregado;
using CaeManager.Application.Common;
using MediatR;

namespace CaeManager.Application.Operaciones.IncorporacionCartera.Queries;

/// <summary>
/// Si quien mira no alcanza nada en el Tenant vigente: sin acceso total y con
/// la cartera de Clientes empresariales vacía (el mismo criterio que
/// <see cref="ObtenerMiTrabajoAgregadoQueryHandler.EsAlcanceCeroAsync"/>). Es
/// el caso de un Gestor CAE sin ninguna Asignación de Cartera vigente en el
/// Tenant propietario, o del Administrador de un Operador CAE externo que ve
/// el Tenant delegado sin cartera en él.
/// <para>
/// Con él, una lista o una cola vacía distingue «no hay nada» de «no alcanzas
/// nada» (P0-9a, FS-03 a FS-06 de la auditoría UX del 2026-09-24): la pantalla
/// no dice «al día» ni invita a crear lo que quizá ya existe fuera del alcance.
/// Alcance cero es un estado correcto, no un error. Solo describe el alcance;
/// no filtra ni autoriza nada.
/// </para>
/// </summary>
public record ObtenerAlcanceCeroQuery : IRequest<bool>;

public class ObtenerAlcanceCeroQueryHandler(IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerAlcanceCeroQuery, bool>
{
    public Task<bool> Handle(ObtenerAlcanceCeroQuery request, CancellationToken cancellationToken) =>
        ObtenerMiTrabajoAgregadoQueryHandler.EsAlcanceCeroAsync(alcanceDatos, cancellationToken);
}
