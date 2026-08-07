using CaeManager.Domain.Common;

namespace CaeManager.Domain.Integraciones;

/// <summary>
/// Plataforma externa donde una Empresa/Centro acredita su documentación CAE
/// (Dokify, Nalanda, CTAIMA...) — Parte 2 del roadmap UX
/// (PLAN-EJECUCION-UX.md § (a), decisión registrada en
/// docs/business/DECISION_LOG.md 2026-08-05). Catálogo del producto: los
/// mismos ~24 proveedores aplican a cualquier tenant, nadie los renombra ni
/// tiene un catálogo paralelo — mismo criterio que los Roles o los enums de
/// dominio en docs/MULTITENANCY.md § 7.
///
/// Deliberadamente extiende <see cref="Entity"/>, no <see cref="EntidadBase"/>:
/// es un catálogo global sin dueño de tenant (como <c>Tenant</c> mismo), y su
/// ciclo de vida se modela con <see cref="Activo"/> (Arch/Opground se siembran
/// inactivos — su foco real no es CAE, ver la semilla), no con soft delete —
/// un proveedor "desactivado" sigue existiendo para no romper referencias de
/// canales ya migrados a él.
///
/// No es <c>CaeManager.Domain.Integraciones.ProveedorIntegracion</c> (el enum
/// ya existente para el conector de mensajería Email/WhatsApp) — nombre
/// distinto a propósito para no colisionar; ese enum ya documentaba que este
/// catálogo llegaría después y se mantuvo deliberadamente estrecho.
///
/// <see cref="Grupo"/> es solo para analítica (p. ej. "Twind (CTAIMA Group)"
/// agrupa Twind/CTAIMACAE legacy/e-coordina) — **nunca** lógica operativa:
/// son proveedores separados aunque compartan marca comercial, porque hay
/// empresas que hoy operan solo en uno de ellos.
/// </summary>
public class ProveedorPlataformaCae : Entity
{
    public const int LongitudMaximaCodigo = 50;
    public const int LongitudMaximaNombre = 150;
    public const int LongitudMaximaGrupo = 150;

    /// <summary>Slug estable (p. ej. "dokify") — identificador de código, nunca mostrado al usuario ni editable desde la UI.</summary>
    public string Codigo { get; private set; } = string.Empty;

    public string Nombre { get; private set; } = string.Empty;

    /// <summary>Grupo empresarial, solo para analítica (ver comentario de clase). Null cuando el proveedor no pertenece a ningún grupo conocido.</summary>
    public string? Grupo { get; private set; }

    /// <summary>
    /// Falso para proveedores sembrados sin foco CAE real (Arch, Opground) —
    /// no aparecen como sugerencia en los selectores, pero se conservan para
    /// no romper referencias si alguna vez se activan.
    /// </summary>
    public bool Activo { get; private set; } = true;

    private ProveedorPlataformaCae()
    {
    }

    public ProveedorPlataformaCae(string codigo, string nombre, string? grupo = null, bool activo = true)
    {
        EstablecerCodigo(codigo);
        EstablecerNombre(nombre);
        EstablecerGrupo(grupo);
        Activo = activo;
    }

    public void Activar() => Activo = true;

    public void Desactivar() => Activo = false;

    private void EstablecerCodigo(string codigo)
    {
        if (string.IsNullOrWhiteSpace(codigo))
            throw new ArgumentException("El código del proveedor es obligatorio.", nameof(codigo));

        var normalizado = codigo.Trim().ToLowerInvariant();

        if (normalizado.Length > LongitudMaximaCodigo)
            throw new ArgumentException($"El código no puede superar {LongitudMaximaCodigo} caracteres.", nameof(codigo));

        Codigo = normalizado;
    }

    private void EstablecerNombre(string nombre)
    {
        if (string.IsNullOrWhiteSpace(nombre))
            throw new ArgumentException("El nombre del proveedor es obligatorio.", nameof(nombre));

        var normalizado = nombre.Trim();

        if (normalizado.Length > LongitudMaximaNombre)
            throw new ArgumentException($"El nombre no puede superar {LongitudMaximaNombre} caracteres.", nameof(nombre));

        Nombre = normalizado;
    }

    private void EstablecerGrupo(string? grupo)
    {
        if (grupo?.Length > LongitudMaximaGrupo)
            throw new ArgumentException($"El grupo no puede superar {LongitudMaximaGrupo} caracteres.", nameof(grupo));

        Grupo = string.IsNullOrWhiteSpace(grupo) ? null : grupo.Trim();
    }
}
