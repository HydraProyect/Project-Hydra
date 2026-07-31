using System.Threading.Channels;
using CaeManager.Application.Common;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.DocumentosIa;

/// <summary>
/// Cola en memoria sobre <see cref="Channel{T}"/>, consumida por
/// <see cref="ProcesadorAnalisisDocumentoHostedService"/>.
///
/// En memoria y no en una tabla porque el trabajo que transporta ya es de
/// mejor esfuerzo por diseño —hoy, si la verificación IA falla, solo se
/// registra en el log y la subida se da por buena igual—, y porque la
/// aplicación corre en una sola réplica (DEPLOY.md: SQLite no admite
/// escritura concurrente desde varios procesos). Persistirla sería montar una
/// infraestructura de jobs que nadie ha pedido y que además conviene diseñar
/// junto con la Plataforma de Integraciones, no antes
/// (ARQUITECTURA-INTEGRACIONES.md).
///
/// LIMITACIÓN CONOCIDA: si el proceso se reinicia con encargos pendientes, se
/// pierden y esos documentos quedan sin analizar sin que nadie se entere. Es
/// aceptable mientras el análisis sea mejor esfuerzo; deja de serlo el día que
/// algo dependa de que se haya ejecutado.
///
/// Sin límite de capacidad: el productor es una persona subiendo documentos a
/// mano, así que la cola no puede crecer más rápido de lo que se vacía. Un
/// límite aquí solo añadiría un modo de fallo (descartar o bloquear) sin
/// resolver ninguno real.
/// </summary>
public class ColaAnalisisDocumentoEnMemoria(ILogger<ColaAnalisisDocumentoEnMemoria> logger) : IColaAnalisisDocumento
{
    private readonly Channel<EncargoAnalisisDocumento> _canal =
        Channel.CreateUnbounded<EncargoAnalisisDocumento>(new UnboundedChannelOptions { SingleReader = true });

    public ChannelReader<EncargoAnalisisDocumento> Lector => _canal.Reader;

    public void Encolar(EncargoAnalisisDocumento encargo)
    {
        ArgumentNullException.ThrowIfNull(encargo);

        if (!_canal.Writer.TryWrite(encargo))
        {
            // Con un canal sin límite esto no debería ocurrir nunca salvo que
            // ya se haya cerrado al apagar la aplicación. No se propaga: el
            // análisis es mejor esfuerzo y no puede tumbar la subida.
            logger.LogWarning(
                "No se pudo encolar el análisis {Tipo} del documento {DocumentoId}.", encargo.Tipo, encargo.DocumentoId);
        }
    }
}
