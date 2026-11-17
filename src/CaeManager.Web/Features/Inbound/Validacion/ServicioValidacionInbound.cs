using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CaeManager.Web.Features.Inbound.Validacion;

/// <summary>
/// Puerto de las pantallas «Documentos por validar» y «Validación» (Tenant TCE). En la demo mínima
/// (<see cref="ServicioValidacionInboundConBackend"/>) tienen backend la cola, Validar y Rechazar uno a uno; siguen sin él
/// el veredicto previo con su Deshacer de 10 s y Marcar incompleto (B-22), el veredicto del sistema (B-24), la propuesta de
/// la IA (B-26) y el lote con muestreo (B-27). Cuando entren, se completa ese servicio sobre sus Commands y Queries; las
/// pantallas no se tocan.
/// </summary>
public interface IServicioValidacionInbound
{
    /// <summary>La cola pendiente del Gestor CAE, con los datos que la pantalla filtra y agrupa en memoria.</summary>
    Task<RespuestaValidacion<ColaValidacion>> ObtenerColaAsync(CancellationToken cancellationToken = default);

    /// <summary>El documento abierto con su lectura previa, su historial y su propuesta.</summary>
    Task<RespuestaValidacion<DocumentoAValidar>> ObtenerDocumentoAsync(Guid id, CancellationToken cancellationToken = default);

    Task<RespuestaValidacion<VeredictoRegistrado>> ValidarAsync(SolicitudValidar solicitud, CancellationToken cancellationToken = default);

    Task<RespuestaValidacion<VeredictoRegistrado>> RechazarAsync(SolicitudRechazar solicitud, CancellationToken cancellationToken = default);

    Task<RespuestaValidacion<VeredictoRegistrado>> MarcarIncompletoAsync(SolicitudMarcarIncompleto solicitud, CancellationToken cancellationToken = default);

    /// <summary>Deshace un veredicto dentro de los 10 s de plazo; pasado el plazo, el servidor lo rechaza.</summary>
    Task<RespuestaValidacion<VeredictoRegistrado>> DeshacerAsync(Guid veredictoId, CancellationToken cancellationToken = default);

    Task<RespuestaValidacion<LoteRegistrado>> ValidarLoteAsync(IReadOnlyList<Guid> documentoIds, CancellationToken cancellationToken = default);

    Task<RespuestaValidacion<LoteRegistrado>> DeshacerLoteAsync(Guid loteId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Implementación vacía: todo «no disponible». La pantalla lo muestra como tal y no pinta ningún dato.
/// </summary>
public sealed class ServicioValidacionInboundSinBackend : IServicioValidacionInbound
{
    // shortcut: sin backend, cablear cuando entre B-21 (cola paginada por cartera e índice de pendientes).
    public Task<RespuestaValidacion<ColaValidacion>> ObtenerColaAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(RespuestaValidacion<ColaValidacion>.NoDisponible);

    // shortcut: sin backend, cablear cuando entren B-22 (documento y veredicto previo), B-24 (veredicto del sistema) y
    // B-26 (propuesta de la IA con su registro).
    public Task<RespuestaValidacion<DocumentoAValidar>> ObtenerDocumentoAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(RespuestaValidacion<DocumentoAValidar>.NoDisponible);

    // shortcut: sin backend, cablear cuando entre B-22 (veredicto previo a la copia, con Deshacer de 10 s).
    public Task<RespuestaValidacion<VeredictoRegistrado>> ValidarAsync(SolicitudValidar solicitud, CancellationToken cancellationToken = default) =>
        Task.FromResult(RespuestaValidacion<VeredictoRegistrado>.NoDisponible);

    // shortcut: sin backend, cablear cuando entre B-22 (veredicto previo a la copia, con Deshacer de 10 s).
    public Task<RespuestaValidacion<VeredictoRegistrado>> RechazarAsync(SolicitudRechazar solicitud, CancellationToken cancellationToken = default) =>
        Task.FromResult(RespuestaValidacion<VeredictoRegistrado>.NoDisponible);

    // shortcut: sin backend, cablear cuando entre B-22 (veredicto previo a la copia, con Deshacer de 10 s).
    public Task<RespuestaValidacion<VeredictoRegistrado>> MarcarIncompletoAsync(SolicitudMarcarIncompleto solicitud, CancellationToken cancellationToken = default) =>
        Task.FromResult(RespuestaValidacion<VeredictoRegistrado>.NoDisponible);

    // shortcut: sin backend, cablear cuando entre B-22 (Deshacer de 10 s con efectos diferidos).
    public Task<RespuestaValidacion<VeredictoRegistrado>> DeshacerAsync(Guid veredictoId, CancellationToken cancellationToken = default) =>
        Task.FromResult(RespuestaValidacion<VeredictoRegistrado>.NoDisponible);

    // shortcut: sin backend, cablear cuando entre B-27 (lote con muestreo, R-2).
    public Task<RespuestaValidacion<LoteRegistrado>> ValidarLoteAsync(IReadOnlyList<Guid> documentoIds, CancellationToken cancellationToken = default) =>
        Task.FromResult(RespuestaValidacion<LoteRegistrado>.NoDisponible);

    // shortcut: sin backend, cablear cuando entre B-27 (lote con muestreo, R-2) sobre el Deshacer de B-22.
    public Task<RespuestaValidacion<LoteRegistrado>> DeshacerLoteAsync(Guid loteId, CancellationToken cancellationToken = default) =>
        Task.FromResult(RespuestaValidacion<LoteRegistrado>.NoDisponible);
}

/// <summary>
/// La cola que el Gestor CAE tiene abierta: los documentos que dejó la lista tras filtrar y agrupar, en el orden en que los
/// ve. «Empezar a validar» y abrir una fila la rellenan; la pantalla de validación la recorre con Anterior y Siguiente.
/// Vive en el circuito del usuario y no guarda ningún dato del documento, solo identificadores.
/// </summary>
public sealed class ColaEnCursoValidacion
{
    private IReadOnlyList<Guid> _ids = [];

    public IReadOnlyList<Guid> Ids => _ids;

    public void Fijar(IEnumerable<Guid> ids) => _ids = [.. ids];

    public void Quitar(Guid id) => _ids = [.. _ids.Where(x => x != id)];
}

/// <summary>
/// Si Validar y Rechazar de Inbound registran veredictos. Apagado por defecto: hasta que entre D-12 (quien subió un documento
/// no puede validarlo), solo se enciende en Development, con <c>Inbound__Validacion__Habilitado</c> en el perfil de arranque.
/// Fuera de Development queda apagado aunque la clave diga lo contrario. Apagado, la cola se ve y Validar y Rechazar responden
/// «no disponible».
/// </summary>
public sealed class OpcionesValidacionInbound
{
    public const string Seccion = "Inbound:Validacion";

    public bool Habilitado { get; set; }
}

public static class ValidacionInboundServiceCollectionExtensions
{
    /// <summary>
    /// Registra el puerto de la validación Inbound sobre el backend de la demo mínima: cola, y Validar y Rechazar uno a uno
    /// detrás de <see cref="OpcionesValidacionInbound"/>. Lo demás sigue «no disponible» hasta B-21, B-22, B-24, B-26 y B-27.
    /// </summary>
    public static IServiceCollection AddValidacionInbound(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment entorno)
    {
        var habilitado = entorno.IsDevelopment()
            && configuration.GetValue<bool>($"{OpcionesValidacionInbound.Seccion}:{nameof(OpcionesValidacionInbound.Habilitado)}");
        services.Configure<OpcionesValidacionInbound>(o => o.Habilitado = habilitado);
        services.AddScoped<IServicioValidacionInbound, ServicioValidacionInboundConBackend>();
        services.AddScoped<ColaEnCursoValidacion>();
        return services;
    }
}
