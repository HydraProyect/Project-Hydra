using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
/// Si la cola, el documento, Validar y Rechazar de Inbound funcionan. Apagado por defecto; lo enciende solo la clave
/// <c>Inbound:Validacion:Habilitado=true</c> (<c>Inbound__Validacion__Habilitado</c> en el entorno), en cualquier entorno,
/// sin mirar su nombre (DECISION_LOG 2026-11-17, entradas de los commits 2757bac y cc7e68e: «solo con la variable»; la
/// opción de arrancar staging como entorno «Staging» quedó sin efecto). Apagado, todo responde «no disponible» y no se lee
/// nada.
/// También abre la descarga del original del Tenant TEC desde la validación (<see cref="ArchivoValidacionInboundEndpoints"/>,
/// demo P6): apagado, esa ruta responde 404 sin leer nada, porque cada apertura deja traza en el Tenant TEC.
/// <para>
/// HUECO DECLARADO: la guarda de Development se quitó sin que se cumplan las dos condiciones que pedía, y de ellas queda
/// una. D-12 (quien subió un documento no puede validarlo) ya se aplica en el servidor: <c>app_inbound_documentos_autorizados</c>
/// marca la versión que subió quien lee (usuario efectivo o Actor real del autor, D-12.2; un autor nulo se puede validar,
/// D-12.1), y <c>RegistrarEvidenciaInboundCommand</c> y <c>ValidarEvidenciaInboundCommand</c> rechazan Validar con esa marca
/// o bajo impersonación (<c>GuardaD12</c>, provisional hasta que el contexto firmado lleve el Actor real); la pantalla solo
/// deshabilita Validar. B-22 (el veredicto previo a la copia) no ha entrado: un Rechazar registra la evidencia y copia el
/// binario al Tenant TCE en el mismo guardado (<c>RegistrarEvidenciaInboundCommand</c>, DN-12 (a)), y esa copia no se
/// borra. Producción usa la misma imagen y queda protegida solo porque allí nunca se pone la clave: es una consecuencia
/// aceptada por Chris en 2757bac, no una propiedad del código.
/// </para>
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
        this IServiceCollection services, IConfiguration configuration)
    {
        // Solo la clave, sin mirar el entorno (DECISION_LOG 2026-11-17, 2757bac y cc7e68e). Sin clave, false.
        var habilitado = configuration.GetValue<bool>(
            $"{OpcionesValidacionInbound.Seccion}:{nameof(OpcionesValidacionInbound.Habilitado)}");
        services.Configure<OpcionesValidacionInbound>(o => o.Habilitado = habilitado);
        services.AddScoped<IServicioValidacionInbound, ServicioValidacionInboundConBackend>();
        services.AddScoped<ColaEnCursoValidacion>();
        return services;
    }
}
