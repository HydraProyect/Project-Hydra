using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Queries.ObtenerFirmaGuardadaUsuario;
using CaeManager.Application.Documentos.Queries.ObtenerSelloEmpresa;
using MediatR;

namespace CaeManager.Web.Features.Documentos;

/// <summary>
/// Sirve las imágenes de la firma guardada del usuario y el sello guardado
/// de una Empresa vía endpoints autenticados — mismo criterio que
/// DocumentosEndpoints (IFileStorageService guarda fuera de wwwroot).
/// </summary>
public static class FirmasGuardadasEndpoints
{
    public static IEndpointRouteBuilder MapFirmasGuardadasEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/mi-firma/archivo", ServirFirmaAsync);
        endpoints.MapGet("/empresas/{id:guid}/sello/archivo", ServirSelloAsync);

        return endpoints;
    }

    /// <summary>
    /// La firma manuscrita del usuario actual. Es un <b>instrumento de firma</b>,
    /// no una vista previa cualquiera: quien se hace con el PNG puede estamparlo
    /// en cualquier documento. Por eso declara la misma política de caché que el
    /// PDF de un Documento sensible — ver <see cref="ServirSelloAsync"/> para el
    /// razonamiento completo.
    /// </summary>
    public static async Task<IResult> ServirFirmaAsync(
        HttpContext contexto, IMediator mediator, IFileStorageService almacenamiento,
        CancellationToken cancellationToken)
    {
        var firma = await mediator.Send(new ObtenerFirmaGuardadaUsuarioQuery(), cancellationToken);
        if (firma is null)
            return Results.NotFound();

        CabecerasArchivoSensible.ProhibirCache(contexto);

        // No pasa por IRegistroAccesoDocumentoSensibleService (DEC-36,
        // HO-099-01 § 6-7): FirmaGuardadaUsuario es una imagen de firma,
        // sin TipoDocumentoId — no es un Documento del catálogo.
        var flujo = await almacenamiento.AbrirAsync(firma.ImagenUrl, cancellationToken);
        return Results.File(flujo, "image/png", enableRangeProcessing: true);
    }

    /// <summary>
    /// El sello guardado de una Empresa dentro de la cartera de GESTIÓN del
    /// usuario (la puerta la pone <c>ObtenerSelloEmpresaQuery</c>, REC-153).
    ///
    /// <para>
    /// <b>Por qué declara la cabecera aquí y no se conforma con el middleware.</b>
    /// <c>UseCabecerasSeguridad</c> ya pone <c>no-store, private</c> por defecto
    /// a toda respuesta, así que hoy estas dos imágenes tampoco quedaban en
    /// disco. Pero ese valor por defecto lo pisa cualquier cosa que escriba
    /// <c>Cache-Control</c> más abajo en el pipeline —es exactamente lo que hace
    /// <c>MapStaticAssets()</c>, y es la razón por la que el middleware lo fija
    /// ANTES de <c>siguiente()</c>—, de modo que la política de la respuesta más
    /// sensible de la aplicación dependía del orden de registro en Program.cs y
    /// de que nadie mapeara estas rutas por otra vía. Declararla en el endpoint
    /// la vuelve una propiedad del endpoint, comprobable sin levantar el host
    /// (ver <c>FirmasGuardadasCacheTests</c>), igual que en
    /// <c>DocumentosEndpoints</c> y <c>AuditoriaEndpoints</c>.
    /// </para>
    ///
    /// <para>
    /// <b>Por qué no lleva ETag/Last-Modified.</b> Con <c>no-store</c> el
    /// navegador no puede almacenar la respuesta, así que no hay copia que
    /// revalidar: un <c>ETag</c> sería una cabecera que nadie va a devolver en
    /// un <c>If-None-Match</c>. La frescura la garantiza quien pinta el
    /// <c>&lt;img&gt;</c> añadiendo <c>?v=</c> con <c>ActualizadaEnUtc</c>.
    /// </para>
    /// </summary>
    public static async Task<IResult> ServirSelloAsync(
        Guid id, HttpContext contexto, IMediator mediator, IFileStorageService almacenamiento,
        CancellationToken cancellationToken)
    {
        var sello = await mediator.Send(new ObtenerSelloEmpresaQuery(id), cancellationToken);
        if (sello is null)
            return Results.NotFound();

        CabecerasArchivoSensible.ProhibirCache(contexto);

        // No pasa por IRegistroAccesoDocumentoSensibleService (DEC-36,
        // HO-099-01 § 6-7): mismo motivo que /mi-firma/archivo arriba —
        // SelloEmpresa es una imagen de sello, no un Documento
        // clasificable por TipoDocumento.
        var flujo = await almacenamiento.AbrirAsync(sello.ImagenUrl, cancellationToken);
        return Results.File(flujo, "image/png", enableRangeProcessing: true);
    }
}
