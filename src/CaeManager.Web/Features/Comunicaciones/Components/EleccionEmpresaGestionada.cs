namespace CaeManager.Web.Features.Comunicaciones.Components;

/// <summary>
/// Lo que se ha elegido en el selector «Empresa gestionada» (<see cref="SelectorEmpresaGestionada"/>): una coordenada del navegador
/// que solo estrecha la vista unificada, nunca autoridad. El servidor la interseca con el conjunto que calcula
/// (<c>EntradaVistaUnificada.Intersecar</c>).
/// </summary>
/// <param name="TenantId">El Tenant beneficiario consultado; <c>null</c> es «Todas las Empresas gestionadas».</param>
public sealed record EleccionEmpresaGestionada(Guid? TenantId);
