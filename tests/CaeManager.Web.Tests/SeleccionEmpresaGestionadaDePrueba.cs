using System;
using CaeManager.Application.Common;

namespace CaeManager.Web.Tests;

/// <summary>
/// Doble de <see cref="IClienteActivoSeleccionado"/> para las pantallas que ahora
/// conocen la empresa gestionada activa (selector de la barra lateral y cabecera
/// de Trabajadores): la cookie de selección, sin cookie ni HttpContext.
/// </summary>
public sealed class SeleccionEmpresaGestionadaDePrueba(Guid? tenantSeleccionado = null) : IClienteActivoSeleccionado
{
    public Guid? TenantIdSeleccionado { get; } = tenantSeleccionado;
    public Guid? AsignacionOperacionIdSeleccionada => null;
    public Guid? SesionPrivilegiadaIdSeleccionada => null;
}
