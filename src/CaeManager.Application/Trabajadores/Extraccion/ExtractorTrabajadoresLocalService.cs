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
/// <item>Todas las páginas tienen texto digital (ninguna sin palabras, cubierta por una imagen o con
/// su texto girado), y el PDF respeta los topes de <see cref="LimitesLecturaPalabrasPdf"/>.</item>
/// <item>Todo lo que tiene forma de DNI/NIE (8 dígitos y letra, o X/Y/Z, 7 dígitos y letra) tiene
/// la letra de control correcta: uno solo erróneo invalida el documento, no se «corrige».</item>
/// <item>Cada página tiene una sola cabecera de tabla (línea con una etiqueta de nombre y otra de
/// documento de identidad) y ningún texto que mencione bajas. La cabecera vale solo para su página:
/// un membrete o una firma de otra página no se leen con la cabecera de la anterior, y una
/// continuación sin cabecera repetida cae a la IA.</item>
/// <item>Todo DNI/NIE está en una fila posterior a esa cabecera, es el único de su fila y su nombre
/// sale de la zona de la columna de nombre (entre la columna vecina anterior y la siguiente de la
/// cabecera), de la misma línea.</item>
/// <item>El nombre se separa sin adivinar: con coma («GARCIA LOPEZ, JUAN») bajo «Apellidos y nombre»,
/// o en dos columnas «Apellidos» y «Nombre». Un nombre sin coma, bajo «Nombre y apellidos» (el
/// orden real de los datos no es seguro) o que continúa en la línea de debajo no se parte.</item>
/// <item>Ninguna línea sin DNI/NIE, bajo la cabecera, tiene aspecto de fila de Trabajador: nombre en
/// la zona de nombre con coma (o con apellidos y nombre en sus dos columnas) o con un número de 9 o
/// más dígitos, aunque venga partido en grupos; y, en cualquier columna, nombre con coma o con
/// número largo por encima de la última fila de la página (dentro de la tabla, no en un pie). Sería
/// un Trabajador con pasaporte o un DNI mal impreso que el parser no vería.</item>
/// <item>El mismo DNI/NIE no sale con dos nombres distintos, y hay al menos un Trabajador.</item>
/// </list>
/// Límite conocido y no mitigado: una tabla de bajas con la misma cabecera que la de altas y sin
/// ninguna mención a «baja» en el texto se leería como altas. Un baja leído como presente no genera
/// ninguna detección, así que ahí tampoco habría nada que revisar para el Gestor CAE.
/// </summary>
public sealed partial class ExtractorTrabajadoresLocalService(IExtractorPalabrasPdf palabras) : IExtractorTrabajadoresLocal
{
    /// <summary>Páginas cuya imagen mayor cubre más de esta fracción se consideran escaneadas.</summary>
    internal const double ProporcionImagenDePaginaEscaneada = 0.5;

    private const int MinimoDigitosDeNumeroLargo = 9;

    /// <summary>Separación horizontal, en alturas de letra, a partir de la cual dos palabras son columnas distintas.</summary>
    private const double SeparacionDeColumna = 0.8;

    private const double ToleranciaDeLinea = 0.4;

    /// <summary>Una línea sin DNI/NIE a menos de esta distancia (en alturas) bajo una fila continúa su nombre.</summary>
    private const double DistanciaDeContinuacion = 1.6;

    public ResultadoExtraccionLocal Extraer(byte[] contenidoPdf)
    {
        var lectura = palabras.ExtraerPalabras(contenidoPdf);
        if (lectura.EsFallido)
            return ResultadoExtraccionLocal.NoCuadra(MotivoNoCuadra.PdfNoLegible);

        var paginas = lectura.Valor;
        if (paginas.Count > LimitesLecturaPalabrasPdf.MaximoPaginas
            || paginas.Sum(p => (long)p.Palabras.Count) > LimitesLecturaPalabrasPdf.MaximoPalabrasEnTotal)
        {
            return ResultadoExtraccionLocal.NoCuadra(MotivoNoCuadra.PdfNoLegible);
        }

        if (paginas.Count == 0 || paginas.All(p => p.Palabras.Count == 0 && p.PalabrasNoHorizontales == 0))
            return ResultadoExtraccionLocal.NoCuadra(MotivoNoCuadra.SinTexto);

        if (paginas.Any(p => p.Palabras.Count == 0 || p.ProporcionImagen > ProporcionImagenDePaginaEscaneada))
            return ResultadoExtraccionLocal.NoCuadra(MotivoNoCuadra.PaginaSinTextoDigital);

        if (paginas.Any(p => p.PalabrasNoHorizontales > LimitesLecturaPalabrasPdf.MaximoPalabrasNoHorizontalesPorPagina
            || p.PalabrasNoHorizontales > p.Palabras.Count))
        {
            return ResultadoExtraccionLocal.NoCuadra(MotivoNoCuadra.TextoNoHorizontal);
        }

        var porIdentificador = new Dictionary<string, TrabajadorExtraidoDto>(StringComparer.Ordinal);

        foreach (var pagina in paginas.OrderBy(p => p.Numero))
        {
            // La cabecera y la última fila valen solo dentro de su página.
            Cabecera? cabecera = null;
            double? centroDeLaUltimaFila = null;
            var candidatasInteriores = new List<double>();

            foreach (var linea in AgruparEnLineas(pagina.Palabras))
            {
                var candidatos = BuscarIdentificadores(linea);

                if (candidatos.Count == 0)
                {
                    if (EsCabecera(linea))
                    {
                        if (cabecera is not null)
                            return ResultadoExtraccionLocal.NoCuadra(MotivoNoCuadra.TablaAmbigua);

                        cabecera = ConstruirCabecera(linea);
                        continue;
                    }

                    if (MencionaBajas(linea))
                        return ResultadoExtraccionLocal.NoCuadra(MotivoNoCuadra.TablaAmbigua);

                    if (cabecera is not null)
                    {
                        if (SospechaDeFilaPerdida(linea, cabecera, centroDeLaUltimaFila) is { } motivoDeSospecha)
                            return ResultadoExtraccionLocal.NoCuadra(motivoDeSospecha);

                        if (EsCandidataInterior(linea))
                            candidatasInteriores.Add(linea.CentroY);
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
                centroDeLaUltimaFila = linea.CentroY;

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

            // Una línea sin DNI/NIE con aspecto de fila, por encima de la última fila leída de la página (o en una
            // página sin ninguna), está dentro de la tabla y no es un pie: es un Trabajador que no se ha leído,
            // aunque su nombre quede fuera de la zona de la columna.
            if (candidatasInteriores.Any(y => centroDeLaUltimaFila is not { } ultima || y > ultima))
                return ResultadoExtraccionLocal.NoCuadra(MotivoNoCuadra.FilaSinIdentificador);
        }

        if (porIdentificador.Count == 0)
            return ResultadoExtraccionLocal.NoCuadra(MotivoNoCuadra.SinTrabajadores);

        return new ResultadoExtraccionLocal([.. porIdentificador.Values], Motivo: null);
    }

    // ---- Líneas -----------------------------------------------------------------------

    private sealed record Linea(IReadOnlyList<PalabraPdf> Palabras, double Alto, double CentroY);

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
                return new Linea(ordenadas, ordenadas.Average(p => p.Alto), ordenadas.Average(p => p.CentroY));
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

    /// <summary>
    /// ¿Hay en la línea un número de 9 o más dígitos (NAF, cuenta de cotización), aunque venga partido en
    /// grupos («28 12345678 90») o con barras? Cuenta como un solo número las palabras numéricas contiguas.
    /// </summary>
    private static bool TieneNumeroLargo(Linea linea)
    {
        var digitos = 0;
        PalabraPdf? anterior = null;

        foreach (var palabra in linea.Palabras)
        {
            var texto = palabra.Texto.Trim();
            var esNumerica = texto.Length > 0 && texto.Any(char.IsAsciiDigit) && texto.All(c => char.IsAsciiDigit(c) || c is '/' or '-');

            if (!esNumerica)
            {
                digitos = 0;
                anterior = null;
                continue;
            }

            var contiguo = anterior is { } previa && palabra.Izquierda - previa.Derecha <= linea.Alto;
            digitos = (contiguo ? digitos : 0) + texto.Count(char.IsAsciiDigit);
            anterior = palabra;

            if (digitos >= MinimoDigitosDeNumeroLargo)
                return true;
        }

        return false;
    }

    private static bool MencionaBajas(Linea linea) =>
        linea.Palabras.Any(p => Etiqueta(p.Texto) is "BAJA" or "BAJAS");

    // ---- Cabecera ---------------------------------------------------------------------

    private enum EsquemaDeNombre
    {
        Combinado,
        Separado,
        Invalido,
    }

    /// <param name="ZonaIzquierda">Punto medio entre la columna vecina anterior de la cabecera y esta (−∞ si no hay).</param>
    /// <param name="ZonaDerecha">Punto medio entre esta columna y la vecina siguiente de la cabecera (+∞ si no hay).</param>
    private sealed record ColumnaDeNombre(bool Apellidos, bool Nombre, bool NombrePrimero, double ZonaIzquierda, double ZonaDerecha);

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

    /// <summary>
    /// Agrupa las etiquetas de la cabecera por columnas (huecos de columna entre grupos) y, para cada
    /// una con etiqueta de nombre, fija su zona: de la mitad del hueco con la columna vecina anterior a la
    /// mitad del hueco con la siguiente. Las zonas no se solapan, y son lo que se compara con el texto de
    /// cada fila (por su centro), porque una etiqueta centrada sobre una columna ancha no queda sobre un
    /// nombre corto y un tramo no puede contar en dos columnas.
    /// </summary>
    private static Cabecera ConstruirCabecera(Linea linea)
    {
        var grupos = new List<List<PalabraPdf>>();
        foreach (var palabra in linea.Palabras)
        {
            if (grupos.Count > 0 && palabra.Izquierda - grupos[^1][^1].Derecha <= SeparacionDeColumna * linea.Alto)
                grupos[^1].Add(palabra);
            else
                grupos.Add([palabra]);
        }

        var columnas = new List<ColumnaDeNombre>();

        for (var i = 0; i < grupos.Count; i++)
        {
            var deNombre = grupos[i].Where(p => EsEtiquetaDeNombre(Etiqueta(p.Texto))).ToList();
            if (deNombre.Count == 0)
                continue;

            var etiquetas = deNombre.Select(p => Etiqueta(p.Texto)).ToList();
            var apellidos = etiquetas.Any(e => e is "APELLIDOS" or "APELLIDO");
            var nombre = etiquetas.Any(e => e is "NOMBRE" or "NOMBRES");
            var trabajador = etiquetas.Contains("TRABAJADOR");

            // Si la etiqueta dice «Nombre y apellidos», el orden real de los datos no es seguro
            // (se imprime a menudo «APELLIDOS, NOMBRE» bajo esa etiqueta): ese caso no se resuelve.
            var primeraDeNombre = deNombre.Where(p => Etiqueta(p.Texto) is "NOMBRE" or "NOMBRES").Select(p => p.Izquierda).DefaultIfEmpty(double.MaxValue).Min();
            var primeraDeApellidos = deNombre.Where(p => Etiqueta(p.Texto) is "APELLIDOS" or "APELLIDO").Select(p => p.Izquierda).DefaultIfEmpty(double.MaxValue).Min();

            columnas.Add(new ColumnaDeNombre(
                Apellidos: apellidos || trabajador,
                Nombre: nombre || trabajador,
                NombrePrimero: nombre && apellidos && primeraDeNombre < primeraDeApellidos,
                ZonaIzquierda: i > 0 ? (grupos[i - 1].Max(p => p.Derecha) + grupos[i].Min(p => p.Izquierda)) / 2 : double.NegativeInfinity,
                ZonaDerecha: i < grupos.Count - 1 ? (grupos[i].Max(p => p.Derecha) + grupos[i + 1].Min(p => p.Izquierda)) / 2 : double.PositiveInfinity));
        }

        var esquema = columnas switch
        {
            [{ Apellidos: true, Nombre: true, NombrePrimero: false }] => EsquemaDeNombre.Combinado,
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

    private sealed record Tramo(string Texto, double Izquierda, double Derecha)
    {
        public double Centro => (Izquierda + Derecha) / 2;
    }

    /// <summary>Para cada columna de nombre de la cabecera, los tramos de la línea cuyo centro cae en su zona.</summary>
    private static List<List<Tramo>> TramosPorColumna(Linea linea, Cabecera cabecera, HashSet<int> excluidos)
    {
        var tramos = ConstruirTramos(linea, excluidos);

        return cabecera.Columnas
            .Select(columna => tramos.Where(t => t.Centro >= columna.ZonaIzquierda && t.Centro <= columna.ZonaDerecha).ToList())
            .ToList();
    }

    private static NombreLeido LeerNombre(Linea linea, Cabecera cabecera, HashSet<int> excluidos)
    {
        var porColumna = TramosPorColumna(linea, cabecera, excluidos);

        if (porColumna.All(t => t.Count == 0))
            return new NombreLeido(ResultadoLectura.SinTramos, null, null);

        if (cabecera.Esquema == EsquemaDeNombre.Invalido || porColumna.Any(t => t.Count != 1))
            return new NombreLeido(ResultadoLectura.Ambiguo, null, null);

        if (cabecera.Esquema == EsquemaDeNombre.Combinado)
        {
            var partes = porColumna[0][0].Texto.Split(',');
            if (partes.Length != 2)
                return new NombreLeido(ResultadoLectura.Ambiguo, null, null);

            var apellidos = Limpiar(partes[0]);
            var nombre = Limpiar(partes[1]);
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

    /// <summary>
    /// Una línea bajo la cabecera sin DNI/NIE que, aun así, podría ser un Trabajador o parte de uno.
    /// Devuelve el motivo si lo parece, o <c>null</c> si es otra cosa (pie, membrete, línea en blanco).
    /// </summary>
    private static MotivoNoCuadra? SospechaDeFilaPerdida(Linea linea, Cabecera cabecera, double? centroDeLaUltimaFila)
    {
        var porColumna = TramosPorColumna(linea, cabecera, []);
        if (porColumna.All(t => t.Count == 0))
            return null;

        var pareceFila = TieneNumeroLargo(linea) || cabecera.Esquema switch
        {
            EsquemaDeNombre.Combinado => porColumna[0].Any(t => t.Texto.Contains(',')),
            EsquemaDeNombre.Separado => porColumna.All(t => t.Count > 0),
            _ => false,
        };

        if (pareceFila)
            return MotivoNoCuadra.FilaSinIdentificador;

        // Justo debajo de una fila, con texto en la columna de nombre y sin aspecto de fila propia: es la
        // continuación de un nombre partido en dos líneas, y la fila ya leída lleva solo la primera mitad.
        if (centroDeLaUltimaFila is { } ultimo)
        {
            var distancia = ultimo - linea.CentroY;
            if (distancia > 0 && distancia <= DistanciaDeContinuacion * linea.Alto)
                return MotivoNoCuadra.NombreAmbiguo;
        }

        return null;
    }

    /// <summary>
    /// Línea sin DNI/NIE con nombre y número largo, o con un nombre con coma, en cualquier columna. Sola no
    /// prueba nada (un pie las tiene); lo prueba estar por encima de la última fila de la página.
    /// </summary>
    private static bool EsCandidataInterior(Linea linea)
    {
        var tramos = ConstruirTramos(linea, []);
        return tramos.Count > 0 && (TieneNumeroLargo(linea) || tramos.Any(t => t.Texto.Contains(',')));
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
