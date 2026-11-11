using CaeManager.Domain.Common;

namespace CaeManager.Application.HerramientasPdf;

/// <summary>
/// Arma un PDF con una página por foto, en el orden dado. No escribe nada: el resultado vuelve como bytes y la pantalla
/// lo entrega para descargar; no se guarda como Documento ni se registra como Apertura, porque no lee ningún Documento
/// almacenado, solo las fotos que acaba de subir quien lo pide.
///
/// No es un request de MediatR a propósito. Todo request pasa por <see cref="Common.PuertaAccesoDatos"/>, un semáforo de
/// uno por circuito, y decodificar hasta veinte fotos tarda decenas de segundos: durante ese tiempo el circuito entero
/// (contadores del layout, buscador, notificaciones) se quedaría sin acceso a datos, y esta operación no usa ninguno.
/// Un Command tampoco sirve: caería en las listas de comandos con cartera, y no toca ningún dato de Tenant. El rol lo exige
/// la página (Administrador, Dirección CAE, Coordinador CAE y Gestor CAE), igual que el resto de Herramientas PDF.
/// </summary>
public class GeneradorPdfDesdeFotos(
    IDisponibilidadHerramientasPdf disponibilidad, IProcesadorFotosPdf procesador, LimitadorConversionesPdf limitador)
{
    public async Task<Result<PdfDeFotos>> GenerarAsync(IReadOnlyList<byte[]> fotos, CancellationToken cancellationToken)
    {
        if (!await disponibilidad.EstaDisponibleAsync(cancellationToken))
            return Fallo(ErroresFotoAPdf.NoDisponible, "Herramientas PDF no está disponible.");

        if (fotos.Count == 0)
            return Fallo(ErroresFotoAPdf.SinFotos, "No hay fotos.");

        if (fotos.Count > LimitesFotoAPdf.MaximoFotos)
            return Fallo(ErroresFotoAPdf.DemasiadasFotos, "Hay más fotos de las admitidas.");

        // Antes de decodificar nada y sin sumar en un tipo que desborde: cada foto está acotada y el recuento también.
        long total = 0;
        for (var i = 0; i < fotos.Count; i++)
        {
            var validada = ValidadorFotoAPdf.Validar(fotos[i]);
            if (validada.EsFallido)
                return Fallo(validada.Error.Codigo, $"Foto {i + 1}: {validada.Error.Mensaje}");

            total += fotos[i].Length;
            if (total > LimitesFotoAPdf.TamanoMaximoTotalBytes)
                return Fallo(ErroresFotoAPdf.TotalDemasiadoGrande, "Las fotos superan el tamaño total admitido.");
        }

        // Decodificar y componer es trabajo de CPU: entra por el limitador del proceso (no se ocupan todos los núcleos por
        // muchas sesiones que generen a la vez) y corre fuera del circuito de Blazor, que si no se quedaría bloqueado. El
        // turno se suelta cuando el procesador termina: Task.Run no completa mientras su delegado sigue en marcha.
        using var turno = await limitador.EntrarAsync(cancellationToken);
        if (turno is null)
            return Fallo(ErroresFotoAPdf.Ocupado, "Hay muchas conversiones en curso.");

        return await Task.Run(() => procesador.ComponerPdf(fotos, cancellationToken), cancellationToken);
    }

    private static Result<PdfDeFotos> Fallo(string codigo, string mensaje) => Result.Fallo<PdfDeFotos>(Error.Crear(codigo, mensaje));
}