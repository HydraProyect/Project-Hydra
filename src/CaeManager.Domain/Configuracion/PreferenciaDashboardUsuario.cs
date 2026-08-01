using CaeManager.Domain.Common;

namespace CaeManager.Domain.Configuracion;

/// <summary>
/// Selección de KPIs que un usuario ha elegido ver en el Dashboard Ejecutivo.
///
/// Extiende <see cref="Entity"/>, no <see cref="EntidadConTenant"/>: catálogo
/// global, mismo tratamiento que <c>AsignacionOperadorDelegado</c> — la
/// preferencia de un usuario aplica sin importar qué Cliente/tenant tenga
/// activo, así que no tiene sentido acotarla a uno.
///
/// <see cref="UsuarioId"/> es un Guid suelto sin navegación EF hacia
/// <c>ApplicationUser</c> — Identity vive en Infrastructure, no accesible
/// desde Domain (mismo patrón que <c>AsignacionOperadorDelegado.UsuarioId</c>).
/// Los códigos de KPI no se validan aquí contra el catálogo conocido: esa
/// validación vive en el validador del Command (Application), igual que
/// ocurre con <c>Rol</c> en <c>CrearAsignacionOperadorDelegadoCommandValidator</c>.
/// </summary>
public class PreferenciaDashboardUsuario : Entity
{
    public Guid UsuarioId { get; private set; }
    public List<string> CodigosKpiSeleccionados { get; private set; } = [];

    private PreferenciaDashboardUsuario()
    {
        // Requerido por EF Core.
    }

    public PreferenciaDashboardUsuario(Guid usuarioId, IEnumerable<string> codigosKpi)
    {
        if (usuarioId == Guid.Empty)
            throw new ArgumentException("La preferencia debe pertenecer a un usuario.", nameof(usuarioId));

        UsuarioId = usuarioId;
        ActualizarSeleccion(codigosKpi);
    }

    public void ActualizarSeleccion(IEnumerable<string> codigosKpi)
    {
        var codigos = codigosKpi.ToList();

        if (codigos.Count == 0)
            throw new ArgumentException("Debe seleccionarse al menos un KPI.", nameof(codigosKpi));

        CodigosKpiSeleccionados = codigos;
    }
}
