using CaeManager.Application.Common;
using CaeManager.Application.Documentos.PaqueteAcreditacion;
using CaeManager.Application.Empresas;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Documentos.Queries.ObtenerPaqueteAcreditacionEmpresa;

/// <summary>Una fila de <c>00-Indice.xlsx</c>: una por documento considerado, incluido o no.</summary>
public record FilaIndicePaquete(
    string Ambito,
    string Titular,
    string? DniNie,
    string Tipo,
    DateOnly Emision,
    string Vigencia,
    Guid DocumentoId,
    string? Ruta,
    string Resultado,
    string? Advertencia);

/// <summary>Vocabulario del índice (contenido del fichero exportado, no interfaz): cabeceras y resultados en un solo sitio.</summary>
public static class IndicePaquete
{
    public const string NombreHoja = "Indice";
    public const string ResultadoIncluido = "incluido";
    public const string ResultadoTopeTamano = "excluido: tope de tamaño del paquete";
    public const string ResultadoArchivoNoDisponible = "excluido: archivo no disponible";

    public static readonly IReadOnlyList<string> Cabeceras =
    [
        "Ámbito", "Titular", "DNI/NIE", "Tipo de documento", "Emisión", "Vigencia",
        "Id del documento", "Ruta en el paquete", "Resultado", "Advertencia"
    ];
}

public record EntradaPaquete(Guid DocumentoId, string ArchivoUrl, string Ruta, int IndiceFila);

public record PaqueteAcreditacionDto(
    string NombreRaiz,
    DateOnly Fecha,
    IReadOnlyList<EntradaPaquete> Entradas,
    IReadOnlyList<FilaIndicePaquete> Filas)
{
    public string NombreZip => NombreRaiz + ".zip";
}

/// <summary>
/// Paquete de acreditación de una Empresa: sus documentos y los de sus Trabajadores, solo vigentes
/// y uno por titular y tipo (<see cref="SeleccionPaqueteAcreditacion"/>). Devuelve null si la
/// Empresa no existe o está fuera del alcance del actor — igual que el Documento individual, que
/// no distingue «no existe» de «no es tuyo». Los Trabajadores se limitan a los visibles en la
/// cartera. Vehículos y Proyectos quedan fuera por ahora.
/// </summary>
public record ObtenerPaqueteAcreditacionEmpresaQuery(Guid EmpresaId) : IRequest<PaqueteAcreditacionDto?>;

public class ObtenerPaqueteAcreditacionEmpresaQueryHandler(
    IDocumentosQueryContext documentosContext,
    IEmpresasQueryContext empresasContext,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    ITrabajadoresQueryContext trabajadoresContext,
    IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerPaqueteAcreditacionEmpresaQuery, PaqueteAcreditacionDto?>
{
    private const string CarpetaEmpresa = "01-Documentacion de Empresa";
    private const string CarpetaTrabajadores = "02-Documentacion de Trabajadores";

    // Los cuatro básicos llevan 01-04 en la carpeta de cada Trabajador; el resto va después,
    // por TipoDocumento.Orden, para que ningún número se repita.
    private const int DesplazamientoNoBasicos = 4;

    public async Task<PaqueteAcreditacionDto?> Handle(ObtenerPaqueteAcreditacionEmpresaQuery request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.EmpresaVisibleAsync(request.EmpresaId, cancellationToken))
            return null;

        var razonSocial = await empresasContext.Empresas
            .Where(e => e.Id == request.EmpresaId)
            .Select(e => e.RazonSocial)
            .FirstOrDefaultAsync(cancellationToken);
        if (razonSocial is null)
            return null;

        var visibles = await alcanceDatos.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken);
        var trabajadores = await trabajadoresContext.Trabajadores
            .Where(t => t.EmpresaId == request.EmpresaId)
            .Where(t => visibles == null || visibles.Contains(t.Id))
            .Select(t => new { t.Id, t.Nombre, t.Apellidos, t.Dni })
            .ToListAsync(cancellationToken);
        var trabajadorIds = trabajadores.Select(t => t.Id).ToList();

        var tipos = await tiposDocumentoContext.TiposDocumento
            .Select(t => new { t.Id, t.Nombre, t.Orden })
            .ToDictionaryAsync(t => t.Id, cancellationToken);

        var filas = await documentosContext.Documentos
            .Where(d => d.EmpresaId == request.EmpresaId || (d.TrabajadorId != null && trabajadorIds.Contains(d.TrabajadorId.Value)))
            .Select(d => new
            {
                d.Id, d.EmpresaId, d.TrabajadorId, d.TipoDocumentoId, d.EstadoVigencia,
                d.FechaVencimiento, d.FechaEmision, d.CreadoEnUtc, d.ArchivoUrl
            })
            .ToListAsync(cancellationToken);

        var hoy = DiaDeNegocio.Hoy();
        var candidatos = filas
            .Where(f => tipos.ContainsKey(f.TipoDocumentoId))
            .Select(f => new DocumentoCandidatoPaquete(
                f.Id, f.TrabajadorId ?? f.EmpresaId!.Value, f.TipoDocumentoId, f.EstadoVigencia,
                f.FechaVencimiento, f.FechaEmision, f.CreadoEnUtc, f.ArchivoUrl))
            .ToList();
        var porId = filas.ToDictionary(f => f.Id);
        var decisiones = SeleccionPaqueteAcreditacion.Seleccionar(candidatos, hoy);

        // Carpeta por Trabajador, sin DNI/NIE; dos con el mismo nombre se distinguen por orden de Id.
        var carpetas = new Dictionary<Guid, string>();
        var usadas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in trabajadores.OrderBy(t => t.Id))
        {
            var baseNombre = Recortar(NombreArchivoDocumento.Limpiar($"{t.Apellidos} {t.Nombre}", "Sin nombre"), 80);
            var nombre = baseNombre;
            for (var n = 2; !usadas.Add(nombre); n++)
                nombre = $"{baseNombre} ({n})";
            carpetas[t.Id] = nombre;
        }
        var trabajadorPorId = trabajadores.ToDictionary(t => t.Id);

        var rutasUsadas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entradas = new List<EntradaPaquete>();
        var indice = new List<FilaIndicePaquete>();

        foreach (var decision in decisiones
                     .OrderBy(d => porId[d.Documento.Id].TrabajadorId is null ? 0 : 1)
                     .ThenBy(d => porId[d.Documento.Id].TrabajadorId is { } tid ? carpetas[tid] : "", StringComparer.OrdinalIgnoreCase)
                     .ThenBy(d => tipos[d.Documento.TipoDocumentoId].Orden)
                     .ThenBy(d => d.Documento.FechaEmision)
                     .ThenBy(d => d.Documento.Id))
        {
            var d = decision.Documento;
            var fila = porId[d.Id];
            var tipo = tipos[d.TipoDocumentoId];
            var esTrabajador = fila.TrabajadorId is not null;
            var titular = esTrabajador
                ? $"{trabajadorPorId[fila.TrabajadorId!.Value].Apellidos} {trabajadorPorId[fila.TrabajadorId!.Value].Nombre}"
                : razonSocial;

            string? ruta = null;
            if (decision.Incluido)
            {
                var orden = esTrabajador ? NumeroDeCarpetaTrabajador(tipo.Nombre, tipo.Orden) : tipo.Orden;
                var nombreFichero = NombreArchivoDocumento.Entrada(orden, tipo.Nombre, d.FechaEmision, decision.Ordinal);
                var carpeta = esTrabajador ? $"{CarpetaTrabajadores}/{carpetas[fila.TrabajadorId!.Value]}" : CarpetaEmpresa;
                ruta = $"{carpeta}/{nombreFichero}";
                for (var n = 2; !rutasUsadas.Add(ruta); n++)
                    ruta = $"{carpeta}/{Path.GetFileNameWithoutExtension(nombreFichero)}_dup{n}{Path.GetExtension(nombreFichero)}";
                entradas.Add(new EntradaPaquete(d.Id, d.ArchivoUrl!, ruta, indice.Count));
            }

            indice.Add(new FilaIndicePaquete(
                esTrabajador ? "Trabajador" : "Empresa",
                titular,
                esTrabajador ? trabajadorPorId[fila.TrabajadorId!.Value].Dni : null,
                tipo.Nombre, d.FechaEmision, TextoVigencia(d, hoy), d.Id, ruta,
                decision.Incluido ? IndicePaquete.ResultadoIncluido : "excluido: " + Motivo(decision.Motivo!.Value),
                decision.VigenciaSinConfirmar ? "vigencia sin confirmar" : null));
        }

        var raiz = $"{Recortar(NombreArchivoDocumento.Limpiar(razonSocial, "Empresa"), 80)} - Documentacion - {hoy:yyyy-MM-dd}";
        return new PaqueteAcreditacionDto(raiz, hoy, entradas, indice);
    }

    // Los cuatro básicos por nombre normalizado (el catálogo no los marca). La misma lista vive en
    // DocumentacionBaseTrabajador.Clasificar (PR del panel): se unifican cuando ambas estén en main.
    private static int NumeroDeCarpetaTrabajador(string tipoNombre, int orden) =>
        NombreArchivoDocumento.Limpiar(tipoNombre, "").ToLowerInvariant().Replace(".", "") switch
        {
            "certificado de aptitud medica" or "reconocimiento medico" or "aptitud medica" => 1,
            "formacion art 19" => 2,
            "informacion art 18" => 3,
            "entrega de epi" => 4,
            _ => Math.Min(orden + DesplazamientoNoBasicos, 99)
        };

    private static string Recortar(string texto, int maximo) =>
        texto.Length <= maximo ? texto : texto[..maximo].TrimEnd('.', ' ', '-');

    private static string Motivo(MotivoExclusionPaquete motivo) => motivo switch
    {
        MotivoExclusionPaquete.Vencido => "vencido",
        MotivoExclusionPaquete.NoEsLaVersionMasReciente => "no es la versión más reciente de su tipo",
        MotivoExclusionPaquete.SinArchivo => "sin archivo adjunto",
        _ => "motivo no previsto"
    };

    private static string TextoVigencia(DocumentoCandidatoPaquete d, DateOnly hoy) => d.EstadoVigencia switch
    {
        EstadoVigenciaDocumento.NoCaduca => "No caduca",
        EstadoVigenciaDocumento.SinConfirmar => "Sin confirmar",
        _ when d.FechaVencimiento is { } vence && vence < hoy => $"Vencido el {vence:dd/MM/yyyy}",
        _ when d.FechaVencimiento is { } vence => $"Vence el {vence:dd/MM/yyyy}",
        _ => "Sin confirmar"
    };
}
