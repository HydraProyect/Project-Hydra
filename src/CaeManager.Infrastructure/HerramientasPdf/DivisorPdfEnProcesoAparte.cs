using CaeManager.Application.HerramientasPdf;
using CaeManager.Domain.Common;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.HerramientasPdf;

/// <summary>
/// <see cref="IDivisorPdf"/> del proceso web: no abre ningún PDF. Abrir para medir y extraer cada tramo van, cada uno, a un
/// proceso aparte (<see cref="ProcesoHerramientasPdf"/>, con <see cref="PdfSharpDivisorPdf"/>) con un límite duro de memoria y
/// de tiempo, con el mismo turno, lanzamiento y degradación que Unir (<see cref="EjecutorProcesoHerramientasPdf"/>). Así un
/// PDF hostil que pase el <see cref="AcotadorEstructuraPdf"/> agota ese proceso, que muere o se mata, y la pantalla dice
/// «demasiado complejo» (<see cref="ErroresDivisorPdf.DemasiadoComplejo"/>) sin reintentar y con la aplicación viva.
///
/// El PDF abierto aquí es solo su recuento de páginas y su firma, más la copia del original que se reenvía para cada tramo:
/// cada extracción vuelve a abrir el original en un proceso nuevo. Cuesta abrirlo una vez por tramo, a cambio de que ningún
/// proceso aparte siga vivo entre una petición y la siguiente.
/// </summary>
public sealed class DivisorPdfEnProcesoAparte(
    OpcionesProcesoHerramientasPdf opciones,
    LimitadorProcesosAislados limitador,
    ILanzadorProcesoAislado lanzador,
    ILogger<DivisorPdfEnProcesoAparte> logger) : IDivisorPdf
{
    private static readonly CodigosProcesoAparte Codigos = new(ErroresDivisorPdf.DemasiadoComplejo, ErroresDivisorPdf.Ilegible);

    private readonly EjecutorProcesoHerramientasPdf _ejecutor = new(opciones, limitador, lanzador, logger);

    public async Task<Result<IPdfParaDividir>> AbrirAsync(byte[] contenidoPdf, CancellationToken cancellationToken)
    {
        // Lo que se decide sin abrir no lanza ningún proceso.
        if (contenidoPdf.Length == 0)
            return Fallo(ErroresDivisorPdf.Ilegible);

        if (contenidoPdf.Length > LimitesDivisionPdf.TamanoMaximoOriginalBytes)
            return Fallo(ErroresDivisorPdf.DemasiadoGrande);

        var salida = await _ejecutor.EjecutarAsync(
            (entrada, token) => ProtocoloProcesoHerramientasPdf.EscribirPeticionAsync(
                entrada, ProtocoloProcesoHerramientasPdf.OperacionAbrirParaDividir, [contenidoPdf], token),
            opciones.TiempoMaximoAbrirParaDividir, ProtocoloProcesoHerramientasPdf.MaximoBytesRespuestaApertura, cancellationToken);

        var apertura = _ejecutor.Traducir(salida, ProtocoloProcesoHerramientasPdf.LeerApertura, "abrir para dividir", Codigos);
        return apertura.EsExitoso
            ? Result.Exito<IPdfParaDividir>(new PdfEnProcesoAparte(this, contenidoPdf, apertura.Valor.NumeroPaginas, apertura.Valor.EstaFirmado))
            : Result.Fallo<IPdfParaDividir>(apertura.Error);
    }

    private async Task<Result<byte[]>> ExtraerAsync(byte[] contenidoPdf, int numeroPaginas, int paginaInicial, int paginaFinal,
        long tamanoMaximoBytes, CancellationToken cancellationToken)
    {
        if (paginaInicial < 1 || paginaFinal < paginaInicial || paginaFinal > numeroPaginas)
            return Result.Fallo<byte[]>(Error.Crear(ErroresDivisorPdf.RangoInvalido, ProtocoloProcesoHerramientasPdf.MensajeDe(ErroresDivisorPdf.RangoInvalido)));

        // El protocolo no admite una cota mayor que la del original más grande: ninguna cota real la pasa.
        var cota = Math.Clamp(tamanoMaximoBytes, 1, ProtocoloProcesoHerramientasPdf.MaximaCotaTramo);
        var tramo = new ProtocoloProcesoHerramientasPdf.ParametrosTramo(paginaInicial, paginaFinal, cota);

        var salida = await _ejecutor.EjecutarAsync(
            (entrada, token) => ProtocoloProcesoHerramientasPdf.EscribirPeticionTramoAsync(entrada, contenidoPdf, tramo, token),
            opciones.TiempoMaximoExtraerTramo, ProtocoloProcesoHerramientasPdf.MaximoBytesRespuestaTramo(cota), cancellationToken);

        return _ejecutor.Traducir(salida, respuesta => ProtocoloProcesoHerramientasPdf.LeerTramo(respuesta, cota), "extraer un tramo", Codigos);
    }

    private static Result<IPdfParaDividir> Fallo(string codigo) =>
        Result.Fallo<IPdfParaDividir>(Error.Crear(codigo, ProtocoloProcesoHerramientasPdf.MensajeDe(codigo)));

    private sealed class PdfEnProcesoAparte(DivisorPdfEnProcesoAparte divisor, byte[] contenidoPdf, int numeroPaginas, bool firmado)
        : IPdfParaDividir
    {
        public int NumeroPaginas => numeroPaginas;

        public bool EstaFirmado => firmado;

        public Task<Result<byte[]>> ExtraerPaginasAsync(int paginaInicial, int paginaFinal, long tamanoMaximoBytes,
            CancellationToken cancellationToken) =>
            divisor.ExtraerAsync(contenidoPdf, numeroPaginas, paginaInicial, paginaFinal, tamanoMaximoBytes, cancellationToken);

        public void Dispose()
        {
            // No retiene nada fuera de la copia del original, que es de quien la pasó.
        }
    }
}
