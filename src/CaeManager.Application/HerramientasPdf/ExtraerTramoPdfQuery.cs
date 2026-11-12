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
/// y la Empresa se vuelven a comprobar tras la extracción, justo antes de devolver el tramo: el Tenant activo y la lectura
/// de la Empresa son en vivo; la cartera, con la frescura del alcance ya resuelto en el circuito (como cualquier Query, a
/// diferencia de un Command, que lo resuelve de nuevo). Un alcance sin restricción no sustituye a la comprobación de la
/// Empresa: la cartera no es aislamiento de Tenant.
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

        // El divisor abre el original en un proceso aparte con su límite de memoria y de tiempo: aquí solo se espera.
        var extraido = await ExtraccionTramoPdf.ExtraerAsync(
            divisor, request.ContenidoPdf, request.PaginaInicial, request.PaginaFinal, logger, cancellationToken);
        if (!extraido.EsExitoso)
            return Result.Fallo<TramoPdfExtraido>(extraido.Error);

        // La extracción tarda: si entretanto el selector cambió de Tenant o la Empresa dejó de existir en él, el tramo ya no
        // se entrega. Es la misma comprobación de antes de abrir, repetida justo antes de devolverlo, y lo último que espera
        // este handler; la cartera se mira con el alcance ya resuelto en el circuito, así que una baja de cartera de hace
        // segundos puede no verse aún.
        if (await RechazoDeAccesoAsync(request, cancellationToken) is { } rechazoTrasExtraer)
            return rechazoTrasExtraer;

        return Result.Exito(new TramoPdfExtraido(
            NombresTramoPdf.De(request.NombreOriginal, request.PaginaInicial, request.PaginaFinal), extraido.Valor));
    }

    private async Task<Result<TramoPdfExtraido>?> RechazoDeAccesoAsync(ExtraerTramoPdfQuery request, CancellationToken cancellationToken)
    {
        if (TenantCruzado(request) is { } cruzado)
            return cruzado;

        // La misma comprobación que el catálogo de la división: la Empresa existe en el Tenant (filtro de Tenant y RLS
        // por debajo), es propia y está en el alcance de gestión. Un alcance sin restricción deja pasar cualquier
        // identificador; la lectura de la Empresa no.
        if (await DocumentosUnibles.EmpresaGestionableAsync(request.EmpresaGestionadaId, empresasContext, alcanceDatos, cancellationToken) is null)
            return Fallo(ObtenerCatalogoDivisionPdfQueryHandler.ErrorEmpresaNoGestionable, "La Empresa no existe o queda fuera de tu cartera.");

        // El Tenant activo pudo cambiar mientras se leía la Empresa: se mira otra vez, ya sin ninguna espera detrás.
        return TenantCruzado(request);
    }

    private static Result<TramoPdfExtraido>? TenantCruzado(ExtraerTramoPdfQuery request, Guid? tenantActivo) =>
        tenantActivo is not { } tenant || tenant != request.TenantEsperadoId
            ? Fallo(CrearDocumentosDesdeDivisionCommandHandler.ErrorTenantCruzado,
                "La Empresa ha cambiado. Vuelve a empezar para no descargar documentos de otra.")
            : null;

    private Result<TramoPdfExtraido>? TenantCruzado(ExtraerTramoPdfQuery request) => TenantCruzado(request, tenantActual.TenantId);

    private static Result<TramoPdfExtraido> Fallo(string codigo, string mensaje) =>
        Result.Fallo<TramoPdfExtraido>(Error.Crear(codigo, mensaje));
}

/// <summary>
/// Abre el original y saca de él un tramo. Lo que el divisor no tipa (una excepción de PDFsharp al copiar las páginas
/// o al guardar el tramo, con un PDF que un tercero subió) se convierte aquí en el fallo de ese tramo, igual que en
/// <see cref="CrearDocumentosDesdeDivisionCommandHandler"/>: la pantalla corta la serie y dice qué tramo no salió, y la
/// excepción no llega al circuito de Blazor. El mensaje de la excepción no se registra porque puede reproducir
/// contenido del documento. No son fallos del tramo y siguen su curso: la cancelación, la falta de memoria, un proceso
/// aparte sin turno (<see cref="PresupuestoMemoriaAgotadoException"/>) y uno que no consta terminado
/// (<see cref="EntradaPdfRetenidaException"/>, que pide retener la reserva del original).
/// </summary>
public static class ExtraccionTramoPdf
{
    public static async Task<Result<byte[]>> ExtraerAsync(IDivisorPdf divisor, byte[] contenidoPdf, int paginaInicial, int paginaFinal,
        ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            var abierto = await divisor.AbrirAsync(contenidoPdf, cancellationToken);
            if (!abierto.EsExitoso)
                return Result.Fallo<byte[]>(abierto.Error);

            using var pdf = abierto.Valor;
            if (paginaFinal > pdf.NumeroPaginas)
                return Result.Fallo<byte[]>(Error.Crear(ErroresDivisorPdf.RangoInvalido, "Las páginas no coinciden con el PDF."));

            // La cota se aplica mientras se escribe el tramo: la reserva de quien extrae se calcula con ella.
            return await pdf.ExtraerPaginasAsync(
                paginaInicial, paginaFinal, LimitesDivisionPdf.TamanoMaximoTramo(contenidoPdf.Length), cancellationToken);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException
                                       or PresupuestoMemoriaAgotadoException or EntradaPdfRetenidaException))
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
