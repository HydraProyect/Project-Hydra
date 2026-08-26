using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Application.Trabajadores;
using CaeManager.Application.Importacion;
using CaeManager.Domain.Common;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Importacion;

/// <summary>
/// Plantilla combinada de 4 hojas (Clientes, Empresas, Centros,
/// Trabajadores) — a diferencia de ClosedXmlPlantillaClientesService (Fase
/// 7), esta sí recoge CIF y Empresa, así que puede crear Clientes y Centros
/// nuevos de verdad (ver ROADMAP.md, "Fuera de alcance" de Fase 10). Las
/// referencias entre hojas son por nombre (Cliente/Empresa en texto), no
/// por Id — más cómodo de rellenar a mano en Excel — y se resuelven contra
/// la base de datos y contra las propias hojas anteriores del archivo.
/// </summary>
public class ClosedXmlPlantillaCombinadaService(ICentrosQueryContext centrosContext, IEmpresasQueryContext empresasContext, ITrabajadoresQueryContext trabajadoresContext) : IPlantillaCombinadaService
{
    private const string HojaClientes = "Clientes";
    private const string HojaEmpresas = "Empresas";
    private const string HojaCentros = "Centros";
    private const string HojaTrabajadores = "Trabajadores";
    private const int FilaCabecera = 1;
    private const int PrimeraFilaDatos = 2;
    private const string MarcadorEjemplo = "EJEMPLO";
    private const char SeparadorLista = ';';

    /// <summary>
    /// Separador interno para componer una clave única (Cliente+Centro) al
    /// comparar contra la base de datos y contra el propio archivo — un
    /// carácter de control que nunca puede aparecer en un nombre real,
    /// evita colisiones que sí podría dar una simple concatenación
    /// ("AB"+"C" == "A"+"BC").
    /// </summary>
    private const string ClaveSeparador = "";

    public byte[] GenerarPlantilla()
    {
        using var libro = new XLWorkbook();

        var hojaClientes = libro.Worksheets.Add(HojaClientes);
        hojaClientes.Cell(FilaCabecera, 1).Value = "Razón social";
        hojaClientes.Cell(FilaCabecera, 2).Value = "CIF";
        hojaClientes.Cell(FilaCabecera, 3).Value = "Crítico (C/N)";
        hojaClientes.Row(FilaCabecera).Style.Font.Bold = true;
        hojaClientes.Cell(PrimeraFilaDatos, 1).Value = "EJEMPLO — Borra esta fila antes de importar";
        hojaClientes.Cell(PrimeraFilaDatos, 2).Value = "B12345674";
        hojaClientes.Cell(PrimeraFilaDatos, 3).Value = "N";
        hojaClientes.Columns().AdjustToContents();

        var hojaEmpresas = libro.Worksheets.Add(HojaEmpresas);
        hojaEmpresas.Cell(FilaCabecera, 1).Value = "Razón social";
        hojaEmpresas.Cell(FilaCabecera, 2).Value = "Clientes asociados (separados por ;)";
        hojaEmpresas.Row(FilaCabecera).Style.Font.Bold = true;
        hojaEmpresas.Cell(PrimeraFilaDatos, 1).Value = "EJEMPLO — Borra esta fila antes de importar";
        hojaEmpresas.Cell(PrimeraFilaDatos, 2).Value = "Cliente Ejemplo S.A.;Otro Cliente S.L.";
        hojaEmpresas.Columns().AdjustToContents();

        var hojaCentros = libro.Worksheets.Add(HojaCentros);
        hojaCentros.Cell(FilaCabecera, 1).Value = "Nombre";
        hojaCentros.Cell(FilaCabecera, 2).Value = "Cliente";
        hojaCentros.Cell(FilaCabecera, 3).Value = "Empresa";
        hojaCentros.Cell(FilaCabecera, 4).Value = "Código";
        hojaCentros.Cell(FilaCabecera, 5).Value = "Dirección";
        hojaCentros.Cell(FilaCabecera, 6).Value = "Contacto";
        hojaCentros.Cell(FilaCabecera, 7).Value = "Contrato vigente hasta";
        hojaCentros.Row(FilaCabecera).Style.Font.Bold = true;
        hojaCentros.Cell(PrimeraFilaDatos, 1).Value = "EJEMPLO — Borra esta fila antes de importar";
        hojaCentros.Cell(PrimeraFilaDatos, 2).Value = "Cliente Ejemplo S.A.";
        hojaCentros.Cell(PrimeraFilaDatos, 3).Value = "Empresa Ejemplo S.L.";
        hojaCentros.Columns().AdjustToContents();

        var hojaTrabajadores = libro.Worksheets.Add(HojaTrabajadores);
        hojaTrabajadores.Cell(FilaCabecera, 1).Value = "Nombre";
        hojaTrabajadores.Cell(FilaCabecera, 2).Value = "Apellidos";
        hojaTrabajadores.Cell(FilaCabecera, 3).Value = "DNI";
        hojaTrabajadores.Cell(FilaCabecera, 4).Value = "Empresa";
        hojaTrabajadores.Cell(FilaCabecera, 5).Value = "Fecha de nacimiento";
        hojaTrabajadores.Cell(FilaCabecera, 6).Value = "Email";
        hojaTrabajadores.Row(FilaCabecera).Style.Font.Bold = true;
        hojaTrabajadores.Cell(PrimeraFilaDatos, 1).Value = "EJEMPLO — Borra esta fila antes de importar";
        hojaTrabajadores.Cell(PrimeraFilaDatos, 3).Value = "12345678Z";
        hojaTrabajadores.Cell(PrimeraFilaDatos, 4).Value = "Empresa Ejemplo S.L.";
        hojaTrabajadores.Columns().AdjustToContents();

        using var stream = new MemoryStream();
        libro.SaveAs(stream);
        return stream.ToArray();
    }

    public async Task<PlanImportacionCombinadaDto> AnalizarAsync(Stream archivo, CancellationToken cancellationToken = default)
    {
        // F3b — Empresas, no la tabla legacy Clientes: la unicidad de Cif y
        // RazonSocial ya es global sobre esa tabla, así que comprobar aquí
        // contra Empresas es lo que de verdad evita el 23505 al guardar.
        var clientesExistentes = await empresasContext.Empresas.ToListAsync(cancellationToken);
        var cifsExistentes = new HashSet<string>(
            clientesExistentes.Where(c => c.Cif is not null).Select(c => c.Cif!), StringComparer.OrdinalIgnoreCase);
        var nombresClientesDisponibles = new HashSet<string>(clientesExistentes.Select(c => c.RazonSocial), StringComparer.OrdinalIgnoreCase);

        var nombresEmpresasExistentes = new HashSet<string>(
            await empresasContext.Empresas.Select(e => e.RazonSocial).ToListAsync(cancellationToken), StringComparer.OrdinalIgnoreCase);
        var nombresEmpresasDisponibles = new HashSet<string>(nombresEmpresasExistentes, StringComparer.OrdinalIgnoreCase);

        var centrosExistentes = new HashSet<string>(
            (await (from centro in centrosContext.Centros
                    join cliente in empresasContext.Empresas on centro.ClienteId equals cliente.Id
                    select cliente.RazonSocial + ClaveSeparador + centro.Nombre)
                .ToListAsync(cancellationToken)),
            StringComparer.OrdinalIgnoreCase);

        var dnisExistentes = new HashSet<string>(
            await trabajadoresContext.Trabajadores.Select(t => t.Dni).ToListAsync(cancellationToken), StringComparer.OrdinalIgnoreCase);

        using var libro = new XLWorkbook(archivo);

        var advertencias = new List<ItemImportacionDto>();
        var omitidos = new List<ItemImportacionDto>();

        var clientes = AnalizarClientes(libro, cifsExistentes, omitidos);
        foreach (var c in clientes) nombresClientesDisponibles.Add(c.RazonSocial);

        var empresas = AnalizarEmpresas(libro, nombresEmpresasExistentes, nombresClientesDisponibles, advertencias, omitidos);
        foreach (var e in empresas) nombresEmpresasDisponibles.Add(e.RazonSocial);

        var centros = AnalizarCentros(libro, nombresClientesDisponibles, nombresEmpresasDisponibles, centrosExistentes, omitidos);
        var trabajadores = AnalizarTrabajadores(libro, nombresEmpresasDisponibles, dnisExistentes, omitidos);

        return new PlanImportacionCombinadaDto(clientes, empresas, centros, trabajadores, advertencias, omitidos);
    }

    private static List<ClienteImportadoDto> AnalizarClientes(
        XLWorkbook libro, HashSet<string> cifsExistentes, List<ItemImportacionDto> omitidos)
    {
        var resultado = new List<ClienteImportadoDto>();

        if (!libro.Worksheets.TryGetWorksheet(HojaClientes, out var hoja))
        {
            omitidos.Add(new ItemImportacionDto(HojaClientes, 0, "Hoja completa", "No se encontró la hoja \"Clientes\" en el archivo."));
            return resultado;
        }

        var cifsVistos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var fila = PrimeraFilaDatos; ; fila++)
        {
            var razonSocial = TextoCelda(hoja.Cell(fila, 1));
            if (string.IsNullOrWhiteSpace(razonSocial)) break;
            if (razonSocial.StartsWith(MarcadorEjemplo, StringComparison.OrdinalIgnoreCase)) continue;

            var cif = TextoCelda(hoja.Cell(fila, 2))?.Trim().ToUpperInvariant();

            if (string.IsNullOrWhiteSpace(cif))
            {
                omitidos.Add(new ItemImportacionDto(HojaClientes, fila, razonSocial, "Falta el CIF."));
                continue;
            }

            var validacion = ValidadorIdentificacion.Analizar(cif);
            if (validacion.Tipo != TipoIdentificacion.NifEmpresa || !validacion.EsValido)
            {
                omitidos.Add(new ItemImportacionDto(HojaClientes, fila, razonSocial, $"El CIF \"{cif}\" no es válido."));
                continue;
            }

            if (!cifsVistos.Add(cif))
            {
                omitidos.Add(new ItemImportacionDto(HojaClientes, fila, razonSocial, "CIF duplicado dentro del propio archivo."));
                continue;
            }

            var esCritico = string.Equals(TextoCelda(hoja.Cell(fila, 3)), "C", StringComparison.OrdinalIgnoreCase);

            resultado.Add(new ClienteImportadoDto(razonSocial, cif, esCritico, cifsExistentes.Contains(cif)));
        }

        return resultado;
    }

    private static List<EmpresaCombinadaImportadaDto> AnalizarEmpresas(
        XLWorkbook libro,
        HashSet<string> nombresEmpresasExistentes,
        HashSet<string> nombresClientesDisponibles,
        List<ItemImportacionDto> advertencias,
        List<ItemImportacionDto> omitidos)
    {
        var resultado = new List<EmpresaCombinadaImportadaDto>();

        if (!libro.Worksheets.TryGetWorksheet(HojaEmpresas, out var hoja))
        {
            omitidos.Add(new ItemImportacionDto(HojaEmpresas, 0, "Hoja completa", "No se encontró la hoja \"Empresas\" en el archivo."));
            return resultado;
        }

        var nombresVistos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var fila = PrimeraFilaDatos; ; fila++)
        {
            var razonSocial = TextoCelda(hoja.Cell(fila, 1));
            if (string.IsNullOrWhiteSpace(razonSocial)) break;
            if (razonSocial.StartsWith(MarcadorEjemplo, StringComparison.OrdinalIgnoreCase)) continue;

            if (!nombresVistos.Add(razonSocial))
            {
                omitidos.Add(new ItemImportacionDto(HojaEmpresas, fila, razonSocial, "Razón social duplicada dentro del propio archivo."));
                continue;
            }

            var clientesTexto = TextoCelda(hoja.Cell(fila, 2));
            var clientesAsociados = new List<string>();

            if (!string.IsNullOrWhiteSpace(clientesTexto))
            {
                foreach (var nombreCliente in clientesTexto.Split(SeparadorLista, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    if (nombresClientesDisponibles.Contains(nombreCliente))
                    {
                        clientesAsociados.Add(nombreCliente);
                    }
                    else
                    {
                        advertencias.Add(new ItemImportacionDto(
                            HojaEmpresas, fila, $"{razonSocial} — {nombreCliente}",
                            $"No se encontró el cliente \"{nombreCliente}\" (ni en el sistema ni en la hoja Clientes de este archivo) — se omitió esta asociación."));
                    }
                }
            }

            resultado.Add(new EmpresaCombinadaImportadaDto(razonSocial, clientesAsociados, nombresEmpresasExistentes.Contains(razonSocial)));
        }

        return resultado;
    }

    private static List<CentroImportadoDto> AnalizarCentros(
        XLWorkbook libro,
        HashSet<string> nombresClientesDisponibles,
        HashSet<string> nombresEmpresasDisponibles,
        HashSet<string> centrosExistentes,
        List<ItemImportacionDto> omitidos)
    {
        var resultado = new List<CentroImportadoDto>();

        if (!libro.Worksheets.TryGetWorksheet(HojaCentros, out var hoja))
        {
            omitidos.Add(new ItemImportacionDto(HojaCentros, 0, "Hoja completa", "No se encontró la hoja \"Centros\" en el archivo."));
            return resultado;
        }

        var clavesVistas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var fila = PrimeraFilaDatos; ; fila++)
        {
            var nombre = TextoCelda(hoja.Cell(fila, 1));
            if (string.IsNullOrWhiteSpace(nombre)) break;
            if (nombre.StartsWith(MarcadorEjemplo, StringComparison.OrdinalIgnoreCase)) continue;

            var cliente = TextoCelda(hoja.Cell(fila, 2));
            var empresa = TextoCelda(hoja.Cell(fila, 3));

            if (string.IsNullOrWhiteSpace(cliente) || string.IsNullOrWhiteSpace(empresa))
            {
                omitidos.Add(new ItemImportacionDto(HojaCentros, fila, nombre, "Faltan datos obligatorios (cliente o empresa)."));
                continue;
            }

            if (!nombresClientesDisponibles.Contains(cliente))
            {
                omitidos.Add(new ItemImportacionDto(
                    HojaCentros, fila, nombre, $"No se encontró el cliente \"{cliente}\" (ni en el sistema ni en la hoja Clientes de este archivo)."));
                continue;
            }

            if (!nombresEmpresasDisponibles.Contains(empresa))
            {
                omitidos.Add(new ItemImportacionDto(
                    HojaCentros, fila, nombre, $"No se encontró la empresa \"{empresa}\" (ni en el sistema ni en la hoja Empresas de este archivo)."));
                continue;
            }

            var clave = cliente + ClaveSeparador + nombre;
            if (!clavesVistas.Add(clave))
            {
                omitidos.Add(new ItemImportacionDto(HojaCentros, fila, nombre, "Este cliente ya tiene un centro con este nombre dentro del propio archivo."));
                continue;
            }

            var codigoCentro = TextoCelda(hoja.Cell(fila, 4));
            var direccion = TextoCelda(hoja.Cell(fila, 5));
            var contacto = TextoCelda(hoja.Cell(fila, 6));
            var contratoVigenteHasta = FechaCelda(hoja.Cell(fila, 7));

            resultado.Add(new CentroImportadoDto(
                nombre, cliente, empresa, codigoCentro, direccion, contacto, contratoVigenteHasta, centrosExistentes.Contains(clave)));
        }

        return resultado;
    }

    private static List<TrabajadorImportadoDto> AnalizarTrabajadores(
        XLWorkbook libro,
        HashSet<string> nombresEmpresasDisponibles,
        HashSet<string> dnisExistentes,
        List<ItemImportacionDto> omitidos)
    {
        var resultado = new List<TrabajadorImportadoDto>();

        if (!libro.Worksheets.TryGetWorksheet(HojaTrabajadores, out var hoja))
        {
            omitidos.Add(new ItemImportacionDto(HojaTrabajadores, 0, "Hoja completa", "No se encontró la hoja \"Trabajadores\" en el archivo."));
            return resultado;
        }

        var dnisVistos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var fila = PrimeraFilaDatos; ; fila++)
        {
            var nombre = TextoCelda(hoja.Cell(fila, 1));
            var apellidos = TextoCelda(hoja.Cell(fila, 2));
            if (string.IsNullOrWhiteSpace(nombre) && string.IsNullOrWhiteSpace(apellidos)) break;
            if ((nombre ?? apellidos)!.StartsWith(MarcadorEjemplo, StringComparison.OrdinalIgnoreCase)) continue;

            var dni = TextoCelda(hoja.Cell(fila, 3))?.Trim().ToUpperInvariant();
            var empresa = TextoCelda(hoja.Cell(fila, 4));

            if (string.IsNullOrWhiteSpace(nombre) || string.IsNullOrWhiteSpace(apellidos) || string.IsNullOrWhiteSpace(dni) || string.IsNullOrWhiteSpace(empresa))
            {
                omitidos.Add(new ItemImportacionDto(
                    HojaTrabajadores, fila, $"{nombre} {apellidos}".Trim(), "Faltan datos obligatorios (nombre, apellidos, DNI o empresa)."));
                continue;
            }

            if (!DocumentoValido(dni))
            {
                omitidos.Add(new ItemImportacionDto(HojaTrabajadores, fila, $"{nombre} {apellidos} ({dni})", "El documento de identidad no es un DNI, NIE o CIF válido."));
                continue;
            }

            if (!dnisVistos.Add(dni))
            {
                omitidos.Add(new ItemImportacionDto(HojaTrabajadores, fila, $"{nombre} {apellidos} ({dni})", "DNI duplicado dentro del propio archivo."));
                continue;
            }

            if (!nombresEmpresasDisponibles.Contains(empresa))
            {
                omitidos.Add(new ItemImportacionDto(
                    HojaTrabajadores, fila, $"{nombre} {apellidos} ({dni})",
                    $"No se encontró la empresa \"{empresa}\" (ni en el sistema ni en la hoja Empresas de este archivo)."));
                continue;
            }

            var fechaNacimiento = FechaCelda(hoja.Cell(fila, 5));
            var email = TextoCelda(hoja.Cell(fila, 6));

            resultado.Add(new TrabajadorImportadoDto(empresa, nombre, apellidos, dni, fechaNacimiento, email, dnisExistentes.Contains(dni)));
        }

        return resultado;
    }

    private static bool DocumentoValido(string dni)
    {
        var resultado = ValidadorIdentificacion.Analizar(dni);
        return resultado.EsValido || resultado.Tipo is not (TipoIdentificacion.Dni or TipoIdentificacion.Nie or TipoIdentificacion.NifEmpresa);
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
