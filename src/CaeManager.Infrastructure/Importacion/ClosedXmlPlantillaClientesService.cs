using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Application.Importacion;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Importacion;

/// <summary>
/// Plantilla simplificada de una sola hoja para dar de alta clientes en masa
/// (ver ROADMAP.md) — a diferencia del formato completo multi-hoja de
/// importación CAE (ClosedXmlImportacionParser). Mismo principio que allí:
/// cada fila es a la vez un Cliente y un Centro con el mismo nombre
/// (ver Centro.cs).
///
/// Cliente ahora exige CIF y Centro exige Empresa (Fase 10) y esta hoja de
/// una sola columna (Cliente/Centro, sin CIF ni Empresa) no recoge ninguno
/// de los dos — así que EjecutarImportacionCommandHandler nunca crea nada
/// desde <see cref="PlanImportacionDto.ClientesCentros"/>: solo reutiliza el
/// Cliente y el Centro que YA existieran. El análisis tiene que prometer
/// exactamente eso: una fila cuyo Cliente o Centro no exista todavía no
/// entra en <c>ClientesCentros</c> — va directa a <see
/// cref="PlanImportacionDto.Omitidos"/> con el mismo motivo que antes solo
/// aparecía al confirmar. "Existe" usa el mismo criterio que la
/// escritura y que <c>ObtenerClientesQuery</c>: Empresa con <c>EsCritico !=
/// null</c> (Cliente empresarial), no cualquier Empresa con ese nombre — una
/// Subcontrata u otra Empresa homónima no cuenta como "el cliente ya existe".
///
/// Invariante «nada se descarta en silencio» (IMPORTACION.md § 3 bis, DCR-12
/// B; auditada en REC-129): la única fila que este analizador salta sin
/// registrar nada es la fila de ejemplo que <see cref="GenerarPlantilla"/>
/// escribe con el marcador <c>EJEMPLO</c> — legítimo y silencioso a
/// propósito, verificado por test. Cualquier otra fila con datos queda en
/// <see cref="PlanImportacionDto.ClientesCentros"/> (Cliente y Centro ya
/// existían, se reutilizan) o en <see cref="PlanImportacionDto.Omitidos"/>
/// con su motivo concreto.
/// </summary>
public class ClosedXmlPlantillaClientesService(ICentrosQueryContext centrosContext, IEmpresasQueryContext empresasContext) : IPlantillaClientesService
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
        // F3b — Empresas, no la tabla legacy Clientes. Cliente empresarial
        // específicamente (EsCritico != null, igual que
        // EjecutarImportacionCommandHandler y ObtenerClientesQuery) — no
        // cualquier Empresa con ese nombre, que podría ser una Subcontrata u
        // otra contraparte homónima.
        var nombresClientesExistentes = new HashSet<string>(
            await empresasContext.Empresas.Where(e => e.EsCritico != null).Select(c => c.RazonSocial).ToListAsync(cancellationToken),
            StringComparer.OrdinalIgnoreCase);
        var nombresCentrosExistentes = new HashSet<string>(
            await centrosContext.Centros.Select(c => c.Nombre).ToListAsync(cancellationToken), StringComparer.OrdinalIgnoreCase);

        using var libro = new XLWorkbook(archivo);

        var clientesCentros = new List<ClienteCentroImportadoDto>();
        var omitidos = new List<ItemImportacionDto>();

        if (!libro.Worksheets.TryGetWorksheet(NombreHoja, out var hoja))
        {
            omitidos.Add(new ItemImportacionDto(NombreHoja, 0, "Hoja completa", "No se encontró la hoja \"Clientes\" en el archivo."));
            return new PlanImportacionDto(Guid.NewGuid(), [], [], [], [], [], [], omitidos);
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

            // Ninguno de los dos existía en la escritura (ver nota de clase, Fase
            // 10): esta plantilla no puede crear el Cliente porque le falta CIF,
            // ni el Centro porque le falta Empresa. El plan no puede prometer una
            // alta que la confirmación se negará a hacer — se omite aquí mismo,
            // con el mismo motivo (y ya con el número de fila real) que antes solo
            // aparecía al confirmar.
            if (!nombresClientesExistentes.Contains(nombre))
            {
                omitidos.Add(new ItemImportacionDto(
                    NombreHoja, fila, nombre,
                    "Este cliente no existe todavía. Ahora requiere un CIF, que esta plantilla no recoge — créalo manualmente en Clientes."));
                continue;
            }

            if (!nombresCentrosExistentes.Contains(nombre))
            {
                omitidos.Add(new ItemImportacionDto(
                    NombreHoja, fila, nombre,
                    "Este centro no existe todavía. Ahora requiere una Empresa asociada, que esta plantilla no recoge — créalo manualmente en Centros."));
                continue;
            }

            clientesCentros.Add(new ClienteCentroImportadoDto(nombre, esCritico, direccion, contacto, YaExisteCliente: true, YaExisteCentro: true));
        }

        return new PlanImportacionDto(Guid.NewGuid(), clientesCentros, [], [], [], [], [], omitidos);
    }

    private static string? TextoCelda(IXLCell celda)
    {
        if (celda.IsEmpty()) return null;
        var texto = celda.GetString().Trim();
        return string.IsNullOrWhiteSpace(texto) ? null : texto;
    }
}
