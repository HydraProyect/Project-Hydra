namespace CaeManager.Web.Features.Inbound.Configuracion;

/// <summary>
/// Respuesta de los servicios de configuración de Inbound («No aplica», Reglas de aviso y Gobierno). «No disponible» no es un
/// error: es la pantalla sin backend, que no pinta datos ni inventa ninguno. <see cref="Mensaje"/> lleva el motivo cuando el
/// servidor rechaza una petición.
/// </summary>
public sealed record RespuestaConfiguracion<T>(bool Disponible, T? Valor, string? Mensaje = null)
{
    public static RespuestaConfiguracion<T> NoDisponible { get; } = new(false, default);

    public static RespuestaConfiguracion<T> Correcta(T valor) => new(true, valor);

    public static RespuestaConfiguracion<T> Rechazada(string mensaje) => new(true, default, mensaje);

    public bool Aceptada => Disponible && Mensaje is null && Valor is not null;
}

// ------------------------------------------------------------------------------------------------ «No aplica» (pantalla 19, B-16)

/// <summary>De dónde viene un requerimiento del contrato de acceso (columna «Origen» de la maqueta).</summary>
public enum OrigenRequerimiento
{
    Legal,
    Preventivo,
    Actividad,
    Centro,
    ExigenciaDelCliente,
}

/// <summary>Estado de un requerimiento tal como lo enseña la tabla del contrato de acceso.</summary>
public enum EstadoRequerimientoContrato
{
    Validado,
    PendienteDeValidar,
    PendienteDeSubir,
    Parcial,
    Incompleto,
    Rechazado,
}

/// <summary>
/// La excepción «No aplica» de un requerimiento. <see cref="TrabajadorId"/> nulo es «todo el contrato de acceso» (o todos los
/// Trabajadores); <see cref="Hasta"/> nulo es permanente. La marca el Gestor CAE; la autoriza el Tenant TCE
/// (<see cref="AutorizadoPor"/>, nombre y cargo).
/// </summary>
public sealed record ExcepcionNoAplica(
    Guid? TrabajadorId, string? TrabajadorNombre, DateOnly? Hasta, string Motivo, string AutorizadoPor);

/// <summary>Una fila de la tabla de requerimientos del contrato de acceso.</summary>
/// <param name="EsN1">Requisito de nivel N1: nunca admite «No aplica».</param>
public sealed record RequerimientoDeContrato(
    Guid Id, string Nombre, string Nota, string Entidad, OrigenRequerimiento Origen, string OrigenTexto, bool EsN1,
    EstadoRequerimientoContrato Estado, string Detalle, ExcepcionNoAplica? Excepcion)
{
    /// <summary>Un requisito legal, preventivo o N1 no admite «No aplica» (Q-9 y duda 7; plan de pantallas, pantalla 19).</summary>
    public bool AdmiteNoAplica => !EsN1 && Origen is not (OrigenRequerimiento.Legal or OrigenRequerimiento.Preventivo);
}

public sealed record TrabajadorDelContrato(Guid Id, string Nombre, bool TieneElDocumento);

/// <summary>Lo que el diálogo necesita saber del contrato: los requerimientos, los Trabajadores asignados y los nombres que enseña.</summary>
public sealed record RequerimientosDeContrato(
    string Empresa, string Centro, string TenantTce, IReadOnlyList<RequerimientoDeContrato> Requerimientos,
    IReadOnlyList<TrabajadorDelContrato> Trabajadores);

public sealed record SolicitudNoAplica(Guid ContratoId, Guid RequerimientoId, ExcepcionNoAplica Excepcion);

/// <summary>
/// Puerto de los requerimientos de un contrato de acceso y de su excepción «No aplica». Sin backend fusionado: los
/// requerimientos son B-11 y la excepción con respaldo y vencimiento, B-16. Cuando entren, se implementa este puerto sobre sus
/// Commands y Queries y se cambia el registro; la pantalla no se toca.
/// </summary>
public interface IServicioNoAplica
{
    Task<RespuestaConfiguracion<RequerimientosDeContrato>> ObtenerAsync(Guid contratoId, CancellationToken cancellationToken = default);

    Task<RespuestaConfiguracion<ExcepcionNoAplica>> GuardarAsync(SolicitudNoAplica solicitud, CancellationToken cancellationToken = default);
}

public sealed class ServicioNoAplicaSinBackend : IServicioNoAplica
{
    // shortcut: sin backend, cablear cuando entre B-11 (Requerimientos de Empresa del contrato, con su origen por capas).
    public Task<RespuestaConfiguracion<RequerimientosDeContrato>> ObtenerAsync(Guid contratoId, CancellationToken cancellationToken = default) =>
        Task.FromResult(RespuestaConfiguracion<RequerimientosDeContrato>.NoDisponible);

    // shortcut: sin backend, cablear cuando entre B-16 («No aplica» con respaldo y vencimiento; nunca sobre un requisito N1).
    public Task<RespuestaConfiguracion<ExcepcionNoAplica>> GuardarAsync(SolicitudNoAplica solicitud, CancellationToken cancellationToken = default) =>
        Task.FromResult(RespuestaConfiguracion<ExcepcionNoAplica>.NoDisponible);
}

// ------------------------------------------------------------------------------------------------ Reglas de aviso (pantalla 18, B-31)

public enum DisparadorAviso
{
    Vencimiento,
    EstadoDelRequerimiento,
    DocumentoNuevo,
    ResumenPeriodico,
}

/// <summary>
/// Una regla de aviso del Tenant TCE (CA-19). <see cref="Centros"/> vacío es «todos los Centros». Las reglas de serie
/// (<see cref="DeSerie"/>) las pone TALVEG: se pausan o se editan, no se eliminan.
/// </summary>
public sealed record ReglaAviso(
    Guid Id, string Nombre, bool DeSerie, DisparadorAviso Disparador, int DiasUmbral, int RepetirCadaDias,
    IReadOnlyList<string> Estados, string DiaResumen, bool ParaEmpresa, bool ParaGestor, bool PorCorreo, bool AlIniciarSesion,
    IReadOnlyList<string> Centros, bool Activa, string Asunto, string Cuerpo);

public sealed record ReglasDeAviso(string TenantTce, IReadOnlyList<ReglaAviso> Reglas, IReadOnlyList<string> Centros);

/// <summary>
/// Puerto de las Reglas de aviso. Sin backend fusionado: el motor de reglas con las de serie y la vista previa son B-31.
/// </summary>
public interface IServicioReglasAviso
{
    Task<RespuestaConfiguracion<ReglasDeAviso>> ObtenerAsync(CancellationToken cancellationToken = default);

    Task<RespuestaConfiguracion<ReglaAviso>> GuardarAsync(ReglaAviso regla, CancellationToken cancellationToken = default);

    Task<RespuestaConfiguracion<ReglaAviso>> CambiarEstadoAsync(Guid id, bool activa, CancellationToken cancellationToken = default);

    Task<RespuestaConfiguracion<Guid>> EliminarAsync(Guid id, CancellationToken cancellationToken = default);
}

public sealed class ServicioReglasAvisoSinBackend : IServicioReglasAviso
{
    // shortcut: sin backend, cablear cuando entre B-31 (contacto publicado y motor de reglas de aviso con las de serie; no hay B-xx
    // propio de CA-19 en el plan de backend: las Commands y Queries de la tarjeta 18 cuelgan de B-31).
    public Task<RespuestaConfiguracion<ReglasDeAviso>> ObtenerAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(RespuestaConfiguracion<ReglasDeAviso>.NoDisponible);

    // shortcut: sin backend, cablear cuando entre B-31 (CrearReglaAviso / EditarReglaAviso).
    public Task<RespuestaConfiguracion<ReglaAviso>> GuardarAsync(ReglaAviso regla, CancellationToken cancellationToken = default) =>
        Task.FromResult(RespuestaConfiguracion<ReglaAviso>.NoDisponible);

    // shortcut: sin backend, cablear cuando entre B-31 (DesactivarReglaAviso).
    public Task<RespuestaConfiguracion<ReglaAviso>> CambiarEstadoAsync(Guid id, bool activa, CancellationToken cancellationToken = default) =>
        Task.FromResult(RespuestaConfiguracion<ReglaAviso>.NoDisponible);

    // shortcut: sin backend, cablear cuando entre B-31 (EliminarReglaAviso, solo reglas propias).
    public Task<RespuestaConfiguracion<Guid>> EliminarAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(RespuestaConfiguracion<Guid>.NoDisponible);
}

// ------------------------------------------------------------------------------------------------ Gobierno de Inbound (pantalla 20, B-36)

public sealed record TenantTceConvalidable(Guid Id, string Nombre, string Nota);

public sealed record TipoRequisitoConvalidable(string Nombre, string Entidad);

public sealed record CambioGobierno(DateTime CuandoUtc, string Quien, string Cambio);

/// <summary>La configuración de convalidación entre Tenants TCE (CA-21) guardada.</summary>
public sealed record ConvalidacionConfigurada(bool Activa, IReadOnlyList<Guid> Tenants, IReadOnlyList<string> Tipos);

public sealed record GobiernoInbound(
    string TenantTce, string OperadorCae, string AsignacionDeOperacion, DateOnly? AsignacionDesde, DateOnly? AsignacionHasta,
    IReadOnlyList<string> OtrosTenantsGestionados, ConvalidacionConfigurada Convalidacion,
    IReadOnlyList<TenantTceConvalidable> TenantsConvalidables, IReadOnlyList<TipoRequisitoConvalidable> Tipos,
    IReadOnlyList<CambioGobierno> Registro);

/// <summary>
/// Puerto de «Gobierno de Inbound». Sin backend fusionado: la convalidación entre Tenants TCE, su registro de cambios y la
/// Asignación de Operación vigente son B-36. La validación automática por tipo de documento llega en la ola siguiente y
/// la pantalla la enseña como «Próximamente».
/// </summary>
public interface IServicioGobiernoInbound
{
    Task<RespuestaConfiguracion<GobiernoInbound>> ObtenerAsync(CancellationToken cancellationToken = default);

    Task<RespuestaConfiguracion<GobiernoInbound>> GuardarConvalidacionAsync(ConvalidacionConfigurada configuracion, CancellationToken cancellationToken = default);
}

public sealed class ServicioGobiernoInboundSinBackend : IServicioGobiernoInbound
{
    // shortcut: sin backend, cablear cuando entre B-36 (ObtenerGobiernoInboundQuery: Asignación de Operación vigente y registro de cambios).
    public Task<RespuestaConfiguracion<GobiernoInbound>> ObtenerAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(RespuestaConfiguracion<GobiernoInbound>.NoDisponible);

    // shortcut: sin backend, cablear cuando entre B-36 (ActivarConvalidacionCommand y DesactivarConvalidacionCommand, CA-21).
    public Task<RespuestaConfiguracion<GobiernoInbound>> GuardarConvalidacionAsync(ConvalidacionConfigurada configuracion, CancellationToken cancellationToken = default) =>
        Task.FromResult(RespuestaConfiguracion<GobiernoInbound>.NoDisponible);
}
