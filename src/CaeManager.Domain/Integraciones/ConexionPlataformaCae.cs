using CaeManager.Domain.Common;

namespace CaeManager.Domain.Integraciones;

/// <summary>
/// Conexión de un Tenant propietario con una plataforma CAE de terceros por su API oficial, bajo
/// acuerdo comercial directo (Project-Hydra-Negocio/DECISION_LOG.md, 2026-10-30). Nunca es acceso
/// desde servidor a un portal: sin API ni acuerdo se usa la extensión de navegador con la sesión del
/// Gestor CAE.
///
/// <para>
/// El nombre distingue dos conexiones del mismo Tenant propietario con la misma plataforma, que son
/// legítimas (Project-Hydra-Negocio/tecnico/ARQUITECTURA-INTEGRACIONES.md § 3). La credencial vive aparte, en
/// <see cref="CredencialConexionPlataformaCae"/>, para que Soporte TALVEG pueda leer esta tabla sin
/// llegar al secreto. Retirar la conexión la marca como eliminada; la credencial se borra de verdad.
/// </para>
/// </summary>
public class ConexionPlataformaCae : EntidadBase
{
    public const int LongitudMaximaNombre = 150;

    public Guid ProveedorPlataformaCaeId { get; private set; }
    public string Nombre { get; private set; } = string.Empty;

    private ConexionPlataformaCae()
    {
    }

    public ConexionPlataformaCae(Guid proveedorPlataformaCaeId, string nombre)
    {
        if (proveedorPlataformaCaeId == Guid.Empty)
            throw new ArgumentException("La conexión debe referenciar una plataforma CAE.", nameof(proveedorPlataformaCaeId));
        if (string.IsNullOrWhiteSpace(nombre))
            throw new ArgumentException("El nombre de la conexión es obligatorio.", nameof(nombre));

        var normalizado = nombre.Trim();
        if (normalizado.Length > LongitudMaximaNombre)
            throw new ArgumentException($"El nombre no puede superar {LongitudMaximaNombre} caracteres.", nameof(nombre));

        ProveedorPlataformaCaeId = proveedorPlataformaCaeId;
        Nombre = normalizado;
    }
}
