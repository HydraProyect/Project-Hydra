using CaeManager.Domain.Common;
using MediatR;

namespace CaeManager.Application.HerramientasPdf;

/// <summary>
/// Arma un PDF con una página por foto, en el orden dado. No escribe nada: el resultado vuelve como bytes y la pantalla
/// lo entrega para descargar; no se guarda como Documento ni se registra como Apertura, porque no lee ningún Documento
/// almacenado, solo las fotos que acaba de subir quien lo pide.
/// Es una Query y no un Command porque no escribe: un Command quedaría en las listas de comandos con cartera, y este
/// no toca ningún dato de Tenant. El rol lo exige la página (Administrador, Dirección CAE, Coordinador CAE y Gestor CAE).
/// </summary>
public record GenerarPdfDesdeFotosQuery(IReadOnlyList<byte[]> Fotos) : IRequest<Result<byte[]>>;

public class GenerarPdfDesdeFotosQueryHandler(
    IDisponibilidadHerramientasPdf disponibilidad,
    IProcesadorFotosPdf procesador)
    : IRequestHandler<GenerarPdfDesdeFotosQuery, Result<byte[]>>
{
    public async Task<Result<byte[]>> Handle(GenerarPdfDesdeFotosQuery request, CancellationToken cancellationToken)
    {
        if (!await disponibilidad.EstaDisponibleAsync(cancellationToken))
            return Fallo(ErroresFotoAPdf.NoDisponible, "Herramientas PDF no está disponible.");

        var fotos = request.Fotos;
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

        // Decodificar y componer es trabajo de CPU síncrono: en un circuito de Blazor bloquearía la pantalla.
        return await Task.Run(() => procesador.ComponerPdf(fotos, cancellationToken), cancellationToken);
    }

    private static Result<byte[]> Fallo(string codigo, string mensaje) => Result.Fallo<byte[]>(Error.Crear(codigo, mensaje));
}
