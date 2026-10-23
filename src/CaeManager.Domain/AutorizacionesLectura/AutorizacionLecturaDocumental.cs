using System.Linq.Expressions;
using CaeManager.Domain.Common;

namespace CaeManager.Domain.AutorizacionesLectura;

/// <summary>
/// Autorización expresa de lectura documental (Inbound M1, PR 2): el acto con el que el Tenant de la Empresa
/// contratista —el Tenant propietario de sus Documentos originales— deja que el Tenant del Cliente empresarial lea en
/// solo lectura, para un contrato de acceso concreto, los Documentos de los Trabajadores que asigna a ese contrato y,
/// si lo marca, los de su Empresa propia. Vive en el Tenant de la Empresa contratista, con <c>TenantId</c> estándar
/// y RLS por Tenant: es él quien la concede y quien la revoca.
///
/// <para>
/// <b>Esta entidad sola no da lectura.</b> Es la mitad del Tenant de la Empresa contratista en un apretón de dos
/// actos: la otra mitad es <c>ContratoAcceso.TenantContratistaId</c> en el Tenant del Cliente empresarial. La lectura
/// entre Tenants (las funciones <c>app_inbound_*</c> de la migración <c>AnadeLecturaInboundEntreTenants</c>) exige a la
/// vez esta autorización vigente y el contrato de acceso vigente y vinculado, y deja cada lectura en
/// <see cref="AccesoLecturaInbound"/>. Revocar corta esa lectura desde ese instante; las copias que el Tenant del Cliente
/// empresarial ya guardó se quedan.
/// </para>
///
/// <para>
/// <see cref="TenantDestinatarioId"/> y <see cref="ContratoAccesoId"/> son identificadores de otro Tenant: no hay FK
/// posible hacia ellos ni este Tenant puede comprobar que existen (RLS no se lo deja ver). Cómo se conocen los dos
/// Tenants sus identificadores es la decisión DN-1, abierta.
/// </para>
///
/// <para>
/// <b>Solo añade (append-only), como <see cref="Tenants.EncargoAdministracion"/>.</b> Ningún método cambia lo que se
/// concedió (destinatario, contrato, alcance y vigencia). La única transición es <see cref="Revocar"/>, una sola vez y
/// definitiva: una autorización revocada no se reactiva, y volver a autorizar es conceder otra fila. Las filas de
/// Trabajadores (<see cref="AutorizacionLecturaTrabajador"/>) solo cambian mientras no esté revocada; después quedan
/// como constancia de qué se autorizó. La base lo repite con disparadores, para cualquier rol.
/// </para>
///
/// <para>
/// No hereda de <see cref="EntidadBase"/>: una autorización no se da de baja con borrado lógico, se revoca. Sí es
/// <see cref="IVersionable"/>: revocar y cambiar sus Trabajadores son escrituras concurrentes sobre la misma
/// autorización, y por eso los cambios de Trabajadores, que viven en otra tabla, renuevan también su versión.
/// </para>
/// </summary>
public class AutorizacionLecturaDocumental : EntidadConTenant, IVersionable
{
    /// <summary>El Tenant del Cliente empresarial que podrá leer. Nunca el propio Tenant.</summary>
    public Guid TenantDestinatarioId { get; private set; }

    /// <summary>El contrato de acceso del Tenant destinatario al que se liga la lectura.</summary>
    public Guid ContratoAccesoId { get; private set; }

    /// <summary>Si la lectura alcanza también los Documentos de la Empresa propia del Tenant de la Empresa contratista.</summary>
    public bool IncluyeDocumentosEmpresa { get; private set; }

    public DateOnly VigenciaDesde { get; private set; }

    /// <summary>Último día autorizado; null = sin fecha de fin.</summary>
    public DateOnly? VigenciaHasta { get; private set; }

    /// <summary>Quien la concedió. Guid suelto hacia <c>ApplicationUser</c>.</summary>
    public Guid ConcedidaPorUsuarioId { get; private set; }

    public DateTime ConcedidaEnUtc { get; private set; }

    /// <summary>Quien la revocó. Se escribe una sola vez, junto con <see cref="RevocadaEnUtc"/>.</summary>
    public Guid? RevocadaPorUsuarioId { get; private set; }

    public DateTime? RevocadaEnUtc { get; private set; }

    /// <inheritdoc cref="IVersionable" />
    public Guid Version { get; private set; } = Guid.NewGuid();

    public bool EstaRevocada => RevocadaEnUtc is not null;

    private AutorizacionLecturaDocumental()
    {
        // Requerido por EF Core.
    }

    /// <summary>
    /// Concede la autorización. Que el destinatario no sea el propio Tenant no se puede comprobar aquí:
    /// <see cref="EntidadConTenant.TenantId"/> lo sella el interceptor al guardar. Lo comprueban el comando, contra el
    /// Tenant activo, y el CHECK <c>CK_AutorizacionesLectura_DestinatarioAjeno</c> en la base. Quién puede concederla
    /// lo decide Application.
    /// </summary>
    public static AutorizacionLecturaDocumental Conceder(
        Guid tenantDestinatarioId,
        Guid contratoAccesoId,
        bool incluyeDocumentosEmpresa,
        DateOnly vigenciaDesde,
        DateOnly? vigenciaHasta,
        Guid concedidaPorUsuarioId,
        DateTime ahoraUtc)
    {
        if (tenantDestinatarioId == Guid.Empty)
            throw new ArgumentException("La autorización debe tener un Tenant destinatario.", nameof(tenantDestinatarioId));
        if (contratoAccesoId == Guid.Empty)
            throw new ArgumentException("La autorización debe ligarse a un contrato de acceso.", nameof(contratoAccesoId));
        if (concedidaPorUsuarioId == Guid.Empty)
            throw new ArgumentException("La autorización debe tener quien la concede.", nameof(concedidaPorUsuarioId));
        if (vigenciaHasta is not null && vigenciaHasta < vigenciaDesde)
            throw new ArgumentException("La vigencia no puede terminar antes de empezar.", nameof(vigenciaHasta));

        return new AutorizacionLecturaDocumental
        {
            TenantDestinatarioId = tenantDestinatarioId,
            ContratoAccesoId = contratoAccesoId,
            IncluyeDocumentosEmpresa = incluyeDocumentosEmpresa,
            VigenciaDesde = vigenciaDesde,
            VigenciaHasta = vigenciaHasta,
            ConcedidaPorUsuarioId = concedidaPorUsuarioId,
            ConcedidaEnUtc = ahoraUtc,
        };
    }

    /// <summary>
    /// Asigna un Trabajador de la Empresa contratista a esta autorización. Quien llama comprueba que el Trabajador
    /// existe en el Tenant (la FK compuesta lo repite en la base) y que no estaba ya asignado.
    /// </summary>
    public AutorizacionLecturaTrabajador AsignarTrabajador(Guid trabajadorId)
    {
        ExigirNoRevocada();
        if (trabajadorId == Guid.Empty)
            throw new ArgumentException("El Trabajador es obligatorio.", nameof(trabajadorId));

        RenovarVersion();
        return new AutorizacionLecturaTrabajador(Id, trabajadorId);
    }

    /// <summary>Anota que se retira un Trabajador: solo mientras no esté revocada.</summary>
    public void RetirarTrabajador(AutorizacionLecturaTrabajador trabajador)
    {
        ArgumentNullException.ThrowIfNull(trabajador);
        ExigirNoRevocada();
        if (trabajador.AutorizacionId != Id)
            throw new ArgumentException("El Trabajador no está asignado a esta autorización.", nameof(trabajador));

        RenovarVersion();
    }

    /// <summary>Revoca la autorización. Una sola vez y para siempre: para volver a autorizar se concede otra.</summary>
    public void Revocar(Guid revocadaPorUsuarioId, DateTime ahoraUtc)
    {
        if (revocadaPorUsuarioId == Guid.Empty)
            throw new ArgumentException("La revocación debe tener un autor.", nameof(revocadaPorUsuarioId));
        if (EstaRevocada)
            throw new InvalidOperationException("La autorización de lectura ya estaba revocada.");

        RevocadaPorUsuarioId = revocadaPorUsuarioId;
        RevocadaEnUtc = ahoraUtc;
    }

    /// <summary>
    /// Sin revocar y dentro de su vigencia; el primer y el último día cuentan como vigentes. Es la versión en memoria
    /// de <see cref="EstaVigenteEn"/>: un test de dominio comprueba que las dos coinciden en cada borde. Que la lectura
    /// surta efecto exige además el contrato de acceso vigente y vinculado: lo comprueban, con este mismo criterio escrito
    /// en SQL, las funciones <c>app_inbound_*</c> de la migración <c>AnadeLecturaInboundEntreTenants</c>.
    /// </summary>
    public bool VigenteEn(DateOnly fecha) =>
        RevocadaEnUtc == null
        && VigenciaDesde <= fecha
        && (VigenciaHasta == null || fecha <= VigenciaHasta);

    /// <summary>El criterio de <see cref="VigenteEn"/> como expresión traducible a SQL.</summary>
    public static Expression<Func<AutorizacionLecturaDocumental, bool>> EstaVigenteEn(DateOnly fecha) =>
        a => a.RevocadaEnUtc == null
             && a.VigenciaDesde <= fecha
             && (a.VigenciaHasta == null || fecha <= a.VigenciaHasta);

    private void ExigirNoRevocada()
    {
        if (EstaRevocada)
            throw new InvalidOperationException("La autorización de lectura está revocada: sus Trabajadores ya no cambian.");
    }

    // Mismo motivo que EntidadBase.RenovarVersion: el cambio vive en la tabla de Trabajadores, y sin tocar la raíz el
    // token de concurrencia no entraría en ningún WHERE; así un cambio de Trabajadores y una revocación simultáneos
    // no pasan los dos.
    private void RenovarVersion() => Version = Guid.NewGuid();
}
