using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CaeManager.Application.Common;
using CaeManager.Domain.Common;

namespace CaeManager.Application.Trabajadores.Extraccion;

/// <summary>
/// Parser local de ITA, RNT y TC2: de cada fila de la tabla saca un DNI/NIE con letra de
/// control correcta (<see cref="ValidadorIdentificacion"/>) y el nombre de <b>esa misma
/// fila</b>, sin IA. Está escrito para no fiarse de sí mismo: ante cualquier duda devuelve
/// <see cref="ResultadoExtraccionLocal.NoCuadra"/> y quien llama cae a la IA. Perder una
/// fila en silencio es el peor desenlace, porque <c>DeteccionTrabajadoresService</c> calcula
/// las bajas por ausencia: un Trabajador que el parser no ve pasaría por una baja inventada.
///
/// <b>Cuándo «cuadra»</b> (todas a la vez; la primera que falle fija el
/// <see cref="MotivoNoCuadra"/>):
/// <list type="number">
/// <item>Todas las páginas tienen texto digital (ninguna sin palabras ni cubierta por una imagen).</item>
/// <item>Todo lo que tiene forma de DNI/NIE (8 dígitos y letra, o X/Y/Z, 7 dígitos y letra) tiene
/// la letra de control correcta: uno solo erróneo invalida el documento, no se «corrige».</item>
/// <item>Todo DNI/NIE está en una fila posterior a una cabecera de tabla (línea con una etiqueta de
/// nombre y otra de documento de identidad), tiene un único DNI/NIE y su nombre sale de la
/// columna de nombre de esa cabecera, de la misma línea.</item>
/// <item>El nombre se separa sin adivinar: con coma («GARCIA LOPEZ, JUAN»), o en dos columnas
/// «Apellidos» y «Nombre». Un nombre sin coma en una sola columna no se parte.</item>
/// <item>Ninguna fila posterior a la cabecera tiene nombre en la columna de nombre y número de
/// afiliación (9 o más dígitos) pero no DNI/NIE: sería un Trabajador con pasaporte o un DNI mal
/// impreso que el parser no vería.</item>
/// <item>El mismo DNI/NIE no sale con dos nombres distintos, y hay al menos un Trabajador.</item>
/// </list>
/// Límites conocidos: un nombre partido en dos líneas se lee con la primera; una tabla de bajas
/// bajo otra cabecera igual se leería como altas. Los dos los acota la revisión del Gestor CAE,
/// que ya existe sobre el resultado de la IA.
/// </summary>
public sealed partial class ExtractorTrabajadoresLocalService(IExtractorPalabrasPdf palabras) : IExtractorTrabajadoresLocal
{
    internal const int MaximoPaginas = 300;
    internal const int MaximoPalabrasEnTotal = 300_000;

    /// <summary>Páginas cuya imagen mayor cubre más de esta fracción se consideran escaneadas.</summary>
    internal const double ProporcionImagenDePaginaEscaneada = 0.5;

    private const int MinimoDigitosDeNumeroDeAfiliacion = 9;

    /// <summary>Separación horizontal, en alturas de letra, a partir de la cual dos palabras son columnas distintas.</summary>
    private const double SeparacionDeColumna = 0.8;

    /// <summary>Holgura, en alturas de letra, al comparar el rango de una cabecera con el de un tramo de la fila.</summary>
    private const double HolguraDeColumna = 0.5;

    private const double ToleranciaDeLinea = 0.4;

    public ResultadoExtraccionLocal Extraer(byte[] contenidoPdf)
    {
        var lectura = palabras.ExtraerPalabras(contenidoPdf);
        if (lectura.EsFallido)
            return ResultadoExtraccionLocal.NoCuadra(MotivoNoCuadra.PdfNoLegible);

        var paginas = lectura.Valor;
        if (paginas.Count > MaximoPaginas || paginas.Sum(p => (long)p.Palabras.Count) > MaximoPalabrasEnTotal)
            return ResultadoExtraccionLocal.NoCuadra(MotivoNoCuadra.PdfNoLegible);

        if (paginas.Count == 0 || paginas.All(p => p.Palabras.Count == 0))
            return ResultadoExtraccionLocal.NoCuadra(MotivoNoCuadra.SinTexto);

        if (paginas.Any(p => p.Palabras.Count == 0 || p.ProporcionImagen > ProporcionImagenDePaginaEscaneada))
            return ResultadoExtraccionLocal.NoCuadra(MotivoNoCuadra.PaginaSinTextoDigital);

        var porIdentificador = new Dictionary<string, TrabajadorExtraidoDto>(StringComparer.Ordinal);
        Cabecera? cabecera = null;

        foreach (var pagina in paginas.OrderBy(p => p.Numero))
        {
            foreach (var linea in AgruparEnLineas(pagina.Palabras))
            {
                var candidatos = BuscarIdentificadores(linea);

                if (candidatos.Count == 0)
                {
                    if (EsCabecera(linea))
                    {
                        cabecera = ConstruirCabecera(linea);
                        continue;
                    }

                    if (cabecera is not null && TieneNumeroDeAfiliacion(linea))
                    {
                        var posibleNombre = LeerNombre(linea, cabecera, []);
                        // Solo cuenta como fila perdida si hay algo en la columna de nombre: una línea de
                        // pie o de membrete con un número largo no tiene nombre ahí.
                        if (posibleNombre.Resultado != ResultadoLectura.SinTramos)
                            return ResultadoExtraccionLocal.NoCuadra(MotivoNoCuadra.FilaSinIdentificador);
                    }

                    continue;
                }

                if (candidatos.Any(c => !EsDniONieValido(c.Texto)))
                    return ResultadoExtraccionLocal.NoCuadra(MotivoNoCuadra.IdentificadorInvalido);

                if (candidatos.Select(c => c.Texto).Distinct(StringComparer.Ordinal).Count() > 1)
                    return ResultadoExtraccionLocal.NoCuadra(MotivoNoCuadra.VariosIdentificadoresEnFila);

                if (cabecera is null)
                    return ResultadoExtraccionLocal.NoCuadra(MotivoNoCuadra.IdentificadorSinFila);

                var indicesDelIdentificador = candidatos.SelectMany(c => Enumerable.Range(c.Indice, c.Cuenta)).ToHashSet();
                var nombre = LeerNombre(linea, cabecera, indicesDelIdentificador);

                switch (nombre.Resultado)
                {
                    case ResultadoLectura.SinTramos:
                        return ResultadoExtraccionLocal.NoCuadra(MotivoNoCuadra.IdentificadorSinFila);
                    case ResultadoLectura.Ambiguo:
                        return ResultadoExtraccionLocal.NoCuadra(MotivoNoCuadra.NombreAmbiguo);
                }

                var identificador = candidatos[0].Texto;
                var trabajador = new TrabajadorExtraidoDto(nombre.Nombre!, nombre.Apellidos!, identificador);

                if (porIdentificador.TryGetValue(identificador, out var existente))
                {
                    if (!MismoNombre(existente, trabajador))
                        return ResultadoExtraccionLocal.NoCuadra(MotivoNoCuadra.IdentificadorConNombresDistintos);
                }
                else
                {
                    porIdentificador.Add(identificador, trabajador);
                }
            }
        }

        if (porIdentificador.Count == 0)
            return ResultadoExtraccionLocal.NoCuadra(MotivoNoCuadra.SinTrabajadores);

        return new ResultadoExtraccionLocal([.. porIdentificador.Values], Motivo: null);
    }

    // ---- Líneas -----------------------------------------------------------------------

    private sealed record Linea(IReadOnlyList<PalabraPdf> Palabras, double Alto);

    private static List<Linea> AgruparEnLineas(IReadOnlyList<PalabraPdf> palabrasDePagina)
    {
        var grupos = new List<(double Referencia, List<PalabraPdf> Palabras)>();

        foreach (var palabra in palabrasDePagina.Where(p => !string.IsNullOrWhiteSpace(p.Texto)).OrderByDescending(p => p.CentroY))
        {
            if (grupos.Count > 0)
            {
                var (referencia, enCurso) = grupos[^1];
                if (Math.Abs(palabra.CentroY - referencia) <= ToleranciaDeLinea * Math.Max(palabra.Alto, enCurso[0].Alto))
                {
                    enCurso.Add(palabra);
                    continue;
                }
            }

            grupos.Add((palabra.CentroY, [palabra]));
        }

        return grupos
            .Select(g =>
            {
                var ordenadas = g.Palabras.OrderBy(p => p.Izquierda).ToList();
                return new Linea(ordenadas, ordenadas.Average(p => p.Alto));
            })
            .ToList();
    }

    // ---- Identificadores --------------------------------------------------------------

    private readonly record struct Candidato(int Indice, int Cuenta, string Texto);

    /// <summary>
    /// Palabras (o dos palabras contiguas, «12345678 Z») con la forma de un DNI o un NIE, hayan
    /// o no acertado la letra: lo que tiene la forma y no cuadra es una alarma, no se descarta.
    /// </summary>
    private static List<Candidato> BuscarIdentificadores(Linea linea)
    {
        var encontrados = new List<Candidato>();
        var palabrasDeLinea = linea.Palabras;

        for (var i = 0; i < palabrasDeLinea.Count; i++)
        {
            var solo = Compactar(palabrasDeLinea[i].Texto);
            if (FormaDeIdentificador().IsMatch(solo))
            {
                encontrados.Add(new Candidato(i, 1, solo));
                continue;
            }

            if (i + 1 < palabrasDeLinea.Count
                && palabrasDeLinea[i + 1].Izquierda - palabrasDeLinea[i].Derecha <= linea.Alto
                && FormaDeIdentificador().IsMatch(Compactar(palabrasDeLinea[i].Texto + palabrasDeLinea[i + 1].Texto)))
            {
                encontrados.Add(new Candidato(i, 2, Compactar(palabrasDeLinea[i].Texto + palabrasDeLinea[i + 1].Texto)));
                i++;
            }
        }

        return encontrados;
    }

    private static string Compactar(string texto) =>
        new string(texto.Trim().Trim(',', ';', ':', '(', ')', '[', ']').Where(c => c is not '-' and not '.' and not '_').ToArray())
            .ToUpperInvariant();

    private static bool EsDniONieValido(string identificador)
    {
        var analisis = ValidadorIdentificacion.Analizar(identificador);
        return analisis.EsValido && analisis.Tipo is TipoIdentificacion.Dni or TipoIdentificacion.Nie;
    }

    private static bool TieneNumeroDeAfiliacion(Linea linea) =>
        linea.Palabras.Any(p =>
        {
            var digitos = p.Texto.Where(c => c is not '/' and not '-' and not ' ').ToArray();
            return digitos.Length >= MinimoDigitosDeNumeroDeAfiliacion && digitos.All(char.IsAsciiDigit);
        });

    // ---- Cabecera ---------------------------------------------------------------------

    private enum EsquemaDeNombre
    {
        Combinado,
        Separado,
        Invalido,
    }

    private sealed record ColumnaDeNombre(bool Apellidos, bool Nombre, bool NombrePrimero, double Izquierda, double Derecha);

    private sealed record Cabecera(EsquemaDeNombre Esquema, IReadOnlyList<ColumnaDeNombre> Columnas);

    private static bool EsEtiquetaDeNombre(string palabra) =>
        palabra is "APELLIDOS" or "APELLIDO" or "NOMBRE" or "NOMBRES" or "TRABAJADOR";

    private static bool EsEtiquetaDeDocumento(string palabra) =>
        palabra.Split('/').Any(parte => parte is "DNI" or "NIF" or "NIE" or "DOCUMENTO" or "IDENTIFICACION" or "IDENTIFICADOR");

    private static string Etiqueta(string texto)
    {
        var descompuesto = texto.Normalize(NormalizationForm.FormD);
        var sinAcentos = new StringBuilder(descompuesto.Length);
        foreach (var c in descompuesto)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && c is not '.' and not ':' and not ',')
                sinAcentos.Append(c);
        }

        return sinAcentos.ToString().ToUpperInvariant();
    }

    private static bool EsCabecera(Linea linea)
    {
        var etiquetas = linea.Palabras.Select(p => Etiqueta(p.Texto)).ToList();
        return etiquetas.Any(EsEtiquetaDeNombre) && etiquetas.Any(EsEtiquetaDeDocumento);
    }

    private static Cabecera ConstruirCabecera(Linea linea)
    {
        var columnas = new List<ColumnaDeNombre>();
        List<PalabraPdf>? grupo = null;

        void Cerrar()
        {
            if (grupo is null)
                return;

            var deNombre = grupo.Where(p => EsEtiquetaDeNombre(Etiqueta(p.Texto))).ToList();
            if (deNombre.Count > 0)
            {
                var etiquetas = deNombre.Select(p => Etiqueta(p.Texto)).ToList();
                var apellidos = etiquetas.Any(e => e is "APELLIDOS" or "APELLIDO");
                var nombre = etiquetas.Any(e => e is "NOMBRE" or "NOMBRES");
                var trabajador = etiquetas.Contains("TRABAJADOR");

                // «Trabajador» solo, o con «nombre»/«apellidos», es una columna que lleva el nombre completo.
                // Si la etiqueta dice «Nombre y apellidos», el orden de los datos es el de la etiqueta.
                var primeraDeNombre = deNombre.Where(p => Etiqueta(p.Texto) is "NOMBRE" or "NOMBRES").Select(p => p.Izquierda).DefaultIfEmpty(double.MaxValue).Min();
                var primeraDeApellidos = deNombre.Where(p => Etiqueta(p.Texto) is "APELLIDOS" or "APELLIDO").Select(p => p.Izquierda).DefaultIfEmpty(double.MaxValue).Min();

                columnas.Add(new ColumnaDeNombre(
                    Apellidos: apellidos || trabajador,
                    Nombre: nombre || trabajador,
                    NombrePrimero: nombre && apellidos && primeraDeNombre < primeraDeApellidos,
                    deNombre.Min(p => p.Izquierda),
                    deNombre.Max(p => p.Derecha)));
            }

            grupo = null;
        }

        foreach (var palabra in linea.Palabras)
        {
            if (grupo is not null && palabra.Izquierda - grupo[^1].Derecha > SeparacionDeColumna * linea.Alto)
                Cerrar();

            (grupo ??= []).Add(palabra);
        }

        Cerrar();

        var esquema = columnas switch
        {
            [{ Apellidos: true, Nombre: true }] => EsquemaDeNombre.Combinado,
            [{ Apellidos: true, Nombre: false }, { Apellidos: false, Nombre: true }] => EsquemaDeNombre.Separado,
            [{ Apellidos: false, Nombre: true }, { Apellidos: true, Nombre: false }] => EsquemaDeNombre.Separado,
            _ => EsquemaDeNombre.Invalido,
        };

        return new Cabecera(esquema, columnas);
    }

    // ---- Nombre -----------------------------------------------------------------------

    private enum ResultadoLectura
    {
        Correcto,
        SinTramos,
        Ambiguo,
    }

    private readonly record struct NombreLeido(ResultadoLectura Resultado, string? Nombre, string? Apellidos);

    private sealed record Tramo(string Texto, double Izquierda, double Derecha);

    private static NombreLeido LeerNombre(Linea linea, Cabecera cabecera, HashSet<int> excluidos)
    {
        var tramos = ConstruirTramos(linea, excluidos);
        var holgura = HolguraDeColumna * linea.Alto;

        var porColumna = cabecera.Columnas
            .Select(columna => tramos.Where(t => t.Derecha >= columna.Izquierda - holgura && t.Izquierda <= columna.Derecha + holgura).ToList())
            .ToList();

        if (porColumna.All(t => t.Count == 0))
            return new NombreLeido(ResultadoLectura.SinTramos, null, null);

        if (cabecera.Esquema == EsquemaDeNombre.Invalido || porColumna.Any(t => t.Count != 1))
            return new NombreLeido(ResultadoLectura.Ambiguo, null, null);

        if (cabecera.Esquema == EsquemaDeNombre.Combinado)
        {
            var texto = porColumna[0][0].Texto;
            var partes = texto.Split(',');
            if (partes.Length != 2)
                return new NombreLeido(ResultadoLectura.Ambiguo, null, null);

            var primero = Limpiar(partes[0]);
            var segundo = Limpiar(partes[1]);
            var (apellidos, nombre) = cabecera.Columnas[0].NombrePrimero ? (segundo, primero) : (primero, segundo);
            return apellidos.Length == 0 || nombre.Length == 0
                ? new NombreLeido(ResultadoLectura.Ambiguo, null, null)
                : new NombreLeido(ResultadoLectura.Correcto, nombre, apellidos);
        }

        var apellidosTexto = Limpiar(porColumna[cabecera.Columnas[0].Apellidos ? 0 : 1][0].Texto);
        var nombreTexto = Limpiar(porColumna[cabecera.Columnas[0].Apellidos ? 1 : 0][0].Texto);

        return apellidosTexto.Length == 0 || nombreTexto.Length == 0 || apellidosTexto.Contains(',') || nombreTexto.Contains(',')
            ? new NombreLeido(ResultadoLectura.Ambiguo, null, null)
            : new NombreLeido(ResultadoLectura.Correcto, nombreTexto, apellidosTexto);
    }

    /// <summary>Palabras de nombre contiguas (sin hueco de columna entre ellas) de la línea, dejando fuera el identificador.</summary>
    private static List<Tramo> ConstruirTramos(Linea linea, HashSet<int> excluidos)
    {
        var tramos = new List<Tramo>();
        List<PalabraPdf>? actual = null;

        void Cerrar()
        {
            if (actual is not null)
            {
                tramos.Add(new Tramo(string.Join(' ', actual.Select(p => p.Texto.Trim())), actual[0].Izquierda, actual[^1].Derecha));
                actual = null;
            }
        }

        for (var i = 0; i < linea.Palabras.Count; i++)
        {
            var palabra = linea.Palabras[i];
            if (excluidos.Contains(i) || !PalabraDeNombre().IsMatch(palabra.Texto.Trim()))
            {
                Cerrar();
                continue;
            }

            if (actual is not null && palabra.Izquierda - actual[^1].Derecha > SeparacionDeColumna * linea.Alto)
                Cerrar();

            (actual ??= []).Add(palabra);
        }

        Cerrar();
        return tramos;
    }

    private static string Limpiar(string texto) =>
        EspaciosRepetidos().Replace(texto.Trim().Trim(',').Trim(), " ");

    private static bool MismoNombre(TrabajadorExtraidoDto a, TrabajadorExtraidoDto b) =>
        string.Equals(a.Nombre, b.Nombre, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Apellidos, b.Apellidos, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^(\d{8}[A-Z]|[XYZ]\d{7}[A-Z])$")]
    private static partial Regex FormaDeIdentificador();

    [GeneratedRegex(@"^(,|,?[\p{L}][\p{L}'’.\-ªº]*,?)$")]
    private static partial Regex PalabraDeNombre();

    [GeneratedRegex(@"\s+")]
    private static partial Regex EspaciosRepetidos();
}
