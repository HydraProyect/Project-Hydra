using CaeManager.Application.Common;
using CaeManager.Application.Asignaciones;
using CaeManager.Application.Centros;
using CaeManager.Application.Clientes;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Application.Importacion;
using CaeManager.Domain.Common;
using CaeManager.Domain.Trabajadores;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Importacion;

/// <summary>
/// Lee el archivo Excel de importación CAE multi-hoja (ver ROADMAP.md,
/// Fase 5). El formato de origen es deliberadamente heterogéneo (columnas
/// agrupadas por tipo de documento, texto libre fusionando Cliente+Centro,
/// bloques de notas incrustados debajo de tablas) — este parser no intenta
/// "adivinar" lo ambiguo: lo importa de forma segura con una simplificación
/// documentada, o lo excluye con un motivo explícito en
/// <see cref="PlanImportacionDto.Advertencias"/>/<see cref="PlanImportacionDto.Omitidos"/>.
/// Nunca lanza una excepción por una fila individual mal formada — solo por
/// un archivo que no tiene ninguna de las hojas esperadas.
/// </summary>
public class ClosedXmlImportacionParser(IAsignacionesQueryContext asignacionesContext, ICentrosQueryContext centrosContext, IClientesQueryContext clientesContext, IDocumentosQueryContext documentosContext, IEmpresasQueryContext empresasContext, ITiposDocumentoQueryContext tiposDocumentoContext, ITrabajadoresQueryContext trabajadoresContext) : IExcelImportacionParser
{
    private const string HojaCentros = "Centros_Plataformas";
    private const string HojaEmpleados = "Empleados";
    private const string HojaExtranjeros = "Extranjeros (Ibertec GmbH)";
    private const string HojaAsignaciones = "Asignaciones";

    private const string EmpresaEmpleados = "Ibertec S.A.";
    private const string EmpresaExtranjeros = "Ibertec GmbH";

    // Columna del "Fecha" de cada grupo de documento en Empleados/Extranjeros
    // → nombre exacto del TipoDocumento semilla (ver Fase 0 / TipoDocumentoSeedData).
    private static readonly (int Columna, string TipoDocumento)[] ColumnasDocumentos =
    [
        (7, "Apto médico laboral"),           // G: Fecha
        (10, "EPIS (firma)"),                 // J: Fecha firma
        (13, "Formación 60h (base convenio)"),// M: Fecha
        (14, "Formación 20h"),                // N: Fecha
        (15, "Formación 6h"),                 // O: Fecha
        (16, "Reciclaje 4h"),                 // P: Fecha
        (19, "Información Art. 18"),          // S: Fecha
        (20, "Formación Art. 19"),            // T: Fecha
        (23, "Carretillas elevadoras"),       // W: Fecha
        (24, "PEMP (plataformas elevadoras)"),// X: Fecha
        (25, "LOTO (4h)"),                    // Y: Fecha
        (26, "Seguridad alimentaria"),        // Z: Fecha
        (27, "Primeros auxilios"),            // AA: Fecha
        (28, "Espacios confinados"),          // AB: Fecha
        (29, "Trabajos en altura (8h)"),      // AC: Fecha
    ];

    private static readonly string[] MarcadoresRevisionManual = ["genérico", "sin confirmar", "todos los centros"];

    public async Task<PlanImportacionDto> AnalizarAsync(Stream archivo, CancellationToken cancellationToken = default)
    {
        using var libro = new XLWorkbook(archivo);

        var nombresClientesExistentes = new HashSet<string>(
            await clientesContext.Clientes.Select(c => c.RazonSocial).ToListAsync(cancellationToken), StringComparer.OrdinalIgnoreCase);
        var nombresCentrosExistentes = new HashSet<string>(
            await centrosContext.Centros.Select(c => c.Nombre).ToListAsync(cancellationToken), StringComparer.OrdinalIgnoreCase);
        var razonesSocialesExistentes = new HashSet<string>(
            await empresasContext.Empresas.Select(e => e.RazonSocial).ToListAsync(cancellationToken), StringComparer.OrdinalIgnoreCase);
        var dnisExistentes = new HashSet<string>(
            await trabajadoresContext.Trabajadores.Select(t => t.Dni).ToListAsync(cancellationToken), StringComparer.OrdinalIgnoreCase);
        var nombresTiposDocumentoValidos = new HashSet<string>(
            await tiposDocumentoContext.TiposDocumento.Select(t => t.Nombre).ToListAsync(cancellationToken), StringComparer.OrdinalIgnoreCase);

        // Pares (DNI, tipo de documento) / (DNI, centro) que ya existen en la base
        // de datos — solo para que la vista previa muestre "nuevos" con precisión;
        // EjecutarImportacionCommand vuelve a comprobar esto de forma autoritativa
        // en el momento de escribir, por si el estado cambió entre analizar y confirmar.
        var documentosExistentes = (await (
            from documento in documentosContext.Documentos
            join trabajador in trabajadoresContext.Trabajadores on documento.TrabajadorId equals trabajador.Id
            join tipoDocumento in tiposDocumentoContext.TiposDocumento on documento.TipoDocumentoId equals tipoDocumento.Id
            select new { trabajador.Dni, tipoDocumento.Nombre })
            .ToListAsync(cancellationToken))
            .Select(x => (x.Dni, x.Nombre))
            .ToHashSet();

        var asignacionesActivasExistentes = (await (
            from asignacion in asignacionesContext.Asignaciones
            join trabajador in trabajadoresContext.Trabajadores on asignacion.TrabajadorId equals trabajador.Id
            join centro in centrosContext.Centros on asignacion.CentroId equals centro.Id
            where asignacion.FechaBaja == null
            select new { trabajador.Dni, centro.Nombre })
            .ToListAsync(cancellationToken))
            .Select(x => (x.Dni, x.Nombre))
            .ToHashSet();

        var advertencias = new List<ItemImportacionDto>();
        var omitidos = new List<ItemImportacionDto>();

        var clientesCentros = AnalizarCentros(libro, nombresClientesExistentes, nombresCentrosExistentes, advertencias, omitidos);
        var nombresCentrosOrdenados = clientesCentros.Select(c => c.Nombre).ToList();

        var empresas = new List<EmpresaImportadaDto>();
        var trabajadores = new List<TrabajadorImportadoDto>();
        var documentos = new List<DocumentoImportadoDto>();
        var dnisVistosEnArchivo = new HashSet<string>();
        var nombreCompletoADni = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        AnalizarHojaTrabajadores(
            libro, HojaEmpleados, EmpresaEmpleados, razonesSocialesExistentes, dnisExistentes, dnisVistosEnArchivo,
            nombresTiposDocumentoValidos, documentosExistentes, nombreCompletoADni, empresas, trabajadores, documentos, advertencias, omitidos);
        AnalizarHojaTrabajadores(
            libro, HojaExtranjeros, EmpresaExtranjeros, razonesSocialesExistentes, dnisExistentes, dnisVistosEnArchivo,
            nombresTiposDocumentoValidos, documentosExistentes, nombreCompletoADni, empresas, trabajadores, documentos, advertencias, omitidos);

        var asignaciones = AnalizarAsignaciones(
            libro, nombreCompletoADni, nombresCentrosOrdenados, asignacionesActivasExistentes, advertencias, omitidos);

        return new PlanImportacionDto(clientesCentros, empresas, trabajadores, documentos, asignaciones, advertencias, omitidos);
    }

    private static List<ClienteCentroImportadoDto> AnalizarCentros(
        XLWorkbook libro,
        HashSet<string> nombresClientesExistentes,
        HashSet<string> nombresCentrosExistentes,
        List<ItemImportacionDto> advertencias,
        List<ItemImportacionDto> omitidos)
    {
        var resultado = new List<ClienteCentroImportadoDto>();

        if (!libro.Worksheets.TryGetWorksheet(HojaCentros, out var hoja))
        {
            omitidos.Add(new ItemImportacionDto(HojaCentros, 0, "Hoja completa", "No se encontró la hoja en el archivo."));
            return resultado;
        }

        var nombresVistos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var fila = 5; ; fila++)
        {
            var nombre = TextoCelda(hoja.Cell(fila, 2));
            if (string.IsNullOrWhiteSpace(nombre)) break;

            if (!nombresVistos.Add(nombre))
            {
                omitidos.Add(new ItemImportacionDto(HojaCentros, fila, nombre, "Nombre de cliente/centro duplicado dentro del propio archivo."));
                continue;
            }

            var esCritico = string.Equals(TextoCelda(hoja.Cell(fila, 1)), "C", StringComparison.OrdinalIgnoreCase);
            var contacto = TextoCelda(hoja.Cell(fila, 7));
            var direccion = TextoCelda(hoja.Cell(fila, 9));

            if (MarcadoresRevisionManual.Any(m => nombre.Contains(m, StringComparison.OrdinalIgnoreCase)))
            {
                advertencias.Add(new ItemImportacionDto(
                    HojaCentros, fila, nombre,
                    "El nombre indica un centro genérico, sin confirmar, o que fusiona varios centros reales. " +
                    "Se importó tal cual (un Cliente y un Centro con este mismo nombre) — revisa manualmente si conviene " +
                    "desglosarlo en varios Centros después de importar."));
            }

            resultado.Add(new ClienteCentroImportadoDto(
                nombre, esCritico, direccion, contacto,
                nombresClientesExistentes.Contains(nombre), nombresCentrosExistentes.Contains(nombre)));
        }

        return resultado;
    }

    private static void AnalizarHojaTrabajadores(
        XLWorkbook libro,
        string nombreHoja,
        string nombreEmpresa,
        HashSet<string> razonesSocialesExistentes,
        HashSet<string> dnisExistentes,
        HashSet<string> dnisVistosEnArchivo,
        HashSet<string> nombresTiposDocumentoValidos,
        HashSet<(string Dni, string TipoDocumento)> documentosExistentes,
        Dictionary<string, string> nombreCompletoADni,
        List<EmpresaImportadaDto> empresas,
        List<TrabajadorImportadoDto> trabajadores,
        List<DocumentoImportadoDto> documentos,
        List<ItemImportacionDto> advertencias,
        List<ItemImportacionDto> omitidos)
    {
        if (!libro.Worksheets.TryGetWorksheet(nombreHoja, out var hoja))
        {
            omitidos.Add(new ItemImportacionDto(nombreHoja, 0, "Hoja completa", "No se encontró la hoja en el archivo."));
            return;
        }

        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var huboAlMenosUnTrabajador = false;

        for (var fila = 4; ; fila++)
        {
            var numero = hoja.Cell(fila, 1);
            if (numero.IsEmpty() || !numero.TryGetValue<int>(out _)) break;

            var nombre = TextoCelda(hoja.Cell(fila, 2));
            var apellidos = TextoCelda(hoja.Cell(fila, 3));

            if (string.IsNullOrWhiteSpace(nombre) && string.IsNullOrWhiteSpace(apellidos))
                continue; // Fila de plantilla sin datos todavía (p. ej. Extranjeros, hoy vacía).

            var dni = TextoCelda(hoja.Cell(fila, 4))?.Trim().ToUpperInvariant();

            if (string.IsNullOrWhiteSpace(nombre) || string.IsNullOrWhiteSpace(apellidos) || string.IsNullOrWhiteSpace(dni))
            {
                omitidos.Add(new ItemImportacionDto(nombreHoja, fila, $"{nombre} {apellidos}".Trim(), "Faltan datos obligatorios (nombre, apellidos o DNI)."));
                continue;
            }

            if (!DniValido(dni))
            {
                omitidos.Add(new ItemImportacionDto(nombreHoja, fila, $"{nombre} {apellidos} ({dni})", "El documento de identidad no es un DNI, NIE o CIF válido."));
                continue;
            }

            if (!dnisVistosEnArchivo.Add(dni))
            {
                omitidos.Add(new ItemImportacionDto(nombreHoja, fila, $"{nombre} {apellidos} ({dni})", "DNI duplicado dentro del propio archivo."));
                continue;
            }

            if (!huboAlMenosUnTrabajador)
            {
                empresas.Add(new EmpresaImportadaDto(nombreEmpresa, razonesSocialesExistentes.Contains(nombreEmpresa)));
                huboAlMenosUnTrabajador = true;
            }

            var fechaNacimiento = FechaCelda(hoja.Cell(fila, 5));
            var email = TextoCelda(hoja.Cell(fila, 6));

            trabajadores.Add(new TrabajadorImportadoDto(
                nombreEmpresa, nombre.Trim(), apellidos.Trim(), dni, fechaNacimiento, email, dnisExistentes.Contains(dni)));

            nombreCompletoADni[$"{nombre.Trim()} {apellidos.Trim()}"] = dni;

            foreach (var (columna, tipoDocumento) in ColumnasDocumentos)
            {
                var fechaEmision = FechaCelda(hoja.Cell(fila, columna));
                if (fechaEmision is null) continue;

                if (!nombresTiposDocumentoValidos.Contains(tipoDocumento))
                {
                    advertencias.Add(new ItemImportacionDto(
                        nombreHoja, fila, $"{nombre} {apellidos} — {tipoDocumento}",
                        "No existe este tipo de documento en el catálogo del sistema; se omitió este documento."));
                    continue;
                }

                if (fechaEmision > hoy)
                {
                    omitidos.Add(new ItemImportacionDto(
                        nombreHoja, fila, $"{nombre} {apellidos} — {tipoDocumento}",
                        $"La fecha de emisión ({fechaEmision:dd/MM/yyyy}) es futura; no se puede importar."));
                    continue;
                }

                documentos.Add(new DocumentoImportadoDto(
                    dni, tipoDocumento, fechaEmision.Value, documentosExistentes.Contains((dni, tipoDocumento))));
            }
        }
    }

    private static List<AsignacionImportadaDto> AnalizarAsignaciones(
        XLWorkbook libro,
        Dictionary<string, string> nombreCompletoADni,
        IReadOnlyList<string> nombresCentrosOrdenados,
        HashSet<(string Dni, string Centro)> asignacionesActivasExistentes,
        List<ItemImportacionDto> advertencias,
        List<ItemImportacionDto> omitidos)
    {
        var resultado = new List<AsignacionImportadaDto>();

        if (!libro.Worksheets.TryGetWorksheet(HojaAsignaciones, out var hoja))
        {
            omitidos.Add(new ItemImportacionDto(HojaAsignaciones, 0, "Hoja completa", "No se encontró la hoja en el archivo."));
            return resultado;
        }

        const int filaCabecera = 4;
        const int primeraColumnaCentro = 4;

        var columnasCentro = new List<int>();
        for (var columna = primeraColumnaCentro; ; columna++)
        {
            var encabezado = TextoCelda(hoja.Cell(filaCabecera, columna));
            if (string.IsNullOrWhiteSpace(encabezado) || encabezado.Equals("TOTAL CENTROS", StringComparison.OrdinalIgnoreCase))
                break;
            columnasCentro.Add(columna);
        }

        // La hoja Asignaciones usa nombres abreviados de centro en su cabecera,
        // distintos del nombre completo de Centros_Plataformas (p. ej. "Cadena Iberia"
        // vs "Cadena Industrial Iberia S.A. - Planta Norte").
        // La propia hoja documenta la correspondencia por POSICIÓN, no por texto
        // ("Las columnas corresponden al mismo orden que la hoja
        // Centros_Plataformas") — la correspondencia se valida por conteo.
        if (columnasCentro.Count != nombresCentrosOrdenados.Count)
        {
            omitidos.Add(new ItemImportacionDto(
                HojaAsignaciones, filaCabecera, "Todas las columnas de centro",
                $"Asignaciones tiene {columnasCentro.Count} columnas de centro pero Centros_Plataformas tiene " +
                $"{nombresCentrosOrdenados.Count} filas — no se puede emparejar por posición con seguridad. No se importó ninguna asignación."));
            return resultado;
        }

        var columnaANombreCentroReal = columnasCentro
            .Select((columna, indice) => (columna, nombreReal: nombresCentrosOrdenados[indice]))
            .ToDictionary(x => x.columna, x => x.nombreReal);

        for (var fila = filaCabecera + 1; ; fila++)
        {
            var numero = hoja.Cell(fila, 1);
            if (numero.IsEmpty() || !numero.TryGetValue<int>(out _)) break;

            var nombre = TextoCelda(hoja.Cell(fila, 2))?.Trim();
            var apellidos = TextoCelda(hoja.Cell(fila, 3))?.Trim();
            if (string.IsNullOrWhiteSpace(nombre) || string.IsNullOrWhiteSpace(apellidos)) continue;

            var nombreCompleto = $"{nombre} {apellidos}";
            if (!nombreCompletoADni.TryGetValue(nombreCompleto, out var dni))
            {
                omitidos.Add(new ItemImportacionDto(
                    HojaAsignaciones, fila, nombreCompleto, "No se encontró este trabajador en las hojas Empleados/Extranjeros."));
                continue;
            }

            foreach (var columna in columnasCentro)
            {
                var marcado = TextoCelda(hoja.Cell(fila, columna));
                if (string.IsNullOrWhiteSpace(marcado)) continue;

                var nombreCentro = columnaANombreCentroReal[columna];
                resultado.Add(new AsignacionImportadaDto(dni, nombreCentro, asignacionesActivasExistentes.Contains((dni, nombreCentro))));
            }
        }

        return resultado;
    }

    private static bool DniValido(string dni)
    {
        if (dni.Length < Trabajador.LongitudMinimaDni || dni.Length > Trabajador.LongitudMaximaDni)
            return false;

        var resultado = ValidadorIdentificacion.Analizar(dni);
        return resultado.EsValido
            || resultado.Tipo is not (TipoIdentificacion.Dni or TipoIdentificacion.Nie or TipoIdentificacion.NifEmpresa);
    }

    private static string? TextoCelda(IXLCell celda)
    {
        if (celda.IsEmpty()) return null;
        var texto = celda.GetString().Trim();
        return string.IsNullOrWhiteSpace(texto) ? null : texto;
    }

    private static DateOnly? FechaCelda(IXLCell celda)
    {
        if (celda.IsEmpty()) return null;
        return celda.TryGetValue<DateTime>(out var fecha) ? DateOnly.FromDateTime(fecha) : null;
    }
}
