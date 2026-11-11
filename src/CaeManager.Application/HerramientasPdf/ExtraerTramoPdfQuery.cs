using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.HerramientasPdf;

/// <summary>
/// Extrae un tramo del PDF que se está dividiendo para descargarlo, sin crear ningún Documento ni escribir nada: es la
/// salida «Descargar» de Dividir, la alternativa a <see cref="CrearDocumentosDesdeDivisionCommand"/>. Exige lo mismo que
/// preparar la división: Herramientas PDF disponible y la Empresa gestionada dentro del alcance de gestión del usuario.
/// </summary>
public record ExtraerTramoPdfQuery(
    byte[] ContenidoPdf, string NombreOriginal, Guid EmpresaGestionadaId, int PaginaInicial, int PaginaFinal)
    : IRequest<Result<TramoPdfExtraido>>;

public record TramoPdfExtraido(string NombreArchivo, byte[] Contenido);

public class ExtraerTramoPdfQueryValidator : AbstractValidator<ExtraerTramoPdfQuery>
{
    public ExtraerTramoPdfQueryValidator()
    {
        RuleFor(q => q.ContenidoPdf).NotEmpty();
        RuleFor(q => q.ContenidoPdf.Length).LessThanOrEqualTo((int)LimitesDivisionPdf.TamanoMaximoOriginalBytes);
        RuleFor(q => q.EmpresaGestionadaId).NotEmpty();
        RuleFor(q => q.PaginaInicial).GreaterThanOrEqualTo(1);
        RuleFor(q => q.PaginaFinal).GreaterThanOrEqualTo(q => q.PaginaInicial);
    }
}

public class ExtraerTramoPdfQueryHandler(
    IDisponibilidadHerramientasPdf disponibilidad,
    IAlcanceDatosService alcanceDatos,
    IDivisorPdf divisor)
    : IRequestHandler<ExtraerTramoPdfQuery, Result<TramoPdfExtraido>>
{
    public async Task<Result<TramoPdfExtraido>> Handle(ExtraerTramoPdfQuery request, CancellationToken cancellationToken)
    {
        if (!await disponibilidad.EstaDisponibleAsync(cancellationToken))
            return Fallo(PrepararDivisionPdfQueryHandler.ErrorNoDisponible, "Herramientas PDF no está disponible.");

        if (!await alcanceDatos.EmpresaParaGestionVisibleAsync(request.EmpresaGestionadaId, cancellationToken))
            return Fallo(ObtenerCatalogoDivisionPdfQueryHandler.ErrorEmpresaNoGestionable, "La Empresa no está en tu cartera.");

        // PDFsharp es síncrono y trabaja con la CPU: en un circuito de Blazor bloquearía la pantalla.
        var extraido = await Task.Run(() => Extraer(request), cancellationToken);
        if (!extraido.EsExitoso)
            return Result.Fallo<TramoPdfExtraido>(extraido.Error);

        return Result.Exito(new TramoPdfExtraido(
            NombresTramoPdf.De(request.NombreOriginal, request.PaginaInicial, request.PaginaFinal), extraido.Valor));
    }

    private Result<byte[]> Extraer(ExtraerTramoPdfQuery request)
    {
        var abierto = divisor.Abrir(request.ContenidoPdf);
        if (!abierto.EsExitoso)
            return Result.Fallo<byte[]>(abierto.Error);

        using var pdf = abierto.Valor;
        if (request.PaginaFinal > pdf.NumeroPaginas)
            return Result.Fallo<byte[]>(Error.Crear(ErroresDivisorPdf.RangoInvalido, "Las páginas no coinciden con el PDF."));

        return pdf.ExtraerPaginas(request.PaginaInicial, request.PaginaFinal);
    }

    private static Result<TramoPdfExtraido> Fallo(string codigo, string mensaje) =>
        Result.Fallo<TramoPdfExtraido>(Error.Crear(codigo, mensaje));
}

/// <summary>Nombre del archivo de un tramo, el mismo al crearlo como Documento que al descargarlo.</summary>
public static class NombresTramoPdf
{
    public static string De(string nombreOriginal, int paginaInicial, int paginaFinal) =>
        $"{Path.GetFileNameWithoutExtension(nombreOriginal)}-pag-{paginaInicial}-{paginaFinal}.pdf";
}
