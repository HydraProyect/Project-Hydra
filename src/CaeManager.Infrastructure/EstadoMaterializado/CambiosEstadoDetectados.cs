namespace CaeManager.Infrastructure.EstadoMaterializado;

/// <summary>
/// Lo que los guardados de un <c>DbContext</c> cambiaron en las entradas del cálculo del estado materializado y aún no se
/// ha recalculado, por Tenant propietario del dato. Lo llena <see cref="DetectorCambiosEstadoInterceptor"/> desde el
/// <c>ChangeTracker</c> y lo vacía <see cref="RecalculoEstadoEnLinea"/>.
/// </summary>
public sealed class CambiosEstadoDetectados
{
    private readonly Dictionary<Guid, CambiosEstadoDeTenant> _porTenant = [];

    public bool EstaVacio => _porTenant.Count == 0;

    public IReadOnlyDictionary<Guid, CambiosEstadoDeTenant> PorTenant => _porTenant;

    public CambiosEstadoDeTenant De(Guid tenantId)
    {
        if (!_porTenant.TryGetValue(tenantId, out var cambios))
            _porTenant[tenantId] = cambios = new CambiosEstadoDeTenant();
        return cambios;
    }

    public void Sumar(CambiosEstadoDetectados otros)
    {
        foreach (var (tenant, cambios) in otros._porTenant)
            De(tenant).Sumar(cambios);
    }

    /// <summary>Devuelve lo acumulado y deja el acumulador vacío.</summary>
    public CambiosEstadoDetectados Tomar()
    {
        var tomados = new CambiosEstadoDetectados();
        tomados.Sumar(this);
        _porTenant.Clear();
        return tomados;
    }

    public void Descartar() => _porTenant.Clear();
}

/// <summary>
/// Las raíces de un Tenant (ver <c>RaicesDeRecalculo</c>), más lo que hay que resolver contra la base porque la entidad
/// cambiada no lleva el Centro ni el titular: una acreditación en la Plataforma CAE (Centro por su canal, titular por su
/// documento) y los eventos (Centro del Proyecto y sus participantes).
/// </summary>
public sealed class CambiosEstadoDeTenant
{
    public HashSet<Guid> Centros { get; } = [];
    public HashSet<Guid> CentrosAfectados { get; } = [];
    public HashSet<Guid> Trabajadores { get; } = [];
    public HashSet<Guid> Empresas { get; } = [];
    public HashSet<(Guid DocumentoId, Guid CanalId)> Acreditaciones { get; } = [];

    /// <summary>Eventos (Proyecto) cuyo Centro y todos sus participantes, también los dados de baja, se rehacen.</summary>
    public HashSet<Guid> Eventos { get; } = [];

    /// <summary>Participación de un Trabajador en un evento: el Trabajador y el Centro del evento (como una Asignación).</summary>
    public HashSet<(Guid ProyectoId, Guid TrabajadorId)> Participaciones { get; } = [];

    /// <summary>Si no es null, el Tenant entero va a la cola con este motivo y lo demás sobra.</summary>
    public string? TenantEntero { get; set; }

    /// <summary>Tipos de entidad que dispararon, para el motivo de la cola.</summary>
    public SortedSet<string> Disparadores { get; } = new(StringComparer.Ordinal);

    public void Sumar(CambiosEstadoDeTenant otros)
    {
        Centros.UnionWith(otros.Centros);
        CentrosAfectados.UnionWith(otros.CentrosAfectados);
        Trabajadores.UnionWith(otros.Trabajadores);
        Empresas.UnionWith(otros.Empresas);
        Acreditaciones.UnionWith(otros.Acreditaciones);
        Eventos.UnionWith(otros.Eventos);
        Participaciones.UnionWith(otros.Participaciones);
        TenantEntero ??= otros.TenantEntero;
        Disparadores.UnionWith(otros.Disparadores);
    }
}
