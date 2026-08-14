using CaeManager.Domain.Common;

namespace CaeManager.Domain.Tenants;

/// <summary>
/// Organización que compra y utiliza Hydra — la frontera absoluta de
/// aislamiento del sistema (ver docs/MULTITENANCY.md § 1). No es lo mismo que
/// la entidad <c>Cliente</c> del dominio CAE (empresa a la que un Tenant
/// presta servicio de coordinación) — un Tenant puede tener muchos Clientes.
///
/// Deliberadamente extiende <see cref="Entity"/>, no <see cref="EntidadBase"/>
/// ni <see cref="EntidadConTenant"/>: un Tenant no pertenece a sí mismo (no
/// tiene <c>TenantId</c>), y su ciclo de vida operativo se modela con
/// <see cref="EstadoTenant"/> (Activo/Suspendido), no con soft delete —
/// suspender un tenant es una operación de negocio explícita, no un borrado.
/// </summary>
public class Tenant : Entity
{
    public const int LongitudMaximaNombre = 200;

    public string Nombre { get; private set; } = string.Empty;
    public EstadoTenant Estado { get; private set; }
    public DateTime CreadoEnUtc { get; private set; } = DateTime.UtcNow;

    /// <summary>
    /// El tenant desde el que se opera Hydra como producto: el único que
    /// puede recibir delegaciones de soporte sobre cualquier otro.
    ///
    /// Es un marcador explícito y no "el tenant con Id …0001" a propósito: ese
    /// Id es determinista y público en el código (hallazgo N-14 de
    /// INFORME-AUDITORIA-2.md), y hacer depender de adivinarlo un privilegio
    /// que cruza la frontera entre organizaciones sería exactamente el tipo de
    /// autorización implícita que este sistema evita en todo lo demás.
    /// </summary>
    public bool EsPlataforma { get; private set; }

    /// <summary>
    /// DDL-072 — capa de presentación, nunca rama de dominio. "Se declara, no
    /// se infiere" se refiere a no derivarlo del número de Empresas del
    /// tenant (DDL-072, tecnico/DESIGN_DECISION_LOG.md) — las tres altas
    /// reales (seed del tenant #1, CrearClienteDeleganteCommand,
    /// SegundoTenantSeeder/DelegacionDemoSeeder) lo pasan explícitamente. El
    /// valor por defecto de este parámetro es conveniencia para el resto de
    /// tests, que no ejercitan DDL-072 y no deberían tener que declarar un
    /// perfil que no les importa — mismo criterio que <c>esPlataforma</c>.
    /// </summary>
    public PerfilVocabularioTenant PerfilVocabulario { get; private set; }

    private Tenant()
    {
        // Requerido por EF Core.
    }

    public Tenant(string nombre, PerfilVocabularioTenant perfilVocabulario = PerfilVocabularioTenant.ClienteDirecto, bool esPlataforma = false)
    {
        EstablecerNombre(nombre);
        Estado = EstadoTenant.Activo;
        EsPlataforma = esPlataforma;
        PerfilVocabulario = perfilVocabulario;
    }

    /// <summary>
    /// Corrige el perfil declarado al alta. Operación de negocio explícita,
    /// no un setter público — mismo criterio que <see cref="MarcarComoPlataforma"/>:
    /// cambia cómo se lee toda la interfaz para este tenant.
    /// </summary>
    public void CambiarPerfilVocabulario(PerfilVocabularioTenant perfilVocabulario) =>
        PerfilVocabulario = perfilVocabulario;

    /// <summary>
    /// Marca este tenant como el de la plataforma. Operación aparte y no un
    /// setter porque conceder o retirar este marcador cambia quién puede
    /// entrar en los datos de todos los demás.
    /// </summary>
    public void MarcarComoPlataforma() => EsPlataforma = true;

    public void DejarDeSerPlataforma() => EsPlataforma = false;

    public void Suspender() => Estado = EstadoTenant.Suspendido;

    public void Reactivar() => Estado = EstadoTenant.Activo;

    public void RenombrarA(string nombre) => EstablecerNombre(nombre);

    private void EstablecerNombre(string nombre)
    {
        if (string.IsNullOrWhiteSpace(nombre))
            throw new ArgumentException("El nombre del tenant es obligatorio.", nameof(nombre));

        var normalizado = nombre.Trim();

        if (normalizado.Length > LongitudMaximaNombre)
            throw new ArgumentException(
                $"El nombre del tenant no puede superar {LongitudMaximaNombre} caracteres.", nameof(nombre));

        Nombre = normalizado;
    }
}
