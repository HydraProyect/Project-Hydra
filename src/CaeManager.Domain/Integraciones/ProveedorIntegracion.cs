namespace CaeManager.Domain.Integraciones;

/// <summary>
/// Proveedor externo al que pertenece una <see cref="ConexionIntegracion"/>.
/// Nace con la llegada del segundo proveedor real (WhatsApp Cloud API,
/// 2026-08-04) — hasta entonces el único era Microsoft 365 y el campo habría
/// sido especulativo (YAGNI, ARQUITECTURA-INTEGRACIONES.md § 11). Un enum y
/// no el catálogo completo ProveedorIntegracion/VersionApiProveedor de § 3:
/// dos proveedores de mensajería no justifican todavía ese framework,
/// pensado para la sincronización documental CAE (Dokify/CTAIMA).
/// </summary>
public enum ProveedorIntegracion
{
    Microsoft365 = 0,
    WhatsApp = 1
}
