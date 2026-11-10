using CaeManager.Application.Common;
using CaeManager.Application.Common.Archivos;
using CaeManager.Application.Documentos;
using CaeManager.Application.Documentos.PaqueteDocumental;
using CaeManager.Application.Empresas;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Proyectos;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.HerramientasPdf;

/// <summary>Un PDF de la unión. El orden de la lista de <see cref="UnirPdfQuery.Entradas"/> es el del PDF unido.</summary>
public abstract record EntradaUnionPdf;

/// <summary>Un PDF que ha subido la persona. Su tamaño lo limita <see cref="LimitesArchivoSubido"/>.</summary>
public sealed record EntradaUnionSubida(byte[] Contenido, string NombreArchivo) : EntradaUnionPdf;

/// <summary>El archivo vigente de un Documento del Tenant; se lee aquí, dentro del alcance, y no lo trae la pantalla.</summary>
public sealed record EntradaUnionDocumento(Guid DocumentoId) : EntradaUnionPdf;

/// <summary>
/// Une varios PDF, subidos o tomados de Documentos de una Empresa gestionada, en uno nuevo que se entrega para descargar.
/// No guarda nada: el resultado no es un Documento. Autorización y alcance:
/// <list type="number">
/// <item>Tenant propietario: <paramref name="TenantEsperadoId"/> es el del inicio de la pantalla; si el selector cambió, se rechaza.</item>
/// <item>La Empresa gestionada es una Empresa propia del Tenant dentro de la cartera de quien une (alcance de gestión).</item>
/// <item>Cada Documento tiene que estar en el universo de esa Empresa (ver <see cref="DocumentosUnibles"/>) y ser legible con
/// la misma puerta que abre un Documento suelto (<c>DocumentosLegiblesAsync</c>). Lo que no, responde como inexistente.</item>
/// <item>Rol: solo los que escriben leen documentos para sacarlos de TALVEG (lista blanca, como el paquete de Empresa 360); una
/// Sesión Privilegiada de plataforma no lee Documentos aquí (la misma decisión provisional que ese paquete).</item>
/// </list>
/// DEC-36: cada Documento cuyo archivo se abre cuenta como una apertura y se registra si es sensible, igual que una descarga
/// normal, salga o no el PDF unido (un PDF rechazado o una firma sin confirmar también revelan algo del Documento), y antes de
/// devolver nada. El registro falla en cerrado y es atómico; un Documento que no se llegó a abrir no deja registro.
/// </summary>
/// <param name="ConfirmaPerderFirmas">
/// Si algún PDF lleva firma electrónica, sin esto no se une: se responde <see cref="EstadoUnionPdf.FirmasSinConfirmar"/> con
/// las posiciones afectadas, sin unir nada (los Documentos ya abiertos para comprobarlo sí dejan su registro de apertura).
/// </param>
public record UnirPdfQuery(
    IReadOnlyList<EntradaUnionPdf> Entradas, Guid TenantEsperadoId, Guid EmpresaGestionadaId, bool ConfirmaPerderFirmas)
    : IRequest<Result<ResultadoUnionPdf>>;

public enum EstadoUnionPdf
{
    Unido = 0,

    /// <summary>Hay PDF firmados y no se confirmó: <see cref="ResultadoUnionPdf.Posiciones"/> dice cuáles.</summary>
    FirmasSinConfirmar = 1,

    /// <summary>Un PDF no sirve: <see cref="ResultadoUnionPdf.Posiciones"/> dice cuál y <see cref="ResultadoUnionPdf.CodigoRechazo"/> por qué.</summary>
    EntradaRechazada = 2
}

/// <param name="Posiciones">Posiciones (desde 0) en la lista de entradas a las que se refiere el estado.</param>
public record ResultadoUnionPdf(
    EstadoUnionPdf Estado, byte[]? Contenido, string? NombreArchivo, int NumeroPaginas,
    IReadOnlyList<int> Posiciones, string? CodigoRechazo)
{
    public static ResultadoUnionPdf Rechazada(int posicion, string codigo) =>
        new(EstadoUnionPdf.EntradaRechazada, null, null, 0, [posicion], codigo);

    public static ResultadoUnionPdf ConFirmas(IReadOnlyList<int> posiciones) =>
        new(EstadoUnionPdf.FirmasSinConfirmar, null, null, 0, posiciones, null);
}

public class UnirPdfQueryHandler(
    IDisponibilidadHerramientasPdf disponibilidad,
    ITenantActual tenantActual,
    IEmpresasQueryContext empresasContext,
    IDocumentosQueryContext documentosContext,
    ITrabajadoresQueryContext trabajadoresContext,
    IProyectosQueryContext proyectosContext,
    IAlcanceDatosService alcanceDatos,
    ICurrentUserService currentUserService,
    ISesionPrivilegiadaActual sesionPrivilegiadaActual,
    IFileStorageService almacenamiento,
    IUnidorPdf unidor,
    IRegistroAccesoDocumentoSensibleService registroAcceso,
    ITransaccionDeComando transaccion)
    : IRequestHandler<UnirPdfQuery, Result<ResultadoUnionPdf>>
{
    public async Task<Result<ResultadoUnionPdf>> Handle(UnirPdfQuery request, CancellationToken cancellationToken)
    {
        var abiertos = new HashSet<Guid>();
        var resultado = await UnirAsync(request, abiertos, cancellationToken);
        if (abiertos.Count == 0)
            return resultado;

        // DEC-36: abrir el archivo de un Documento es leerlo, salga o no el PDF unido (un PDF rechazado, una firma sin
        // confirmar o un tope superado también revelan algo de él). Todas las aperturas o ninguna, y antes de devolver
        // nada: sin constancia no hay resultado. La señal de extracción masiva se emite solo tras confirmar (ver
        // DescargarPaqueteEmpresaQueryHandler, del que se copia el mecanismo).
        ILoteAccesosSensibles? lote = null;
        var confirmada = await transaccion.EjecutarAsync(async ct =>
        {
            lote = registroAcceso.IniciarLote();
            foreach (var documentoId in abiertos)
                await lote.RegistrarSiSensibleAsync(documentoId, TipoAccesoDocumentoSensible.Apertura, ct);
            return Result.Exito();
        }, cancellationToken);

        if (confirmada.EsFallido)
            return Result.Fallo<ResultadoUnionPdf>(confirmada.Error);
        if (lote is not null)
            await lote.EmitirSenalesAsync(CancellationToken.None);

        return resultado;
    }

    private async Task<Result<ResultadoUnionPdf>> UnirAsync(UnirPdfQuery request, HashSet<Guid> abiertos, CancellationToken cancellationToken)
    {
        if (!await disponibilidad.EstaDisponibleAsync(cancellationToken))
            return Fallo(ErroresUnirPdf.NoDisponible, "Herramientas PDF no está disponible.");

        if (tenantActual.TenantId is not { } tenant || tenant != request.TenantEsperadoId)
            return Fallo(ErroresUnirPdf.TenantCruzado, "La Empresa gestionada ha cambiado. Vuelve a empezar para no unir documentos de otra.");

        var entradas = request.Entradas;
        if (entradas.Count is < LimitesUnionPdf.MinimoFicheros or > LimitesUnionPdf.MaximoFicheros)
            return Fallo(ErroresUnirPdf.NumeroDeFicherosInvalido, "Hay que unir entre 2 y 20 PDF.");

        var empresa = await DocumentosUnibles.EmpresaGestionableAsync(request.EmpresaGestionadaId, empresasContext, alcanceDatos, cancellationToken);
        if (empresa is null)
            return Fallo(ErroresUnirPdf.EmpresaNoGestionable, "La Empresa gestionada no existe o queda fuera de tu cartera.");

        var documentoIds = entradas.OfType<EntradaUnionDocumento>().Select(e => e.DocumentoId).Distinct().ToList();
        Dictionary<Guid, string> archivos = [];
        if (documentoIds.Count > 0)
        {
            // Tenant propietario y alcance antes de abrir nada. Solo lo que lee Documentos pide rol y descarta la Sesión Privilegiada.
            if (await DocumentosUnibles.RechazoDeLecturaAsync(sesionPrivilegiadaActual, currentUserService, cancellationToken) is { } rechazoDeLectura)
                return Result.Fallo<ResultadoUnionPdf>(rechazoDeLectura);

            archivos = await ArchivosDeLosDocumentosAsync(request.EmpresaGestionadaId, documentoIds, cancellationToken);
        }

        // Se lee y se valida en el orden pedido para que el primer PDF que no sirve sea el que se señala.
        var contenidos = new List<byte[]>(entradas.Count);
        var infos = new List<InfoPdfParaUnir>(entradas.Count);
        var lecturaDeDocumentos = new Dictionary<Guid, byte[]>();
        long total = 0;
        var paginas = 0;

        try
        {
            for (var posicion = 0; posicion < entradas.Count; posicion++)
            {
                var leido = await LeerAsync(entradas[posicion], archivos, lecturaDeDocumentos, abiertos, cancellationToken);
                if (leido.Rechazo is { } rechazo)
                    return Result.Exito(ResultadoUnionPdf.Rechazada(posicion, rechazo));

                var contenido = leido.Contenido!;
                total += contenido.Length;
                if (total > LimitesUnionPdf.TamanoMaximoTotalBytes)
                    return Fallo(ErroresUnirPdf.TotalDemasiadoGrande, "Los PDF pesan juntos más de lo que se puede unir.");

                var info = await Task.Run(() => unidor.Inspeccionar(contenido), cancellationToken);
                if (!info.EsExitoso)
                    return Result.Exito(ResultadoUnionPdf.Rechazada(posicion, CodigoDeEntrada(info.Error.Codigo)));

                paginas += info.Valor.NumeroPaginas;
                if (paginas > LimitesUnionPdf.MaximoPaginasTotales)
                    return Fallo(ErroresUnirPdf.TotalDemasiadasPaginas, "El PDF unido pasaría del máximo de páginas.");

                contenidos.Add(contenido);
                infos.Add(info.Valor);
            }
        }
        catch (PresupuestoMemoriaAgotadoException)
        {
            // Transitorio: sin hueco en el presupuesto de memoria de documentos del proceso. No se registra acceso alguno.
            return Fallo(ErroresUnirPdf.SinEspacio, "No hay memoria libre para unir ahora mismo. Inténtalo de nuevo en unos segundos.");
        }

        var firmados = infos.Select((info, posicion) => (info, posicion)).Where(x => x.info.EstaFirmado).Select(x => x.posicion).ToList();
        if (firmados.Count > 0 && !request.ConfirmaPerderFirmas)
            return Result.Exito(ResultadoUnionPdf.ConFirmas(firmados));

        var unido = await Task.Run(() => unidor.Unir(contenidos), cancellationToken);
        if (!unido.EsExitoso)
            return Result.Exito(ResultadoUnionPdf.Rechazada(0, CodigoDeEntrada(unido.Error.Codigo)));

        var nombre = $"{ArmadoPaqueteDocumental.SanearSegmento(empresa.RazonSocial).Replace(' ', '-')}_unido_{DiaDeNegocio.Hoy():yyyy-MM-dd}.pdf";
        return Result.Exito(new ResultadoUnionPdf(EstadoUnionPdf.Unido, unido.Valor.Contenido, nombre, unido.Valor.NumeroPaginas, [], null));
    }

    /// <summary>Id de Documento a clave de su archivo, solo de los que están en el universo de la Empresa y que esta sesión puede leer.</summary>
    private async Task<Dictionary<Guid, string>> ArchivosDeLosDocumentosAsync(
        Guid empresaId, List<Guid> documentoIds, CancellationToken cancellationToken)
    {
        var trabajadorIds = await DocumentosUnibles.TrabajadorIdsAsync(empresaId, trabajadoresContext, alcanceDatos, cancellationToken);
        var enUniverso = await DocumentosUnibles.De(documentosContext, empresaId, trabajadorIds)
            .Where(d => documentoIds.Contains(d.Id))
            .Select(d => new { d.Id, ArchivoUrl = d.ArchivoUrl! })
            .ToListAsync(cancellationToken);

        var legibles = await alcanceDatos.DocumentosLegiblesAsync(
            enUniverso.Select(d => d.Id).ToList(), documentosContext, proyectosContext, cancellationToken);

        return enUniverso.Where(d => legibles.Contains(d.Id)).ToDictionary(d => d.Id, d => d.ArchivoUrl);
    }

    private async Task<(byte[]? Contenido, string? Rechazo)> LeerAsync(
        EntradaUnionPdf entrada, Dictionary<Guid, string> archivos, Dictionary<Guid, byte[]> yaLeidos, HashSet<Guid> abiertos,
        CancellationToken cancellationToken)
    {
        switch (entrada)
        {
            case EntradaUnionSubida subida:
                var rechazo = ValidacionPdfSubidoParaUnir.Rechazo(subida.Contenido, subida.NombreArchivo);
                return rechazo is null ? (subida.Contenido, null) : (null, rechazo);

            case EntradaUnionDocumento documento:
                if (yaLeidos.TryGetValue(documento.DocumentoId, out var repetido))
                    return (repetido, null);
                if (!archivos.TryGetValue(documento.DocumentoId, out var clave))
                    return (null, ErroresUnirPdf.DocumentoNoDisponible);

                byte[] contenido;
                try
                {
                    await using var flujo = await almacenamiento.AbrirAsync(clave, cancellationToken);
                    abiertos.Add(documento.DocumentoId);
                    // Lo almacenado puede pasar del tope por archivo de una subida (un PDF unificado), pero nunca del total.
                    if (flujo.CanSeek && flujo.Length > LimitesUnionPdf.TamanoMaximoTotalBytes)
                        return (null, ErroresUnirPdf.DemasiadoGrande);
                    using var memoria = new MemoryStream();
                    await flujo.CopyToAsync(memoria, cancellationToken);
                    contenido = memoria.ToArray();
                }
                catch (FileNotFoundException)
                {
                    return (null, ErroresUnirPdf.ArchivoNoDisponible);
                }

                if (contenido.Length == 0 || !ValidadorFirmaArchivo.TieneFirmaValida(contenido, "documento.pdf"))
                    return (null, ErroresUnirPdf.NoEsPdf);

                yaLeidos[documento.DocumentoId] = contenido;
                return (contenido, null);

            default:
                return (null, ErroresUnirPdf.NoEsPdf);
        }
    }

    private static string CodigoDeEntrada(string codigo) => codigo switch
    {
        ErroresUnirPdf.ConContrasena or ErroresUnirPdf.DemasiadoGrande or ErroresUnirPdf.DemasiadasPaginas => codigo,
        _ => ErroresUnirPdf.Ilegible,
    };

    private static Result<ResultadoUnionPdf> Fallo(string codigo, string mensaje) =>
        Result.Fallo<ResultadoUnionPdf>(Error.Crear(codigo, mensaje));
}
