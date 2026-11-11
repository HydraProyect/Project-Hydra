using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Domain.Common;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.HerramientasPdf;

/// <summary>
/// Extrae un tramo del PDF que se está dividiendo para descargarlo, sin crear ningún Documento ni escribir nada: es la
/// salida «Descargar» de Dividir, la alternativa a <see cref="CrearDocumentosDesdeDivisionCommand"/>. Exige lo mismo que
/// guardar la división, y en el mismo orden, antes de abrir el PDF: Herramientas PDF disponible, el Tenant propietario
/// del inicio de la pantalla y una Empresa que exista en ese Tenant, sea propia y esté en el alcance de gestión. El Tenant
/// y la Empresa se vuelven a comprobar tras la extracción, justo antes de devolver el tramo. Un
/// alcance sin restricción no sustituye a la comprobación de la Empresa: la cartera no es aislamiento de Tenant.
/// </summary>
/// <param name="TenantEsperadoId">El Tenant con el que empezó la pantalla; si el selector cambió, se rechaza.</param>
public record ExtraerTramoPdfQuery(
    byte[] ContenidoPdf, string NombreOriginal, Guid TenantEsperadoId, Guid EmpresaGestionadaId, int PaginaInicial, int PaginaFinal)
    : IRequest<Result<TramoPdfExtraido>>;

public record TramoPdfExtraido(string NombreArchivo, byte[] Contenido);

public class ExtraerTramoPdfQueryValidator : AbstractValidator<ExtraerTramoPdfQuery>
{
    public ExtraerTramoPdfQueryValidator()
    {
        RuleFor(q => q.ContenidoPdf).NotEmpty();
        RuleFor(q => q.ContenidoPdf.Length).LessThanOrEqualTo((int)LimitesDivisionPdf.TamanoMaximoOriginalBytes);
        RuleFor(q => q.TenantEsperadoId).NotEmpty();
        RuleFor(q => q.EmpresaGestionadaId).NotEmpty();
        RuleFor(q => q.PaginaInicial).GreaterThanOrEqualTo(1);
        RuleFor(q => q.PaginaFinal).GreaterThanOrEqualTo(q => q.PaginaInicial);
    }
}

public class ExtraerTramoPdfQueryHandler(
    IDisponibilidadHerramientasPdf disponibilidad,
    ITenantActual tenantActual,
    IEmpresasQueryContext empresasContext,
    IAlcanceDatosService alcanceDatos,
    IDivisorPdf divisor,
    ILogger<ExtraerTramoPdfQueryHandler> logger)
    : IRequestHandler<ExtraerTramoPdfQuery, Result<TramoPdfExtraido>>
{
    public async Task<Result<TramoPdfExtraido>> Handle(ExtraerTramoPdfQuery request, CancellationToken cancellationToken)
    {
        if (!await disponibilidad.EstaDisponibleAsync(cancellationToken))
            return Fallo(PrepararDivisionPdfQueryHandler.ErrorNoDisponible, "Herramientas PDF no está disponible.");

        if (await RechazoDeAccesoAsync(request, cancellationToken) is { } rechazo)
            return rechazo;

        // PDFsharp es síncrono y trabaja con la CPU: en un circuito de Blazor bloquearía la pantalla.
        var extraido = await Task.Run(
            () => ExtraccionTramoPdf.Extraer(divisor, request.ContenidoPdf, request.PaginaInicial, request.PaginaFinal, logger),
            cancellationToken);
        if (!extraido.EsExitoso)
            return Result.Fallo<TramoPdfExtraido>(extraido.Error);

        // La extracción tarda: si entretanto el selector cambió de Tenant o la Empresa dejó de estar en el Tenant o en la
        // cartera, el tramo ya no se entrega. Es la misma comprobación de antes de abrir, repetida justo antes de devolverlo.
        if (await RechazoDeAccesoAsync(request, cancellationToken) is { } rechazoTrasExtraer)
            return rechazoTrasExtraer;

        return Result.Exito(new TramoPdfExtraido(
            NombresTramoPdf.De(request.NombreOriginal, request.PaginaInicial, request.PaginaFinal), extraido.Valor));
    }

    private async Task<Result<TramoPdfExtraido>?> RechazoDeAccesoAsync(ExtraerTramoPdfQuery request, CancellationToken cancellationToken)
    {
        if (tenantActual.TenantId is not { } tenant || tenant != request.TenantEsperadoId)
            return Fallo(CrearDocumentosDesdeDivisionCommandHandler.ErrorTenantCruzado,
                "La Empresa ha cambiado. Vuelve a empezar para no descargar documentos de otra.");

        // La misma comprobación que el catálogo de la división: la Empresa existe en el Tenant (filtro de Tenant y RLS
        // por debajo), es propia y está en el alcance de gestión. Un alcance sin restricción deja pasar cualquier
        // identificador; la lectura de la Empresa no.
        if (await DocumentosUnibles.EmpresaGestionableAsync(request.EmpresaGestionadaId, empresasContext, alcanceDatos, cancellationToken) is null)
            return Fallo(ObtenerCatalogoDivisionPdfQueryHandler.ErrorEmpresaNoGestionable, "La Empresa no existe o queda fuera de tu cartera.");

        return null;
    }

    private static Result<TramoPdfExtraido> Fallo(string codigo, string mensaje) =>
        Result.Fallo<TramoPdfExtraido>(Error.Crear(codigo, mensaje));
}

/// <summary>
/// Abre el original y saca de él un tramo. Lo que el divisor no tipa (una excepción de PDFsharp al copiar las páginas
/// o al guardar el tramo, con un PDF que un tercero subió) se convierte aquí en el fallo de ese tramo, igual que en
/// <see cref="CrearDocumentosDesdeDivisionCommandHandler"/>: la pantalla corta la serie y dice qué tramo no salió, y la
/// excepción no llega al circuito de Blazor. El mensaje de la excepción no se registra porque puede reproducir
/// contenido del documento. La cancelación y la falta de memoria no son fallos del tramo y siguen su curso.
/// </summary>
public static class ExtraccionTramoPdf
{
    public static Result<byte[]> Extraer(IDivisorPdf divisor, byte[] contenidoPdf, int paginaInicial, int paginaFinal, ILogger logger)
    {
        try
        {
            var abierto = divisor.Abrir(contenidoPdf);
            if (!abierto.EsExitoso)
                return Result.Fallo<byte[]>(abierto.Error);

            using var pdf = abierto.Valor;
            if (paginaFinal > pdf.NumeroPaginas)
                return Result.Fallo<byte[]>(Error.Crear(ErroresDivisorPdf.RangoInvalido, "Las páginas no coinciden con el PDF."));

            // La cota se aplica mientras se escribe el tramo: la reserva de quien extrae se calcula con ella.
            return pdf.ExtraerPaginas(paginaInicial, paginaFinal, LimitesDivisionPdf.TamanoMaximoTramo(contenidoPdf.Length));
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            logger.LogWarning("Dividir PDF no pudo extraer un tramo para descargarlo ({Excepcion}).", ex.GetType().Name);
            return Result.Fallo<byte[]>(Error.Crear(ErroresDivisorPdf.Ilegible, "No se ha podido preparar este tramo."));
        }
    }
}

/// <summary>Nombre del archivo de un tramo, el mismo al crearlo como Documento que al descargarlo.</summary>
public static class NombresTramoPdf
{
    public static string De(string nombreOriginal, int paginaInicial, int paginaFinal) =>
        $"{Path.GetFileNameWithoutExtension(nombreOriginal)}-pag-{paginaInicial}-{paginaFinal}.pdf";
}
