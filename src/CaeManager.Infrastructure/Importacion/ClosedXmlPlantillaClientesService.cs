using CaeManager.Application.Common;
using CaeManager.Application.Importacion;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Importacion;

/// <summary>
/// Plantilla simplificada de un solo hoja para dar de alta clientes en masa
/// (ver ROADMAP.md) — a diferencia del Cuadro de Control CAE de KHS
/// (ClosedXmlImportacionParser), que es multi-hoja y específico de ese
/// origen. Mismo principio que allí: cada fila es a la vez un Cliente y un
/// Centro con el mismo nombre (ver Centro.cs).
/// </summary>
public class ClosedXmlPlantillaClientesService(IApplicationDbContext dbContext) : IPlantillaClientesService
{
    private const string NombreHoja = "Clientes";
    private const int FilaCabecera = 1;
    private const int PrimeraFilaDatos = 2;
    private const string MarcadorEjemplo = "EJEMPLO";

    public byte[] GenerarPlantilla()
    {
        using var libro = new XLWorkbook();
        var hoja = libro.Worksheets.Add(NombreHoja);

        hoja.Cell(FilaCabecera, 1).Value = "Cliente / Centro";
        hoja.Cell(FilaCabecera, 2).Value = "Crítico (C/N)";
        hoja.Cell(FilaCabecera, 3).Value = "Dirección";
        hoja.Cell(FilaCabecera, 4).Value = "Contacto";
        hoja.Row(FilaCabecera).Style.Font.Bold = true;

        hoja.Cell(PrimeraFilaDatos, 1).Value = "EJEMPLO — Borra esta fila antes de importar";
        hoja.Cell(PrimeraFilaDatos, 2).Value = "N";
        hoja.Cell(PrimeraFilaDatos, 3).Value = "Calle Ejemplo 1, Ciudad";
        hoja.Cell(PrimeraFilaDatos, 4).Value = "Nombre Apellidos — email@ejemplo.com";

        hoja.Columns().AdjustToContents();

        using var stream = new MemoryStream();
        libro.SaveAs(stream);
        return stream.ToArray();
    }

    public async Task<PlanImportacionDto> AnalizarAsync(Stream archivo, CancellationToken cancellationToken = default)
    {
        var nombresClientesExistentes = new HashSet<string>(
            await dbContext.Clientes.Select(c => c.RazonSocial).ToListAsync(cancellationToken), StringComparer.OrdinalIgnoreCase);
        var nombresCentrosExistentes = new HashSet<string>(
            await dbContext.Centros.Select(c => c.Nombre).ToListAsync(cancellationToken), StringComparer.OrdinalIgnoreCase);

        using var libro = new XLWorkbook(archivo);

        var clientesCentros = new List<ClienteCentroImportadoDto>();
        var omitidos = new List<ItemImportacionDto>();

        if (!libro.Worksheets.TryGetWorksheet(NombreHoja, out var hoja))
        {
            omitidos.Add(new ItemImportacionDto(NombreHoja, 0, "Hoja completa", "No se encontró la hoja \"Clientes\" en el archivo."));
            return new PlanImportacionDto([], [], [], [], [], [], omitidos);
        }

        var nombresVistos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var fila = PrimeraFilaDatos; ; fila++)
        {
            var nombre = TextoCelda(hoja.Cell(fila, 1));
            if (string.IsNullOrWhiteSpace(nombre)) break;

            if (nombre.StartsWith(MarcadorEjemplo, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!nombresVistos.Add(nombre))
            {
                omitidos.Add(new ItemImportacionDto(NombreHoja, fila, nombre, "Nombre duplicado dentro del propio archivo."));
                continue;
            }

            var esCritico = string.Equals(TextoCelda(hoja.Cell(fila, 2)), "C", StringComparison.OrdinalIgnoreCase);
            var direccion = TextoCelda(hoja.Cell(fila, 3));
            var contacto = TextoCelda(hoja.Cell(fila, 4));

            clientesCentros.Add(new ClienteCentroImportadoDto(
                nombre, esCritico, direccion, contacto,
                nombresClientesExistentes.Contains(nombre), nombresCentrosExistentes.Contains(nombre)));
        }

        return new PlanImportacionDto(clientesCentros, [], [], [], [], [], omitidos);
    }

    private static string? TextoCelda(IXLCell celda)
    {
        if (celda.IsEmpty()) return null;
        var texto = celda.GetString().Trim();
        return string.IsNullOrWhiteSpace(texto) ? null : texto;
    }
}
