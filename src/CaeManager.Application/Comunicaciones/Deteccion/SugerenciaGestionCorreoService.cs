using CaeManager.Application.Asignaciones;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Cumplimiento;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Documentos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Comunicaciones.Deteccion;

/// <summary>
/// Resultado de <see cref="ISugerenciaGestionCorreoService.ProcesarAsync"/> — exactamente uno de
/// los dos es no-null, nunca ambos: <paramref name="Sugerencia"/> cuando la IA identificó ítems
/// concretos, <paramref name="ResumenAgregado"/> cuando el correo es de una plataforma de solo
/// resumen sin desglose por persona (ronda de reducción de ruido en Comunicaciones). Ambos null
/// cuando el correo no aplica.
/// </summary>
public record ResultadoDeteccionGestionDto(SugerenciaGestionCorreo? Sugerencia, ResumenAgregadoGestionDto? ResumenAgregado)
{
    public static readonly ResultadoDeteccionGestionDto Vacio = new(null, null);
}

/// <summary>
/// Orquestador puro (mismo patrón que SugerenciaVisitaCorreoService): llamado
/// desde IngestaWebhookService justo después de persistir un Mensaje
/// Entrante, carga solo los Trabajadores con Asignación activa en algún Centro
/// del Cliente empresarial de la conversación y los TipoDocumento de ámbito
/// Trabajador. Pide la clasificación y registra una sugerencia pendiente por los
/// ítems detectados, validando de nuevo que el Trabajador siga entre los candidatos
/// (ver <see cref="FiltrarTrabajadoresFueraDeVentana"/>). Los Trabajadores con solo
/// Asignaciones históricas no salen al proveedor (Z4-2), así que la IA ya no puede
/// resolver un correo a uno de ellos: su ítem vuelve sin Trabajador. No guarda
/// cambios — el llamador ya persiste todo el mensaje ingerido en una sola
/// operación. Devuelve el resultado de la extracción (o vacío si no aplica)
/// para que otros pasos de la ingesta (ronda de reducción de ruido en
/// Comunicaciones) puedan reutilizar la misma llamada a IA sin pagar una
/// segunda.
///
/// Consumidor de IA con gate de Nivel 0 (DEC-33, REC-035): sin instrucción de
/// tratamiento vigente del Tenant propietario no se envían al proveedor ni el cuerpo
/// del correo ni los Trabajadores candidatos, y se devuelve el resultado vacío.
/// </summary>
public interface ISugerenciaGestionCorreoService
{
    Task<ResultadoDeteccionGestionDto> ProcesarAsync(Mensaje mensaje, Guid clienteId, CancellationToken cancellationToken = default);
}

public class SugerenciaGestionCorreoService(
    ICentrosQueryContext centrosContext,
    IAsignacionesQueryContext asignacionesContext,
    ITrabajadoresQueryContext trabajadoresContext,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    IDeteccionGestionCorreoService deteccion,
    ISugerenciaGestionCorreoRepository sugerenciaRepositorio,
    IInstruccionTratamientoIaService instruccionTratamientoIa,
    ITenantActual tenantActual,
    ILogger<SugerenciaGestionCorreoService> logger) : ISugerenciaGestionCorreoService
{
    public async Task<ResultadoDeteccionGestionDto> ProcesarAsync(Mensaje mensaje, Guid clienteId, CancellationToken cancellationToken = default)
    {
        // Nivel 0 (DEC-33, REC-035) — ver el mismo gate en VerificacionIaDocumentoService.
        // Esta es la detección que más datos personales envía de las tres de la
        // ingesta: además del cuerpo del correo viajan los nombres de los
        // Trabajadores candidatos que el correo menciona (el proveedor preselecciona
        // por documento o por nombre y el DNI no sale: viaja su marcador, ver
        // AnthropicDeteccionGestionCorreoService), así que el gate
        // va por delante de cargar nada. Vacío es el
        // mismo resultado que "el correo no aplica" — el llamador ya sabe tratarlo,
        // y ninguna clasificación de ruido depende de IA para existir.
        if (tenantActual.TenantId is not { } tenantId || !await instruccionTratamientoIa.EstaHabilitadaAsync(tenantId, cancellationToken))
        {
            logger.LogInformation(
                "Detección de gestión por correo omitida para el mensaje {MensajeId}: el Tenant propietario no tiene instrucción de tratamiento IA vigente (Nivel 0).",
                mensaje.Id);
            return ResultadoDeteccionGestionDto.Vacio;
        }

        var centroIds = await centrosContext.Centros
            .Where(c => c.ClienteId == clienteId)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);

        if (centroIds.Count == 0)
            return ResultadoDeteccionGestionDto.Vacio;

        // Solo Asignaciones activas (Z4-2): un Trabajador que solo tiene Asignaciones
        // históricas en los Centros de este Cliente empresarial no sale al proveedor de IA,
        // ni su nombre ni su documento. El filtro va en la consulta, antes de la llamada.
        var trabajadorIds = await asignacionesContext.Asignaciones
            .Where(a => centroIds.Contains(a.CentroId) && a.FechaBaja == null)
            .Select(a => a.TrabajadorId)
            .Distinct()
            .ToListAsync(cancellationToken);

        // Sin Trabajadores con Asignación activa en algún Centro del Cliente empresarial
        // no hay a quién asociar la sugerencia — v1 no cubre "sugerir un
        // Trabajador nuevo" (YAGNI, mismo criterio que SugerenciaVisitaCorreoService).
        if (trabajadorIds.Count == 0)
            return ResultadoDeteccionGestionDto.Vacio;

        var trabajadorIdsActivos = trabajadorIds.ToHashSet();

        var trabajadores = await trabajadoresContext.Trabajadores
            .Where(t => trabajadorIds.Contains(t.Id))
            .Select(t => new TrabajadorCandidatoGestionDto(t.Id, t.Nombre + " " + t.Apellidos, t.Dni))
            .ToListAsync(cancellationToken);

        var tiposDocumento = await tiposDocumentoContext.TiposDocumento
            .Where(t => t.AmbitoAplicacion == AmbitoAplicacion.Trabajador && !t.Archivado)
            .Select(t => new TipoDocumentoCandidatoGestionDto(t.Id, t.Nombre))
            .ToListAsync(cancellationToken);

        if (tiposDocumento.Count == 0)
            return ResultadoDeteccionGestionDto.Vacio;

        var resultado = await deteccion.DetectarAsync(mensaje.CuerpoHtml, trabajadores, tiposDocumento, cancellationToken);

        if (resultado.EsFallido)
        {
            logger.LogInformation(
                "Detección de gestión por correo no disponible para el mensaje {MensajeId}: {Codigo}", mensaje.Id, resultado.Error.Codigo);
            return ResultadoDeteccionGestionDto.Vacio;
        }

        var deteccionDto = resultado.Valor;
        if (!deteccionDto.EsActualizacionDocumento)
            return ResultadoDeteccionGestionDto.Vacio;

        if (deteccionDto.Items.Count == 0)
            return new ResultadoDeteccionGestionDto(null, deteccionDto.ResumenAgregado);

        var items = FiltrarTrabajadoresFueraDeVentana(deteccionDto.Items, trabajadorIdsActivos);
        if (items.Count == 0)
            return ResultadoDeteccionGestionDto.Vacio;

        var sugerencia = new SugerenciaGestionCorreo(
            mensaje.Id, deteccionDto.Resumen ?? "El correo parece pedir la actualización de un documento.", deteccionDto.Confianza);

        foreach (var item in items)
            sugerencia.AgregarDetalle(item.TrabajadorId, item.TipoDocumentoId, item.ConfianzaTrabajador, item.ConfianzaTipoDocumento);

        sugerenciaRepositorio.Agregar(sugerencia);
        return new ResultadoDeteccionGestionDto(sugerencia, null);
    }

    /// <summary>
    /// Descarta los ítems cuyo Trabajador resuelto ya no tiene asignación
    /// activa (patrón "trabajador fuera de ventana") — un ítem sin Trabajador
    /// resuelto (solo TipoDocumento) nunca se descarta por este motivo, no
    /// hay nada que comprobar. Desde Z4-2 los candidatos ya son solo los activos,
    /// así que esto queda como defensa: el proveedor descarta además cualquier Id
    /// que no esté en la lista.
    /// </summary>
    public static IReadOnlyList<ItemDeteccionGestionDto> FiltrarTrabajadoresFueraDeVentana(
        IReadOnlyList<ItemDeteccionGestionDto> items, IReadOnlySet<Guid> trabajadorIdsActivos) =>
        items.Where(item => item.TrabajadorId is null || trabajadorIdsActivos.Contains(item.TrabajadorId.Value)).ToList();
}
