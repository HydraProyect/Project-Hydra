using CaeManager.Application.Common;
using System.IO.Compression;
using CaeManager.Application.Centros;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Comunicaciones;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Visitas.PaqueteDocumental;

public class PaqueteDocumentalVisitaService(
    IVisitasQueryContext visitasContext,
    ICentrosQueryContext centrosContext,
    IDocumentosQueryContext documentosContext,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    IEmpresasQueryContext empresasContext,
    ITrabajadoresQueryContext trabajadoresContext,
    IConversacionRepository conversacionRepositorio,
    IFileStorageService almacenamiento,
    ILogger<PaqueteDocumentalVisitaService> logger) : IPaqueteDocumentalVisitaService
{
    // Remitente distinto del de ResponderConversacionCommand
    // (equipo-cae@buzon-simulado.local) a propósito — este mensaje no lo
    // escribió ninguna persona, y el hilo debe dejarlo claro.
    private const string RemitenteAutomaticoEmail = "hydra-automatico@sistema.local";

    private record DocumentoParaZipDto(Guid? TrabajadorId, Guid TipoDocumentoId, string ArchivoUrl);
    private record TrabajadorNombreDto(string Nombre, string Apellidos);

    public async Task GenerarYEnviarAsync(Guid visitaId, Guid conversacionId, CancellationToken cancellationToken = default)
    {
        var visita = await visitasContext.Visitas
            .Where(v => v.Id == visitaId)
            .Select(v => new { v.Id, v.CentroId, v.FechaInicio, v.FechaFin })
            .FirstOrDefaultAsync(cancellationToken);

        if (visita is null) return;

        var centro = await centrosContext.Centros
            .Where(c => c.Id == visita.CentroId)
            .Select(c => new { c.Id, c.Nombre, c.EmpresaId })
            .FirstOrDefaultAsync(cancellationToken);

        if (centro is null) return;

        var trabajadorIds = await visitasContext.VisitasTrabajadores
            .Where(vt => vt.VisitaId == visitaId)
            .Select(vt => vt.TrabajadorId)
            .ToListAsync(cancellationToken);

        var documentos = await documentosContext.Documentos
            .Where(d => d.ArchivoUrl != null && (d.EmpresaId == centro.EmpresaId || (d.TrabajadorId != null && trabajadorIds.Contains(d.TrabajadorId.Value))))
            .Select(d => new DocumentoParaZipDto(d.TrabajadorId, d.TipoDocumentoId, d.ArchivoUrl!))
            .ToListAsync(cancellationToken);

        if (documentos.Count == 0)
        {
            logger.LogInformation("Visita {VisitaId}: sin documentos de empresa/trabajadores disponibles, no se genera paquete documental.", visitaId);
            return;
        }

        var tiposDocumentoIds = documentos.Select(d => d.TipoDocumentoId).Distinct().ToList();
        var nombresTipoDocumento = await tiposDocumentoContext.TiposDocumento
            .Where(t => tiposDocumentoIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Nombre })
            .ToDictionaryAsync(t => t.Id, t => t.Nombre, cancellationToken);

        var empresa = await empresasContext.Empresas
            .Where(e => e.Id == centro.EmpresaId)
            .Select(e => new { e.RazonSocial })
            .FirstOrDefaultAsync(cancellationToken);

        var trabajadoresPorId = await trabajadoresContext.Trabajadores
            .Where(t => trabajadorIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Nombre, t.Apellidos })
            .ToDictionaryAsync(t => t.Id, t => new TrabajadorNombreDto(t.Nombre, t.Apellidos), cancellationToken);

        var zipBytes = await ConstruirZipAsync(documentos, nombresTipoDocumento, empresa?.RazonSocial, trabajadoresPorId, cancellationToken);
        if (zipBytes is null) return; // ningún archivo pudo abrirse — no tiene sentido adjuntar un zip vacío.

        var conversacion = await conversacionRepositorio.ObtenerPorIdAsync(conversacionId, cancellationToken);
        if (conversacion is null) return;

        var nombreZip = $"documentacion-visita-{centro.Nombre.Replace(' ', '-')}-{visita.FechaInicio:yyyyMMdd}.zip";
        using var flujoZip = new MemoryStream(zipBytes);
        var archivoUrlZip = await almacenamiento.GuardarAsync(flujoZip, nombreZip, cancellationToken);

        var cuerpo =
            $"""
            <p>Adjuntamos automáticamente la documentación disponible en la plataforma para la visita en <strong>{centro.Nombre}</strong>
            del {visita.FechaInicio:dd/MM/yyyy} al {visita.FechaFin:dd/MM/yyyy} ({documentos.Count} documento(s)).</p>
            """;

        var mensaje = conversacion.AgregarMensaje(DireccionMensaje.Saliente, conversacion.Canal, RemitenteAutomaticoEmail, cuerpo);
        mensaje.AgregarAdjunto(nombreZip, "application/zip", zipBytes.LongLength, archivoUrlZip);
    }

    /// <summary>Devuelve null si ningún documento pudo abrirse (storage inconsistente) — mejor no adjuntar nada que adjuntar un zip vacío.</summary>
    private async Task<byte[]?> ConstruirZipAsync(
        IReadOnlyList<DocumentoParaZipDto> documentos,
        IReadOnlyDictionary<Guid, string> nombresTipoDocumento,
        string? razonSocialEmpresa,
        IReadOnlyDictionary<Guid, TrabajadorNombreDto> trabajadoresPorId,
        CancellationToken cancellationToken)
    {
        using var memoria = new MemoryStream();
        var nombresUsados = new HashSet<string>();
        var algunoAgregado = false;

        using (var zip = new ZipArchive(memoria, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var documento in documentos)
            {
                Stream contenido;
                try
                {
                    contenido = await almacenamiento.AbrirAsync(documento.ArchivoUrl, cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "No se pudo abrir el archivo {ArchivoUrl} para el paquete documental de la visita.", documento.ArchivoUrl);
                    continue;
                }

                await using (contenido)
                {
                    var tipoNombre = nombresTipoDocumento.GetValueOrDefault(documento.TipoDocumentoId, "Documento");
                    var carpeta = documento.TrabajadorId is not null ? "Trabajadores" : "Empresa";
                    var titular = documento.TrabajadorId is not null && trabajadoresPorId.TryGetValue(documento.TrabajadorId.Value, out var trabajador)
                        ? $"{trabajador.Nombre} {trabajador.Apellidos}"
                        : razonSocialEmpresa ?? "Empresa";

                    var extension = Path.GetExtension(documento.ArchivoUrl);
                    var nombreEntrada = SanearNombreEntrada($"{carpeta}/{tipoNombre} - {titular}{extension}", nombresUsados);

                    var entrada = zip.CreateEntry(nombreEntrada, CompressionLevel.Fastest);
                    await using var flujoEntrada = entrada.Open();
                    await contenido.CopyToAsync(flujoEntrada, cancellationToken);
                    algunoAgregado = true;
                }
            }
        }

        return algunoAgregado ? memoria.ToArray() : null;
    }

    private static string SanearNombreEntrada(string nombrePropuesto, HashSet<string> nombresUsados)
    {
        var invalidos = Path.GetInvalidFileNameChars();
        var limpio = string.Concat(nombrePropuesto.Select(c => invalidos.Contains(c) && c != '/' ? '_' : c));

        if (nombresUsados.Add(limpio)) return limpio;

        var extension = Path.GetExtension(limpio);
        var sinExtension = limpio[..^extension.Length];
        var contador = 2;
        string candidato;
        do
        {
            candidato = $"{sinExtension} ({contador}){extension}";
            contador++;
        } while (!nombresUsados.Add(candidato));

        return candidato;
    }
}
