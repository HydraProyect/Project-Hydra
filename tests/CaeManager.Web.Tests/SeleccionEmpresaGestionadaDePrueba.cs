using System;
using CaeManager.Application.Common;

namespace CaeManager.Web.Tests;

/// <summary>
/// Doble de <see cref="IClienteActivoSeleccionado"/> para las pantallas que ahora
/// conocen la empresa gestionada activa (selector de la barra lateral y cabecera
/// de Trabajadores): la cookie de selección, sin cookie ni HttpContext.
/// </summary>
public sealed class SeleccionEmpresaGestionadaDePrueba(Guid? tenantSeleccionado = null) : IClienteActivoSeleccionado, ITenantActual
{
    public Guid? TenantIdSeleccionado { get; } = tenantSeleccionado;
    Guid? ITenantActual.TenantId => TenantIdSeleccionado;
    public Guid? AsignacionOperacionIdSeleccionada => null;
    public Guid? SesionPrivilegiadaIdSeleccionada => null;
}

/// <summary>Doble de <see cref="CaeManager.Application.Plataforma.ICoordenadaSesionPrivilegiada"/> con la coordenada fijada.</summary>
internal sealed class CoordenadaSesionPrivilegiadaDePrueba(bool haySeleccionada) : CaeManager.Application.Plataforma.ICoordenadaSesionPrivilegiada
{
    public bool HaySeleccionada { get; } = haySeleccionada;
}

internal sealed class SeleccionPrivilegiadaDePrueba : IClienteActivoSeleccionado
{
    public Guid? TenantIdSeleccionado => null;
    public Guid? AsignacionOperacionIdSeleccionada => null;
    public Guid? SesionPrivilegiadaIdSeleccionada { get; set; } = Guid.NewGuid();
}
