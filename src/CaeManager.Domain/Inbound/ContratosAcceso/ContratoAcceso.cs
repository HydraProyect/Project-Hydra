using System.Linq.Expressions;
using CaeManager.Domain.Common;

namespace CaeManager.Domain.Inbound.ContratosAcceso;

/// <summary>
/// Contrato de acceso (Inbound M1): la relación Empresa contratista × Centro de trabajo × periodo que da a esa
/// Empresa contratista acceso al Centro. Vive en el Tenant propietario del Cliente empresarial —el titular del
/// Centro—, no en el de la Empresa contratista: <see cref="EmpresaContratistaId"/> es la Empresa contraparte local
/// con la que ese Tenant representa a la contratista (modelo egocéntrico por Tenant), nunca el Tenant de la
/// contratista.
///
/// <para>
/// <b>Los requisitos no se guardan aquí.</b> Lo que se exige a la Empresa contratista mientras el contrato está
/// vigente son las filas <c>TipoDocumentoCentro</c> del Centro (opción 1 de DI-04): un contrato no copia ni
/// congela requisitos, y cambiar los del Centro cambia lo que se pide a todos sus contratos vigentes. Por eso
/// tampoco tiene nombre propio: se identifica por sus tres coordenadas y su periodo.
/// </para>
///
/// <para>
/// <see cref="ClienteEmpresarialId"/> es la Empresa titular del Centro y tiene que coincidir con <c>Centro.ClienteId</c>: la
/// FK compuesta (TenantId, CentroId, ClienteEmpresarialId) contra la clave alternativa (TenantId, Id, ClienteId) de Centro lo hace irrepresentable en la
/// base, igual que en <see cref="Proyectos.Proyecto"/>. Que la Empresa contratista no sea el propio Cliente
/// empresarial es invariante de dominio (<see cref="Crear"/>). Se llama <c>ClienteEmpresarialId</c> y no
/// <c>ClienteId</c> como las tablas nuevas de Plataforma CAE y de tolerancias por Cliente empresarial:
/// <c>ClienteIdNoSeExtiendeTests</c> congela la columna legacy <c>ClienteId</c> mientras la decisión D1 siga abierta.
/// </para>
///
/// <para>
/// <b>Estado (B-05).</b> El ciclo de vida es <see cref="EstadoContratoAcceso"/>, con sus transiciones aquí. Finalizar
/// guarda además <see cref="FechaCierreReal"/>, el cierre real, que puede adelantarse a <see cref="FechaFin"/> (el fin
/// contratado; null = indefinido). Un contrato Finalizado no se reabre: D-21 (DECISION_LOG 2026-11-14) retira
/// <c>Reabrir</c>. El criterio de vigencia en un día vive en <see cref="EstaVigenteEn"/>, el mismo que traducen a SQL las
/// consultas y que repite la auxiliar <c>app_inbound_tenant_contratista_del_contrato</c> de la lectura entre Tenants.
/// </para>
///
/// <para>
/// <b>Solapes permitidos.</b> Dos contratos de la misma Empresa contratista en el mismo Centro pueden solaparse en el
/// tiempo: no hay regla de unicidad ni índice único. D-20 (DECISION_LOG 2026-11-14) lo fija así para DN-6: los contratos
/// solapados no se prohíben, como en Proyecto (varios Proyectos abiertos en un Centro).
/// </para>
/// </summary>
public class ContratoAcceso : EntidadBase
{
    public const int LongitudMaximaNotas = 1000;

    private readonly List<HistorialVigenciaContrato> _historialVigencia = [];

    public Guid ClienteEmpresarialId { get; private set; }
    public Guid CentroId { get; private set; }
    public Guid EmpresaContratistaId { get; private set; }
    public DateOnly FechaInicio { get; private set; }
    public DateOnly? FechaFin { get; private set; }
    public DateOnly? FechaCierreReal { get; private set; }
    public string? Notas { get; private set; }

    public EstadoContratoAcceso Estado { get; private set; }
    public TipoContratoAcceso? Tipo { get; private set; }
    public PropiaActividadContrato? PropiaActividad { get; private set; }
    public int? NivelMaxSubcontrata { get; private set; }
    public int? CupoTrabajadores { get; private set; }
    public int? CupoSubcontratas { get; private set; }
    public string? MotivoCupo { get; private set; }

    /// <summary>
    /// El Tenant de la Empresa contratista (Inbound M1, PR 2): el otro extremo del contrato, donde viven los Documentos
    /// originales de la Empresa contratista y donde ella concede su <c>AutorizacionLecturaDocumental</c> para este
    /// contrato. Null mientras no se haya vinculado. No es <see cref="EmpresaContratistaId"/>: aquel es la Empresa
    /// contraparte local de este Tenant; este es otro Tenant, y no hay FK posible hacia él. Ver
    /// <see cref="VincularTenantContratista"/>.
    /// </summary>
    public Guid? TenantContratistaId { get; private set; }

    /// <summary>
    /// Los cambios del fin contratado, solo inserción (<see cref="HistorialVigenciaContrato"/>). No lo carga
    /// <c>ObtenerPorIdAsync</c>: aquí solo se añaden filas nuevas, que EF inserta al guardar.
    /// </summary>
    public IReadOnlyList<HistorialVigenciaContrato> HistorialVigencia => _historialVigencia.AsReadOnly();

    /// <summary>Sin cierre real y en un estado no final (pendiente de confirmar, activo o suspendido).</summary>
    public bool EstaAbierto =>
        FechaCierreReal is null
        && Estado is EstadoContratoAcceso.PendienteDeConfirmar or EstadoContratoAcceso.Activo or EstadoContratoAcceso.Suspendido;

    public CondicionesContratoAcceso Condiciones =>
        new(Tipo, PropiaActividad, NivelMaxSubcontrata, CupoTrabajadores, CupoSubcontratas, MotivoCupo);

    private ContratoAcceso()
    {
    }

    /// <param name="pendienteDeConfirmar">
    /// El alta nace <see cref="EstadoContratoAcceso.PendienteDeConfirmar"/> cuando el Tenant TEC aún no confirmó a este
    /// Tenant TCE, y <see cref="EstadoContratoAcceso.Activo"/> si ya lo hizo (plan backend Inbound § 2.2). Quién lo sabe
    /// es la confirmación por Tenant TCE (B-09): hasta entonces la aplicación da de alta en Activo, como hacía antes de
    /// B-05, con la autorización por contrato de M1 como acto del Tenant TEC.
    /// </param>
    public static ContratoAcceso Crear(
        Guid clienteEmpresarialId, Guid centroId, Guid empresaContratistaId, DateOnly fechaInicio, DateOnly? fechaFin, string? notas,
        CondicionesContratoAcceso? condiciones = null, bool pendienteDeConfirmar = false)
    {
        if (clienteEmpresarialId == Guid.Empty)
            throw new ArgumentException("El contrato de acceso debe pertenecer a un Cliente empresarial.", nameof(clienteEmpresarialId));
        if (centroId == Guid.Empty)
            throw new ArgumentException("El contrato de acceso debe pertenecer a un Centro de trabajo.", nameof(centroId));
        if (empresaContratistaId == Guid.Empty)
            throw new ArgumentException("El contrato de acceso debe tener una Empresa contratista.", nameof(empresaContratistaId));
        if (empresaContratistaId == clienteEmpresarialId)
            throw new ArgumentException(
                "La Empresa contratista no puede ser el propio Cliente empresarial titular del Centro.", nameof(empresaContratistaId));
        ExigirFinNoAnteriorAlInicio(fechaInicio, fechaFin);

        var contrato = new ContratoAcceso
        {
            ClienteEmpresarialId = clienteEmpresarialId,
            CentroId = centroId,
            EmpresaContratistaId = empresaContratistaId,
            FechaInicio = fechaInicio,
            FechaFin = fechaFin,
            Estado = pendienteDeConfirmar ? EstadoContratoAcceso.PendienteDeConfirmar : EstadoContratoAcceso.Activo
        };
        contrato.EstablecerNotas(notas);
        contrato.FijarCondiciones(condiciones ?? CondicionesContratoAcceso.SinFijar);
        return contrato;
    }

    /// <summary>
    /// Las coordenadas (Cliente empresarial, Centro, Empresa contratista) y el inicio no cambian: otro periodo u otra
    /// Empresa es otro contrato. Si cambia el fin contratado, queda en <see cref="HistorialVigencia"/> como
    /// <see cref="OrigenCambioVigencia.Edicion"/>.
    /// </summary>
    public void Actualizar(DateOnly? fechaFin, string? notas, Guid usuarioId, DateTime ahoraUtc)
    {
        ExigirFinNoAnteriorAlInicio(FechaInicio, fechaFin);
        if (notas?.Length > LongitudMaximaNotas)
            throw new ArgumentException($"Las notas no pueden superar {LongitudMaximaNotas} caracteres.", nameof(notas));

        if (fechaFin != FechaFin)
            CambiarFin(fechaFin, OrigenCambioVigencia.Edicion, usuarioId, ahoraUtc);
        EstablecerNotas(notas);
    }

    /// <summary>Tipo, propia actividad, nivel máximo de subcontrata y cupos, de una vez (ver <see cref="CondicionesContratoAcceso"/>).</summary>
    public void FijarCondiciones(CondicionesContratoAcceso condiciones)
    {
        ArgumentNullException.ThrowIfNull(condiciones);

        Tipo = condiciones.Tipo;
        PropiaActividad = condiciones.PropiaActividad;
        NivelMaxSubcontrata = condiciones.NivelMaxSubcontrata;
        CupoTrabajadores = condiciones.CupoTrabajadores;
        CupoSubcontratas = condiciones.CupoSubcontratas;
        MotivoCupo = condiciones.MotivoCupo;
    }

    /// <summary>
    /// Renovar amplía la vigencia sin cambiar de estado (duda 3): solo un contrato Activo o Suspendido, con fin contratado,
    /// y a un fin posterior. Queda en <see cref="HistorialVigencia"/> como <see cref="OrigenCambioVigencia.Renovacion"/>.
    /// </summary>
    public void Renovar(DateOnly nuevaFechaFin, Guid usuarioId, DateTime ahoraUtc)
    {
        ExigirEstado("renovar", EstadoContratoAcceso.Activo, EstadoContratoAcceso.Suspendido);
        if (FechaFin is not { } finActual)
            throw new InvalidOperationException("Un contrato de acceso sin fecha de fin no se renueva: ya es indefinido.");
        if (nuevaFechaFin <= finActual)
            throw new ArgumentException("Renovar amplía la vigencia: el fin nuevo tiene que ser posterior al actual.", nameof(nuevaFechaFin));

        CambiarFin(nuevaFechaFin, OrigenCambioVigencia.Renovacion, usuarioId, ahoraUtc);
    }

    /// <summary>El Tenant TEC confirma el contrato (B-09 lo conecta): Pendiente de confirmar → Activo.</summary>
    public void ConfirmarPorTenantContratista()
    {
        ExigirEstado("confirmar", EstadoContratoAcceso.PendienteDeConfirmar);
        Estado = EstadoContratoAcceso.Activo;
    }

    /// <summary>El Tenant TEC rechaza el contrato (B-09 lo conecta): Pendiente de confirmar → Rechazado, final.</summary>
    public void RechazarPorTenantContratista()
    {
        ExigirEstado("rechazar", EstadoContratoAcceso.PendienteDeConfirmar);
        Estado = EstadoContratoAcceso.Rechazado;
    }

    /// <summary>Activo → Suspendido (el comando y sus avisos entran con B-18).</summary>
    public void Suspender()
    {
        ExigirEstado("suspender", EstadoContratoAcceso.Activo);
        Estado = EstadoContratoAcceso.Suspendido;
    }

    /// <summary>Suspendido → Activo (el comando entra con B-18).</summary>
    public void Reanudar()
    {
        ExigirEstado("reanudar", EstadoContratoAcceso.Suspendido);
        Estado = EstadoContratoAcceso.Activo;
    }

    /// <summary>
    /// Activo o Suspendido → Finalizado, final, con el cierre real en <paramref name="fechaCierre"/>. Lo usa hoy
    /// <c>CerrarContratoAccesoCommand</c>; la cascada a asignaciones y subcontratas entra con B-18.
    /// </summary>
    public void Finalizar(DateOnly fechaCierre)
    {
        if (FechaCierreReal is not null)
            throw new InvalidOperationException("El contrato de acceso ya está cerrado.");
        ExigirEstado("finalizar", EstadoContratoAcceso.Activo, EstadoContratoAcceso.Suspendido);
        if (fechaCierre < FechaInicio)
            throw new ArgumentException("La fecha de cierre no puede ser anterior a la de inicio.", nameof(fechaCierre));

        FechaCierreReal = fechaCierre;
        Estado = EstadoContratoAcceso.Finalizado;
    }

    /// <summary>
    /// D-21 (DECISION_LOG 2026-11-14): «Cancelado» es la baja antes de empezar, sin actividad. Solo desde Pendiente de
    /// confirmar o Activo, con <paramref name="hoy"/> anterior al inicio y sin actividad (la mide quien llama: hoy, las
    /// copias de evidencia del contrato). Final: no da acceso nunca, tampoco desde su fecha de inicio.
    /// </summary>
    public void Cancelar(DateOnly hoy, bool tieneActividad)
    {
        ExigirEstado("cancelar", EstadoContratoAcceso.PendienteDeConfirmar, EstadoContratoAcceso.Activo);
        if (hoy >= FechaInicio)
            throw new InvalidOperationException("Un contrato de acceso que ya empezó no se cancela: se finaliza.");
        if (tieneActividad)
            throw new InvalidOperationException("Un contrato de acceso con actividad no se cancela.");

        Estado = EstadoContratoAcceso.Cancelado;
    }

    /// <summary>
    /// Vincula el contrato con el Tenant de la Empresa contratista. <b>Una sola vez</b>: un vínculo equivocado se
    /// corrige cerrando este contrato y creando otro, nunca cambiándolo. Cambiar el vínculo después de que el Tenant
    /// de la Empresa contratista haya concedido su autorización de lectura redirigiría en silencio esa lectura hacia
    /// los Documentos de otro Tenant, con la autorización que dio un tercero. La base lo repite con el trigger
    /// <c>TR_ContratosAcceso_TenantContratistaUnaVez</c>: escrito el vínculo, no cambia ni se vacía para ningún rol.
    ///
    /// <para>
    /// Tampoco puede ser el propio Tenant del contrato. <see cref="EntidadConTenant.TenantId"/> solo se conoce aquí
    /// cuando el contrato ya se guardó (lo sella el interceptor al guardar): para un contrato recién creado la
    /// comprobación la repiten <c>VincularTenantContratistaCommand</c>, contra el Tenant activo, y el CHECK
    /// <c>CK_ContratosAcceso_TenantContratistaAjeno</c> en la base.
    /// </para>
    /// </summary>
    public void VincularTenantContratista(Guid tenantContratistaId)
    {
        if (tenantContratistaId == Guid.Empty)
            throw new ArgumentException("El Tenant de la Empresa contratista es obligatorio.", nameof(tenantContratistaId));
        if (tenantContratistaId == TenantId)
            throw new ArgumentException(
                "El Tenant de la Empresa contratista no puede ser el propio Tenant del contrato.", nameof(tenantContratistaId));
        if (TenantContratistaId is not null)
            throw new InvalidOperationException(
                "El contrato de acceso ya está vinculado con el Tenant de la Empresa contratista: para corregirlo, cierra este contrato y crea otro.");

        TenantContratistaId = tenantContratistaId;
    }

    /// <summary>
    /// ¿Da acceso el contrato en <paramref name="fecha"/>? No eliminado, en estado Activo o Finalizado, ya empezado, sin
    /// pasar su fin contratado y sin pasar su cierre real; el día de inicio, el de fin y el de cierre cuentan como
    /// vigentes. Pendiente de confirmar, Rechazado y Cancelado no dan acceso nunca (X-1: hasta confirmar, el Tenant TCE no
    /// recibe nada). Suspendido tampoco, a falta de decidir qué deja ver una suspensión (B-18): falla cerrado. Es la
    /// versión en memoria de <see cref="EstaVigenteEn"/>: un test de dominio comprueba que las dos coinciden en cada borde.
    /// </summary>
    public bool VigenteEn(DateOnly fecha) =>
        !EstaEliminado
        && (Estado == EstadoContratoAcceso.Activo || Estado == EstadoContratoAcceso.Finalizado)
        && FechaInicio <= fecha
        && (FechaFin == null || fecha <= FechaFin)
        && (FechaCierreReal == null || fecha <= FechaCierreReal);

    /// <summary>
    /// El criterio de <see cref="VigenteEn"/> como expresión traducible a SQL, para las consultas que filtran contratos
    /// vigentes en un día sin cargarlos.
    /// </summary>
    public static Expression<Func<ContratoAcceso, bool>> EstaVigenteEn(DateOnly fecha) =>
        c => !c.EstaEliminado
             && (c.Estado == EstadoContratoAcceso.Activo || c.Estado == EstadoContratoAcceso.Finalizado)
             && c.FechaInicio <= fecha
             && (c.FechaFin == null || fecha <= c.FechaFin)
             && (c.FechaCierreReal == null || fecha <= c.FechaCierreReal);

    private void CambiarFin(DateOnly? fechaFin, OrigenCambioVigencia origen, Guid usuarioId, DateTime ahoraUtc)
    {
        _historialVigencia.Add(new HistorialVigenciaContrato(Id, FechaFin, fechaFin, origen, usuarioId, ahoraUtc));
        FechaFin = fechaFin;
    }

    private void ExigirEstado(string accion, params EstadoContratoAcceso[] permitidos)
    {
        if (!permitidos.Contains(Estado))
            throw new InvalidOperationException($"Un contrato de acceso en estado {Estado} no se puede {accion}.");
    }

    private static void ExigirFinNoAnteriorAlInicio(DateOnly fechaInicio, DateOnly? fechaFin)
    {
        if (fechaFin is not null && fechaFin < fechaInicio)
            throw new ArgumentException("La fecha de fin no puede ser anterior a la de inicio.", nameof(fechaFin));
    }

    private void EstablecerNotas(string? notas)
    {
        if (notas?.Length > LongitudMaximaNotas)
            throw new ArgumentException($"Las notas no pueden superar {LongitudMaximaNotas} caracteres.", nameof(notas));

        Notas = notas;
    }
}
