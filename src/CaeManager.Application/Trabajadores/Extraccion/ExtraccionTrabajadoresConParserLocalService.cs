using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Trabajadores.Extraccion;

/// <summary>
/// Servicio de extracción de Trabajadores que registra la aplicación: primero el parser
/// local (<see cref="IExtractorTrabajadoresLocal"/>) y, solo si el documento no cuadra, la IA
/// (<c>respaldoIa</c>, el cliente de Anthropic de siempre). El camino local no envía nada fuera
/// de la aplicación; el de respaldo es exactamente el de antes.
///
/// No cambia cuándo se dispara el flujo: el Nivel 0 (instrucción de tratamiento con IA del
/// Tenant propietario), la configuración por tipo de documento y el resto de condiciones las
/// sigue evaluando <c>DeteccionTrabajadoresService</c> antes de llamar aquí, su único consumidor
/// (por eso este fichero figura como exento en el ratchet del Nivel 0, como el adaptador del
/// router). Este servicio solo decide con qué se lee un documento al que ya se decidió leer.
///
/// Trazabilidad: el origen (<c>ParserLocal</c> o <c>Ia</c>) y, si cayó a la IA, el
/// <see cref="MotivoNoCuadra"/> salen en el log estructurado y en el contador
/// <see cref="Observabilidad.ExtraccionesTrabajadores"/>. Nunca nombres ni DNI. Este flujo no
/// escribe <c>AuditoriaExtraccionIa</c> (no lo hacía antes tampoco), así que no hay fila que
/// ampliar sin una migración.
/// </summary>
public sealed class ExtraccionTrabajadoresConParserLocalService(
    IExtractorTrabajadoresLocal parserLocal,
    IExtraccionTrabajadoresIaService respaldoIa,
    ILogger<ExtraccionTrabajadoresConParserLocalService> logger) : IExtraccionTrabajadoresIaService
{
    public const string OrigenParserLocal = "ParserLocal";
    public const string OrigenIa = "Ia";

    public async Task<Result<IReadOnlyList<TrabajadorExtraidoDto>>> ExtraerAsync(byte[] contenidoPdf, CancellationToken cancellationToken = default)
    {
        ResultadoExtraccionLocal local;

        try
        {
            local = parserLocal.Extraer(contenidoPdf);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Un fallo del parser no puede dejar al documento sin lectura: es un «no cuadra» más.
            // Sin el mensaje de la excepción en el log: viene de un PDF de un tercero.
            logger.LogWarning(
                "El parser local de Trabajadores falló ({TipoExcepcion}); se usa la IA.", ex.GetType().Name);
            local = ResultadoExtraccionLocal.NoCuadra(MotivoNoCuadra.ErrorInterno);
        }

        if (local.Cuadra)
        {
            Registrar(OrigenParserLocal, motivo: null, local.Trabajadores.Count);
            return Result.Exito(local.Trabajadores);
        }

        var motivo = local.Motivo!.Value;
        var resultado = await respaldoIa.ExtraerAsync(contenidoPdf, cancellationToken);

        Registrar(OrigenIa, motivo, resultado.EsFallido ? 0 : resultado.Valor.Count);
        return resultado;
    }

    private void Registrar(string origen, MotivoNoCuadra? motivo, int trabajadores)
    {
        var etiquetaMotivo = motivo?.ToString() ?? "Cuadra";

        Observabilidad.ExtraccionesTrabajadores.Add(
            1,
            new KeyValuePair<string, object?>("origen", origen),
            new KeyValuePair<string, object?>("motivo", etiquetaMotivo));

        logger.LogInformation(
            "Extracción de Trabajadores resuelta por {Origen} ({Motivo}): {Trabajadores} Trabajadores.",
            origen, etiquetaMotivo, trabajadores);
    }
}
