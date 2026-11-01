using CaeManager.Web.Components.DesignSystem;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentoPorId;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Application.Importacion;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Exportacion;
using CaeManager.Web.Services;
using ClosedXML.Excel;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Net.Http.Headers;

namespace CaeManager.Web.Features.Documentos;

/// <summary>
/// Sirve el PDF adjunto de un Documento vía un endpoint autenticado — nunca
/// como archivo estático público, precisamente porque IFileStorageService
/// guarda fuera de wwwroot (ver Project-Hydra-Negocio/tecnico/ARCHITECTURE.md, "Archivos").
/// </summary>
public static class DocumentosEndpoints
{
    public static IEndpointRouteBuilder MapDocumentosEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Solo sesión interactiva: el iframe del visor y los enlaces «Ver PDF». La extensión de navegador
        // no entra por aquí con su token: recibe el archivo por /extension/acreditaciones/{id}/archivo,
        // ligado a una Acreditación pendiente, con límite de ritmo y kill switch comprobados en el servidor
        // (EntregaArchivoExtensionEndpoints). ObtenerDocumentoPorIdQuery filtra por Tenant y por alcance de
        // cartera con el rol real de quien pide.
        endpoints.MapGet("/documentos/{id:guid}/archivo", ServirArchivoAsync)
            .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = IdentityConstants.ApplicationScheme });

        // Solo se enlaza desde /importacion (Importacion.razor: Roles = Administrador)
        // — mismo motivo que el RequireAuthorization de /clientes/plantilla.xlsx,
        // aunque aquí el archivo es una plantilla en blanco sin datos de tenant.
        endpoints.MapGet("/documentos/plantilla.xlsx", (IPlantillaDocumentosService servicio) =>
            Results.File(
                servicio.GenerarPlantilla(),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                "plantilla-documentos.xlsx"))
        .RequireAuthorization(policy => policy.RequireRole(CaeManager.Infrastructure.Identity.Roles.Administrador))
        .ExcluidoDelEncargoDeAdministracion();

        // Mismo patrón de referencia que ClientesEndpoints. Sin columna de
        // Plataformas/Acreditaciones: ObtenerDocumentosQueryHandler la
        // resuelve con una segunda pasada fuera de la página, y repetirla
        // por cada lote de PaginadorExportacion sería un N+1.
        // Lleva el listado completo de Documentos del tenant (propietario,
        // tipo, fechas): mismo criterio de caché que el PDF.
        // Sin SoloRolesDelListado(), como /trabajadores/exportar.xlsx: Documentos.razor
        // declara [Authorize] sin roles y el menú del rol Cliente la ofrece
        // (CatalogoSeccionesMenu.RolesDeDocumentos, AdmisionDeDestinos.RutasDelMenuDeCliente).
        // Quien exporta el listado es quien lo ve; el alcance lo aplica ObtenerDocumentosQuery,
        // la misma consulta de la página.
        endpoints.MapGet("/documentos/exportar.xlsx", ExportarAsync);

        return endpoints;
    }

    public static async Task<IResult> ExportarAsync(
        HttpContext contexto, IMediator mediator, ITenantActual tenantActual, IRegistroExportacionService registroExportacion,
        CancellationToken cancellationToken,
        string? q = null, string? ambito = null, string? estado = null, string? orden = null, bool desc = false,
        string? tipo = null, string? plataforma = null, string? propietario = null, string? agrupar = null)
    {
        // Estado 4a: la página no muestra los Documentos del Tenant de origen a quien tiene que
        // elegir una empresa de su cartera (ContextoEmpresaActiva.ResolverAsync); el endpoint no
        // los consulta, no genera el archivo ni deja rastro. Misma guarda que Trabajadores.
        var autorizados = await mediator.Send(new ObtenerClientesAutorizadosQuery(), cancellationToken);
        if (ClientesAutorizados.PideElegirEmpresa(autorizados, tenantActual.TenantId))
            return Results.Redirect("/documentos");

        CabecerasArchivoSensible.ProhibirCache(contexto);

        using var libro = new XLWorkbook();
        var hoja = libro.Worksheets.Add("Documentos");

        hoja.Cell(1, 1).Value = "Propietario";
        hoja.Cell(1, 2).Value = "Ámbito";
        hoja.Cell(1, 3).Value = "Tipo de documento";
        hoja.Cell(1, 4).Value = "Emisión";
        hoja.Cell(1, 5).Value = "Vencimiento";
        hoja.Cell(1, 6).Value = "Estado";
        hoja.Row(1).Style.Font.Bold = true;

        var fila = 2;
        await foreach (var documento in PaginadorExportacion.PaginarAsync((pagina, tamanoPagina) =>
            mediator.Send(
                new ObtenerDocumentosQuery(
                    TrabajadorId: null,
                    Ambito: Enum.TryParse<AmbitoAplicacion>(ambito, out var ambitoFiltro) ? ambitoFiltro : null,
                    Busqueda: string.IsNullOrWhiteSpace(q) ? null : q,
                    // La selección de la franja de estado: varios nombres separados por coma.
                    Estado: null,
                    Estados: SeleccionEstados.Separar<EstadoDocumento>(estado) is { Count: > 0 } estados ? estados : null,
                    Pagina: pagina,
                    TamanoPagina: tamanoPagina,
                    OrdenarPor: string.IsNullOrWhiteSpace(orden) ? null : orden,
                    Descendente: desc,
                    // Un valor que no es un Id se ignora, igual que en el listado: no es un 400.
                    PropietarioId: Guid.TryParse(propietario, out var entidadAsociadaId) ? entidadAsociadaId : null,
                    TipoDocumentoId: Guid.TryParse(tipo, out var tipoFiltro) ? tipoFiltro : null,
                    ProveedorPlataformaCaeId: Guid.TryParse(plataforma, out var plataformaFiltro) ? plataformaFiltro : null,
                    AgruparPorAmbito: agrupar == "ambito"),
                cancellationToken)))
        {
            hoja.Cell(fila, 1).Value = documento.PropietarioNombre;
            hoja.Cell(fila, 2).Value = documento.Ambito.ToString();
            hoja.Cell(fila, 3).Value = documento.TipoDocumentoNombre;
            hoja.Cell(fila, 4).Value = documento.FechaEmision.ToDateTime(TimeOnly.MinValue);
            if (documento.FechaVencimiento is not null)
                hoja.Cell(fila, 5).Value = documento.FechaVencimiento.Value.ToDateTime(TimeOnly.MinValue);
            hoja.Cell(fila, 6).Value = EstadoDocumentoUi.Texto(documento.Estado);
            fila++;
        }

        hoja.Columns().AdjustToContents();

        var stream = new MemoryStream();
        libro.SaveAs(stream);
        stream.Position = 0;

        try
        {
            var estadosAplicados = SeleccionEstados.Separar<EstadoDocumento>(estado);
            await registroExportacion.RegistrarAsync(
                nameof(Documento), fila - 2,
                CriteriosExportacion.Desde(
                    ("busqueda", string.IsNullOrWhiteSpace(q) ? null : "true"),
                    ("ambito", Enum.TryParse<AmbitoAplicacion>(ambito, out var ambitoAplicado) && Enum.IsDefined(ambitoAplicado) ? ambitoAplicado.ToString() : null),
                    ("estado", estadosAplicados.Count > 0 ? string.Join(",", estadosAplicados) : null),
                    ("tipo", Guid.TryParse(tipo, out var tipoAplicado) ? tipoAplicado.ToString() : null),
                    ("plataforma", Guid.TryParse(plataforma, out var plataformaAplicada) ? plataformaAplicada.ToString() : null),
                    ("propietario", Guid.TryParse(propietario, out var propietarioAplicado) ? propietarioAplicado.ToString() : null),
                    ("agrupar", agrupar == "ambito" ? "ambito" : null),
                    ("orden", CriteriosExportacion.SoloNombre(orden)), ("desc", desc ? "true" : null)),
                cancellationToken);
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }

        return Results.File(
            stream,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "documentos.xlsx");
    }

    /// <summary>
    /// Manejador de <c>GET /documentos/{id}/archivo</c>. Método con nombre, y no una lambda, para
    /// poder probarlo sin levantar el host (mismo criterio que <c>LogoTenantEndpoints.ServirAsync</c>).
    /// </summary>
    public static async Task<IResult> ServirArchivoAsync(
        Guid id, HttpContext contexto, IMediator mediator, IFileStorageService almacenamiento,
        IRegistroAccesoDocumentoSensibleService registroAcceso, ILoggerFactory fabricaRegistro,
        CancellationToken cancellationToken)
    {
        var abierto = await AbrirConRegistroAsync(
            id, TipoAccesoDocumentoSensible.Apertura, contexto, mediator, almacenamiento, registroAcceso, fabricaRegistro, cancellationToken);
        if (abierto.Rechazo is not null)
            return abierto.Rechazo;

        var documento = abierto.Documento!;

        // Se muestra en el navegador, no se descarga: VisorDocumento lo incrusta en un <iframe> y los
        // enlaces «Ver PDF» lo abren en una pestaña. Pasar el nombre a Results.File como
        // fileDownloadName habría añadido Content-Disposition: attachment (el defecto que forzaba la
        // descarga); aquí va en una cabecera `inline` propia, que el ejecutor del resultado respeta
        // porque no recibe nombre. Quien quiere descargar sigue pudiendo: el enlace «Descargar PDF» del
        // panel del Documento lleva el atributo `download`, y el navegador lo aplica sobre `inline`.
        if (!string.IsNullOrWhiteSpace(documento.NombreArchivoDescarga))
        {
            var disposicion = new ContentDispositionHeaderValue("inline");
            disposicion.SetHttpFileName(documento.NombreArchivoDescarga);
            contexto.Response.Headers.ContentDisposition = disposicion.ToString();
        }
        else
        {
            contexto.Response.Headers.ContentDisposition = "inline";
        }

        // X-Frame-Options: DENY y frame-ancestors 'none' son el valor por defecto de toda la
        // aplicación (UseCabecerasSeguridad). Solo esta respuesta se relaja, y solo al mismo origen.
        contexto.PermitirIncrustacionEnMismoOrigen();

        // enableRangeProcessing: el visor de PDF del navegador pide por
        // rangos al paginar/buscar en vez de volver a traer el archivo
        // entero en cada petición. No reduce el coste del servidor —
        // DiskFileStorageService ya descifra el archivo completo en
        // memoria antes de servirlo (medición de Módulo 2, PR #360) — la
        // lectura por rangos que sí lo haría exige un formato cifrado por
        // bloques, pendiente de decisión.
        return Results.File(abierto.Flujo!, "application/pdf", enableRangeProcessing: true);
    }

    /// <summary>
    /// Lo que comparte todo lo que sirve el archivo vigente de un Documento (esta ruta y la entrega a la extensión):
    /// lectura con alcance, apertura y registro de acceso. Devuelve el flujo abierto y el Documento, o la respuesta de
    /// rechazo.
    /// </summary>
    internal static async Task<(Stream? Flujo, DocumentoDetalleDto? Documento, IResult? Rechazo)> AbrirConRegistroAsync(
        Guid id, TipoAccesoDocumentoSensible tipoAcceso, HttpContext contexto, IMediator mediator, IFileStorageService almacenamiento,
        IRegistroAccesoDocumentoSensibleService registroAcceso, ILoggerFactory fabricaRegistro,
        CancellationToken cancellationToken)
    {
        DocumentoDetalleDto? documento;
        try
        {
            documento = await mediator.Send(new ObtenerDocumentoPorIdQuery(id), cancellationToken);
        }
        catch (Exception ex) when (ErroresLecturaSensible.EsContextoNoAutorizado(ex))
        {
            return (null, null, ErroresLecturaSensible.Responder(contexto, ex, id,
                fabricaRegistro.CreateLogger(typeof(DocumentosEndpoints).FullName!)));
        }
        if (documento?.ArchivoUrl is null)
            return (null, null, Results.NotFound());

        CabecerasArchivoSensible.ProhibirCache(contexto);

        // DEC-36 (REC-099): se abre primero y se registra después de que
        // AbrirAsync confirme que el archivo existe — si el blob no
        // estuviera (storage inconsistente), no queda un registro de un
        // acceso que nunca entregó contenido (Codex, HO-099-01).
        Stream flujo;
        try
        {
            flujo = await almacenamiento.AbrirAsync(documento.ArchivoUrl, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            // El Documento existe y dice tener adjunto, pero el almacén no lo tiene: 404, como los
            // demás endpoints de contenido (AuditoriaEndpoints, LogoTenantEndpoints), y no la
            // excepción sin controlar. A diferencia de ellos se avisa, porque aquí es una
            // inconsistencia que alguien tiene que mirar. El aviso lleva el Id del Documento y no la
            // excepción ni la clave del fichero: la ruta del almacén no sale ni en la respuesta ni
            // en el registro.
            fabricaRegistro.CreateLogger(typeof(DocumentosEndpoints).FullName!).LogWarning(
                "El Documento {DocumentoId} tiene adjunto registrado pero su fichero no está en el almacén; se responde 404.",
                documento.Id);
            return (null, null, Results.NotFound());
        }

        // Falla en cerrado: ningún rechazo ni fallo de registro entrega contenido.
        try
        {
            await registroAcceso.RegistrarSiSensibleAsync(documento.Id, tipoAcceso, cancellationToken);
        }
        catch (Exception ex) when (ErroresLecturaSensible.EsMapeable(ex))
        {
            await flujo.DisposeAsync();
            return (null, null, ErroresLecturaSensible.Responder(contexto, ex, documento.Id,
                fabricaRegistro.CreateLogger(typeof(DocumentosEndpoints).FullName!)));
        }
        catch
        {
            await flujo.DisposeAsync();
            throw;
        }

        return (flujo, documento, null);
    }

}
