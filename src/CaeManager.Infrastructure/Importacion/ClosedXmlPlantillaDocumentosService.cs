using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Application.Importacion;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Importacion;

/// <summary>
/// Plantilla simplificada de una sola hoja para subir en bloque fechas de
/// emisión de Documentos (ver ROADMAP.md) — a diferencia del formato completo
/// multi-hoja de importación CAE (ClosedXmlImportacionParser), no da de alta
/// Trabajadores ni Tipos de Documento: ambos deben existir ya, se rechaza
/// con motivo explícito si el DNI o el nombre del tipo no coinciden con el
/// catálogo actual.
///
/// Invariante «nada se descarta en silencio» (IMPORTACION.md § 3 bis, DCR-12
/// B; auditada en REC-129): la única fila que este analizador salta sin
/// registrar nada es la fila de ejemplo que <see cref="GenerarPlantilla"/>
/// escribe con el marcador <c>EJEMPLO</c> en el DNI — legítimo y silencioso
/// a propósito, verificado por test. La celda de fecha de emisión vacía y la
/// ilegible tienen motivo propio en <see cref="PlanImportacionDto.Omitidos"/>,
/// distinto entre sí y del de "falta el tipo de documento" — antes las tres
/// causales compartían un único motivo genérico ("Faltan datos
/// obligatorios"), que llegó a acusar de ausente una fecha que en realidad
/// estaba presente pero ilegible (REC-129).
/// </summary>
public class ClosedXmlPlantillaDocumentosService(IDocumentosQueryContext documentosContext, ITiposDocumentoQueryContext tiposDocumentoContext, ITrabajadoresQueryContext trabajadoresContext) : IPlantillaDocumentosService
{
    private const string NombreHoja = "Documentos";
    private const int FilaCabecera = 1;
    private const int PrimeraFilaDatos = 2;
    private const string MarcadorEjemplo = "EJEMPLO";

    public byte[] GenerarPlantilla()
    {
        using var libro = new XLWorkbook();
        var hoja = libro.Worksheets.Add(NombreHoja);

        hoja.Cell(FilaCabecera, 1).Value = "DNI";
        hoja.Cell(FilaCabecera, 2).Value = "Tipo de documento";
        hoja.Cell(FilaCabecera, 3).Value = "Fecha de emisión";
        hoja.Row(FilaCabecera).Style.Font.Bold = true;

        hoja.Cell(PrimeraFilaDatos, 1).Value = "EJEMPLO — Borra esta fila antes de importar";
        hoja.Cell(PrimeraFilaDatos, 2).Value = "Certificado de aptitud médica";
        hoja.Cell(PrimeraFilaDatos, 3).Value = "01/01/2026";

        hoja.Columns().AdjustToContents();

        using var stream = new MemoryStream();
        libro.SaveAs(stream);
        return stream.ToArray();
    }

    public async Task<PlanImportacionDto> AnalizarAsync(Stream archivo, CancellationToken cancellationToken = default)
    {
        var trabajadoresPorDni = await trabajadoresContext.Trabajadores
            .Where(t => t.Dni != null)
            .Select(t => t.Dni!)
            .ToListAsync(cancellationToken);
        var dnisExistentes = new HashSet<string>(trabajadoresPorDni, StringComparer.OrdinalIgnoreCase);

        var nombresTiposDocumentoValidos = new HashSet<string>(
            await tiposDocumentoContext.TiposDocumento.Select(t => t.Nombre).ToListAsync(cancellationToken), StringComparer.OrdinalIgnoreCase);

        var documentosExistentes = (await (
            from documento in documentosContext.Documentos
            join trabajador in trabajadoresContext.Trabajadores on documento.TrabajadorId equals trabajador.Id
            join tipoDocumento in tiposDocumentoContext.TiposDocumento on documento.TipoDocumentoId equals tipoDocumento.Id
            select new { trabajador.Dni, tipoDocumento.Nombre })
            .ToListAsync(cancellationToken))
            .Select(x => (x.Dni, x.Nombre))
            .ToHashSet(DocumentoExistenteComparer.Instancia);

        using var libro = new XLWorkbook(archivo);

        var documentos = new List<DocumentoImportadoDto>();
        var omitidos = new List<ItemImportacionDto>();

        if (!libro.Worksheets.TryGetWorksheet(NombreHoja, out var hoja))
        {
            omitidos.Add(new ItemImportacionDto(NombreHoja, 0, "Hoja completa", "No se encontró la hoja \"Documentos\" en el archivo."));
            return new PlanImportacionDto(Guid.NewGuid(), [], [], [], [], [], [], omitidos);
        }

        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var clavesVistas = new HashSet<(string Dni, string Tipo)>(DocumentoExistenteComparer.Instancia);

        for (var fila = PrimeraFilaDatos; ; fila++)
        {
            var dni = TextoCelda(hoja.Cell(fila, 1))?.ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(dni)) break;

            if (dni.StartsWith(MarcadorEjemplo, StringComparison.OrdinalIgnoreCase))
                continue;

            var tipoDocumento = TextoCelda(hoja.Cell(fila, 2));

            if (string.IsNullOrWhiteSpace(tipoDocumento))
            {
                omitidos.Add(new ItemImportacionDto(NombreHoja, fila, dni, "Falta el tipo de documento."));
                continue;
            }

            var resultadoFecha = FechaCeldaAyudante.Leer(hoja.Cell(fila, 3));
            if (resultadoFecha.Estado == EstadoCeldaFecha.Vacia)
            {
                omitidos.Add(new ItemImportacionDto(NombreHoja, fila, $"{dni} — {tipoDocumento}", "Falta la fecha de emisión."));
                continue;
            }

            if (resultadoFecha.Estado == EstadoCeldaFecha.Ilegible)
            {
                omitidos.Add(new ItemImportacionDto(
                    NombreHoja, fila, $"{dni} — {tipoDocumento}",
                    $"La fecha «{resultadoFecha.ValorBruto}» no se pudo interpretar como una fecha válida; no se importó este documento."));
                continue;
            }

            var fechaEmision = resultadoFecha.Fecha!.Value;

            if (!dnisExistentes.Contains(dni))
            {
                omitidos.Add(new ItemImportacionDto(
                    NombreHoja, fila, dni, "No existe ningún trabajador con este DNI. Da de alta al trabajador antes de importar sus documentos."));
                continue;
            }

            if (!nombresTiposDocumentoValidos.Contains(tipoDocumento))
            {
                omitidos.Add(new ItemImportacionDto(
                    NombreHoja, fila, $"{dni} — {tipoDocumento}", "No existe este tipo de documento en el catálogo del sistema."));
                continue;
            }

            if (fechaEmision > hoy)
            {
                omitidos.Add(new ItemImportacionDto(
                    NombreHoja, fila, $"{dni} — {tipoDocumento}", $"La fecha de emisión ({fechaEmision:dd/MM/yyyy}) es futura; no se puede importar."));
                continue;
            }

            if (!clavesVistas.Add((dni, tipoDocumento)))
            {
                omitidos.Add(new ItemImportacionDto(NombreHoja, fila, $"{dni} — {tipoDocumento}", "Fila duplicada dentro del propio archivo."));
                continue;
            }

            documentos.Add(new DocumentoImportadoDto(
                dni, tipoDocumento, fechaEmision, documentosExistentes.Contains((dni, tipoDocumento))));
        }

        return new PlanImportacionDto(Guid.NewGuid(), [], [], [], documentos, [], [], omitidos);
    }

    private static string? TextoCelda(IXLCell celda)
    {
        if (celda.IsEmpty()) return null;
        var texto = celda.GetString().Trim();
        return string.IsNullOrWhiteSpace(texto) ? null : texto;
    }

    private sealed class DocumentoExistenteComparer : IEqualityComparer<(string Dni, string Nombre)>
    {
        public static readonly DocumentoExistenteComparer Instancia = new();

        public bool Equals((string Dni, string Nombre) x, (string Dni, string Nombre) y) =>
            string.Equals(x.Dni, y.Dni, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Nombre, y.Nombre, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Dni, string Nombre) obj) =>
            HashCode.Combine(obj.Dni.ToUpperInvariant(), obj.Nombre.ToUpperInvariant());
    }
}
