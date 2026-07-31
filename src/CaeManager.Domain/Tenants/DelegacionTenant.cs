using CaeManager.Domain.Common;

namespace CaeManager.Domain.Tenants;

/// <summary>
/// Delegación de acceso de una Consultora (Tenant sin datos operativos
/// propios) sobre un Cliente Delegante (otro Tenant, dueño de sus datos) —
/// ver ADR-004-delegacion-consultoras-cae.md § 5.3. Cada fila con
/// <see cref="Activa"/> es, en el vocabulario de negocio, un
/// <c>Delegated Workspace</c>.
///
/// Extiende <see cref="Entity"/>, no <see cref="EntidadConTenant"/>: es un
/// catálogo global de autorización, no un dato que pertenezca a un único
/// tenant — igual tratamiento que <see cref="Tenant"/> (sin
/// <c>HasQueryFilter</c>, ver CaeManagerDbContext). Nunca tiene FK hacia
/// Empresa/Centro/Trabajador/Documento — es la representación literal de
/// "delegación de acceso, no de propiedad" (ADR-004 § 2).
///
/// "Desactivar, nunca borrar": el Escenario 4 (internalización, ADR-004 § 2)
/// es <see cref="Desactivar"/>, no un soft delete — conserva el histórico de
/// qué Consultora operó sobre qué tenant y cuándo.
/// </summary>
public class DelegacionTenant : Entity
{
    public Guid TenantConsultoraId { get; private set; }
    public Guid TenantClienteId { get; private set; }
    public bool Activa { get; private set; }
    public DateTime CreadoEnUtc { get; private set; } = DateTime.UtcNow;

    /// <summary>Comercial (una consultora presta servicio) o Soporte (Hydra entra a revisar una incidencia).</summary>
    public PropositoDelegacion Proposito { get; private set; }

    /// <summary>
    /// Por qué se activó. Obligatorio en las de Soporte: es lo que permite
    /// responder a un cliente "entramos el día X por la incidencia Y".
    /// </summary>
    public string? MotivoActivacion { get; private set; }

    /// <summary>
    /// Cuándo deja de valer sola. Null = sin caducidad, que es lo normal en
    /// una delegación comercial y lo que nunca debería tener una de soporte.
    /// </summary>
    public DateTime? ExpiraEnUtc { get; private set; }

    public DateTime? ActivadaEnUtc { get; private set; }

    private DelegacionTenant()
    {
        // Requerido por EF Core.
    }

    /// <summary>
    /// Una delegación de Soporte nace <b>inactiva</b>: se aprovisiona al dar
    /// de alta el tenant para no tener que crear nada cuando entra una queja,
    /// pero conceder el acceso sigue siendo un acto explícito con motivo y
    /// ventana. Tener acceso permanente a la cartera entera es justo lo que
    /// esto evita — si la cuenta de soporte se ve comprometida, no abre
    /// ninguna puerta por sí sola.
    /// </summary>
    public static DelegacionTenant ParaSoporte(Guid tenantPlataformaId, Guid tenantClienteId)
    {
        var delegacion = new DelegacionTenant(tenantPlataformaId, tenantClienteId)
        {
            Proposito = PropositoDelegacion.Soporte
        };

        delegacion.Activa = false;

        return delegacion;
    }

    public DelegacionTenant(Guid tenantConsultoraId, Guid tenantClienteId)
    {
        if (tenantConsultoraId == Guid.Empty)
            throw new ArgumentException("La delegación debe tener una Consultora.", nameof(tenantConsultoraId));
        if (tenantClienteId == Guid.Empty)
            throw new ArgumentException("La delegación debe tener un Cliente Delegante.", nameof(tenantClienteId));
        if (tenantConsultoraId == tenantClienteId)
            throw new ArgumentException("Un tenant no puede delegarse acceso a sí mismo.", nameof(tenantClienteId));

        TenantConsultoraId = tenantConsultoraId;
        TenantClienteId = tenantClienteId;
        Activa = true;
    }

    public void Desactivar()
    {
        Activa = false;
        ExpiraEnUtc = null;
        MotivoActivacion = null;
    }

    /// <summary>
    /// Reactiva una delegación comercial. <b>No sirve para una de soporte</b>:
    /// esa se abre con <see cref="ActivarParaSoporte"/>, que exige motivo y
    /// ventana. Sin este bloqueo, la reactivación normal sería una puerta
    /// trasera que concede acceso permanente a los datos de un cliente
    /// saltándose el control entero.
    /// </summary>
    public void Reactivar()
    {
        if (Proposito is PropositoDelegacion.Soporte)
            throw new InvalidOperationException(
                "Una delegación de soporte se abre con motivo y ventana, no reactivándola sin más.");

        Activa = true;
    }

    /// <summary>
    /// Abre una ventana de acceso de soporte: exige decir por qué y hasta
    /// cuándo. Las dos cosas son el precio de entrar en datos de los que
    /// responde otro.
    /// </summary>
    public void ActivarParaSoporte(string motivo, DateTime expiraEnUtc, DateTime ahoraUtc)
    {
        if (Proposito is not PropositoDelegacion.Soporte)
            throw new InvalidOperationException(
                "Solo una delegación de soporte se activa con motivo y ventana; una comercial se reactiva sin más.");

        if (string.IsNullOrWhiteSpace(motivo))
            throw new ArgumentException(
                "Entrar en los datos de un cliente exige dejar dicho por qué.", nameof(motivo));

        if (expiraEnUtc <= ahoraUtc)
            throw new ArgumentException(
                "La ventana de acceso tiene que terminar después de empezar.", nameof(expiraEnUtc));

        Activa = true;
        MotivoActivacion = motivo.Trim();
        ExpiraEnUtc = expiraEnUtc;
        ActivadaEnUtc = ahoraUtc;
    }

    /// <summary>
    /// Si la delegación vale a día de hoy. Una activa pero caducada no vale:
    /// la comprobación de caducidad va aquí, en el dominio, y no solo en la
    /// consulta que la lee, para que ninguna vía de acceso pueda saltársela.
    /// </summary>
    public bool EstaVigente(DateTime ahoraUtc) =>
        Activa && (ExpiraEnUtc is null || ahoraUtc < ExpiraEnUtc.Value);
}
