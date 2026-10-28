using System.Resources;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Common;
using CaeManager.Domain.Trabajadores;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Visitas;

/// <summary>
/// Qué Trabajadores pueden entrar en una Visita (decisión del propietario, 2026-10-28, hallazgo
/// S-B-1): el selector de Empresa gestionada fija el alcance. En una Visita solo entran Trabajadores
/// de la Empresa gestionada activa, nunca de otra Empresa ni de otro Tenant de la cartera del Gestor
/// CAE, porque su documentación —también la sensible, como la aptitud médica— viaja después al
/// Centro en el paquete documental.
///
/// <para>
/// Tres comprobaciones, todas en servidor y sin confiar en el selector:
/// <list type="bullet">
/// <item><b>Tenant</b>: la Empresa gestionada activa es el Tenant efectivo (<see cref="ITenantActual"/>);
/// <see cref="ITrabajadoresQueryContext"/> ya está acotado a él por el filtro de consulta y RLS, así
/// que un Trabajador de otro Tenant de la cartera no se encuentra.</item>
/// <item><b>Empresa de la Visita</b>: dentro del Tenant, el Trabajador es de la Empresa a la que
/// pertenece el Centro de la Visita (<c>Centro.EmpresaId</c>, en <c>EmpresaId</c> o, si esa Empresa es
/// una Subcontrata, en <c>SubcontrataId</c>), o de una Subcontrata con una
/// <c>RelacionEmpresarial</c> vigente como proveedora de esa Empresa (<see cref="DeLaEmpresa"/>).</item>
/// <item><b>Alcance de datos</b>: el Trabajador es visible para quien opera
/// (<see cref="IAlcanceDatosService.ObtenerTrabajadorIdsVisiblesAsync"/>, el mismo criterio que
/// <c>ObtenerDocumentoPorIdQuery</c> aplica al abrir cada Documento).</item>
/// </list>
/// Lo usan <c>CrearVisita</c>, <c>EditarVisita</c> (solo para los Trabajadores que se añaden),
/// <c>AnadirTrabajadorAVisita</c>, los candidatos del panel y, como defensa en profundidad, el
/// paquete documental. Un Id que no existe, de otra Empresa o fuera de alcance recibe la misma
/// respuesta, para no revelar qué hay fuera.
/// </para>
/// </summary>
public static class TrabajadoresAdmisiblesEnVisita
{
    public const string CodigoNoAdmisible = "Visita.TrabajadorNoAdmisible";

    private static readonly ResourceManager Textos = new(
        "CaeManager.Application.Visitas.Recursos.ErroresVisita", typeof(TrabajadoresAdmisiblesEnVisita).Assembly);

    /// <summary>Error de servidor cuando alguno de los Trabajadores pedidos no puede entrar en la Visita.</summary>
    public static Error NoAdmisible(bool varios) =>
        Error.Crear(CodigoNoAdmisible, Textos.GetString(varios ? "TrabajadoresNoAdmisibles" : "TrabajadorNoAdmisible")!);

    /// <summary>
    /// Trabajadores de la Empresa <paramref name="empresaId"/>: los suyos y los de sus Subcontratas
    /// vigentes. «Los suyos» incluye el caso en que la Empresa del Centro es ella misma una Subcontrata
    /// (p. ej. de nivel de servicio Gestionada): sus Trabajadores la llevan en <c>SubcontrataId</c>, no en
    /// <c>EmpresaId</c>. Componible en una consulta de EF (un único SQL con subconsulta correlacionada).
    /// </summary>
    public static IQueryable<Trabajador> DeLaEmpresa(
        this IQueryable<Trabajador> trabajadores, Guid empresaId, IEmpresasQueryContext empresas)
    {
        var relaciones = empresas.RelacionesEmpresariales;
        return trabajadores.Where(t => t.EmpresaId == empresaId
            || t.SubcontrataId == empresaId
            || (t.SubcontrataId != null && relaciones.Any(r =>
                r.VigenciaHasta == null && r.ProveedoraId == t.SubcontrataId && r.ClienteId == empresaId)));
    }

    /// <summary>
    /// Carga los Trabajadores pedidos si TODOS pueden entrar en la Visita de la Empresa
    /// <paramref name="empresaId"/>; <c>null</c> si alguno no existe, es de otra Empresa o queda
    /// fuera del alcance de datos. Se cargan enteros: la asignación guarda su foto
    /// (<c>VisitaTrabajador.EsExtranjeroCobrable</c>).
    /// </summary>
    public static async Task<Dictionary<Guid, Trabajador>?> CargarAsync(
        IReadOnlyCollection<Guid> trabajadorIds, Guid empresaId,
        ITrabajadoresQueryContext trabajadoresContext, IEmpresasQueryContext empresasContext,
        IAlcanceDatosService alcanceDatos, CancellationToken cancellationToken)
    {
        if (trabajadorIds.Count == 0)
            return [];

        var visibles = await alcanceDatos.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken);
        if (visibles is not null && trabajadorIds.Any(id => !visibles.Contains(id)))
            return null;

        var trabajadores = await trabajadoresContext.Trabajadores
            .Where(t => trabajadorIds.Contains(t.Id))
            .DeLaEmpresa(empresaId, empresasContext)
            .ToDictionaryAsync(t => t.Id, cancellationToken);

        return trabajadores.Count == trabajadorIds.Distinct().Count() ? trabajadores : null;
    }

    /// <summary>
    /// De <paramref name="trabajadorIds"/>, los que pueden entrar en una Visita de la Empresa
    /// <paramref name="empresaId"/> según el alcance de datos de quien pregunta.
    /// </summary>
    public static async Task<IReadOnlyList<Guid>> FiltrarAsync(
        IReadOnlyCollection<Guid> trabajadorIds, Guid empresaId,
        ITrabajadoresQueryContext trabajadoresContext, IEmpresasQueryContext empresasContext,
        IAlcanceDatosService alcanceDatos, CancellationToken cancellationToken)
    {
        if (trabajadorIds.Count == 0)
            return [];

        var visibles = await alcanceDatos.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken);
        var pedidos = visibles is null ? trabajadorIds.ToList() : trabajadorIds.Where(visibles.Contains).ToList();
        if (pedidos.Count == 0)
            return [];

        return await trabajadoresContext.Trabajadores
            .Where(t => pedidos.Contains(t.Id))
            .DeLaEmpresa(empresaId, empresasContext)
            .Select(t => t.Id)
            .ToListAsync(cancellationToken);
    }
}
