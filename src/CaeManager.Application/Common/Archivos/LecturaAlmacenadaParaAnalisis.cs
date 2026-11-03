namespace CaeManager.Application.Common.Archivos;

/// <summary>
/// Un archivo del almacenamiento copiado a un <c>byte[]</c> para analizarlo (extracción por IA, clasificación), con su
/// reserva del presupuesto de memoria de documentos (<see cref="IPresupuestoMemoriaDocumentos"/>) <b>viva hasta que se
/// desecha</b>. Seguimiento de la revisión de Codex del 2026-11-01 (OWASP API4:2023, ASVS 4.0.3 V12.1.1 y V5.2.1,
/// CWE-400).
///
/// <para>
/// El flujo que devuelve <see cref="IFileStorageService.AbrirAsync"/> retiene su reserva solo mientras está abierto, y
/// quien lo copia lo cierra en cuanto termina; si el análisis se hace después con el <c>byte[]</c> a secas, ese
/// documento sigue en memoria sin que el presupuesto lo cuente, justo durante la parte larga y que más deriva de él
/// (texto, páginas rasterizadas, base64). Aquí la reserva del análisis se toma <b>antes</b> de la copia, con el
/// flujo aún abierto, y se devuelve al desechar: nunca hay un <c>byte[]</c> sin reserva.
/// </para>
/// </summary>
public sealed class LecturaAlmacenadaParaAnalisis : IDisposable
{
    /// <summary>
    /// Veces el tamaño del documento que el análisis tiene a la vez en memoria: el mismo factor que la lectura de la
    /// Subida múltiple y del alta individual (<c>SubidaMasiva.FactorMemoriaLectura</c>, en Web, que Application no puede
    /// referenciar): el original y lo que la extracción deriva de él.
    /// </summary>
    public const int FactorMemoriaAnalisis = 4;


    private readonly ReservaMemoriaDocumentos _reserva;

    private LecturaAlmacenadaParaAnalisis(byte[] contenido, ReservaMemoriaDocumentos reserva)
    {
        Contenido = contenido;
        _reserva = reserva;
    }

    /// <summary>El archivo descifrado. Solo es válido, y solo está reservado, hasta que se desecha esta lectura.</summary>
    public byte[] Contenido { get; }

    /// <summary>Bytes que esta lectura descuenta ahora del presupuesto.</summary>
    public long BytesReservados => _reserva.Bytes;

    /// <summary>
    /// Abre el archivo, reserva su análisis y lo copia. Cierra siempre el flujo, y si algo falla después de reservar
    /// devuelve la reserva antes de propagar el fallo.
    /// </summary>
    /// <exception cref="FileNotFoundException">No existe o no es del Tenant activo.</exception>
    /// <exception cref="PresupuestoMemoriaAgotadoException">
    /// Sin hueco, al abrir o al reservar el análisis, o el flujo entrega más bytes que los reservados: es saturación
    /// transitoria o contenido fuera de lo declarado, y falla cerrado, sin dejar nada reservado.
    /// </exception>
    public static async Task<LecturaAlmacenadaParaAnalisis> LeerAsync(
        IFileStorageService almacenamiento, IPresupuestoMemoriaDocumentos presupuesto, string archivoUrl,
        CancellationToken cancellationToken = default)
    {
        await using var flujo = await almacenamiento.AbrirAsync(archivoUrl, cancellationToken);

        // Un flujo que no dice su longitud se reserva por el peor caso, no por cero.
        var longitud = flujo.CanSeek ? flujo.Length : LimitesArchivoSubido.TamanoMaximoBytes;
        // El flujo sigue abierto y retiene su claro: esperar hueco con esa reserva retenida haría que varias lecturas
        // se esperasen unas a otras hasta agotar el plazo y rechazasen trabajo que cabría por separado. Se amplía sin
        // esperar y sin recortar (TryReservar): si el análisis no cabe junto al flujo, se rechaza con la saturación
        // transitoria en vez de seguir con una reserva menor que su estimación.
        cancellationToken.ThrowIfCancellationRequested();
        if (!presupuesto.TryReservar(longitud * FactorMemoriaAnalisis, out var reserva))
            throw new PresupuestoMemoriaAgotadoException();

        try
        {
            // La copia se corta al llegar a lo reservado: un flujo que no dice su longitud (o que miente) y entrega más
            // que el máximo supuesto no puede crecer en memoria por encima de lo que el presupuesto cuenta.
            using var memoria = new MemoryStream(capacity: (int)Math.Min(longitud, Array.MaxLength));
            var bloque = new byte[Math.Min(81_920, Math.Max(1, longitud + 1))];
            while (true)
            {
                var leidos = await flujo.ReadAsync(bloque, cancellationToken);
                if (leidos == 0) break;

                if (memoria.Length + leidos > longitud)
                    throw new PresupuestoMemoriaAgotadoException();

                memoria.Write(bloque, 0, leidos);
            }

            return new LecturaAlmacenadaParaAnalisis(memoria.ToArray(), reserva);
        }
        catch
        {
            reserva.Dispose();
            throw;
        }
    }

    public void Dispose() => _reserva.Dispose();
}
