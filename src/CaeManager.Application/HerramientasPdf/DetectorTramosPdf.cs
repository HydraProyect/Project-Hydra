using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CaeManager.Application.Trabajadores.Extraccion;
using CaeManager.Domain.Documentos;

namespace CaeManager.Application.HerramientasPdf;

/// <summary>
/// Propone dónde empieza cada documento de un PDF fusionado y de quién es, solo con el texto digital y reglas.
///
/// Es una función pura a propósito, y ese es el invariante que la sostiene: en un PDF fusionado el tipo aún no se
/// conoce, así que la guarda de salud (<c>TipoDocumento.LecturaIaActiva</c>) no podría actuar. Ninguna página sale de
/// TALVEG en la detección; lo prohíbe <c>DetectorDePdfNoDependeDeIaTests</c>.
///
/// Empieza un tramo nuevo cuando: cambia entre página con y sin texto; la página dice «Página 1 de N»; se reconoce un
/// tipo distinto, o el mismo con otro titular; o cambia el encabezado y hay un título destacado. «Página k de N» con
/// k mayor que 1 siempre continúa el tramo. Una página sin texto no se adivina: queda «Sin identificar».
/// </summary>
public static partial class DetectorTramosPdf
{
    private const int MinimoPalabrasConTexto = 3;
    private const int LineasDeTitulo = 2;
    private const int LineasDeZonaDeTitulo = 12;
    private const double FactorTituloDestacado = 1.2;

    private static readonly HashSet<string> PalabrasVacias =
        ["de", "del", "la", "el", "los", "las", "y", "e", "en", "con", "para", "por", "a", "al", "o", "u"];

    public static IReadOnlyList<TramoDetectado> Detectar(
        IReadOnlyList<PaginaPdf> paginas, int numeroPaginas, CatalogoDivisionPdf catalogo)
    {
        if (numeroPaginas <= 0)
            return [];

        var porNumero = paginas.ToDictionary(p => p.Numero);
        var tipos = catalogo.Tipos.Select(TipoReconocible.Crear).Where(t => t.Variantes.Count > 0).ToList();

        var tramos = new List<TramoEnCurso>();
        LecturaPagina? anterior = null;

        for (var numero = 1; numero <= numeroPaginas; numero++)
        {
            var lectura = LecturaPagina.Crear(numero, porNumero.GetValueOrDefault(numero), tipos, catalogo);

            if (anterior is null || EmpiezaDocumento(lectura, anterior, tramos[^1]))
                tramos.Add(new TramoEnCurso(numero));

            tramos[^1].Anadir(lectura);
            anterior = lectura;
        }

        return tramos.Select(t => t.Cerrar()).ToList();
    }

    private static bool EmpiezaDocumento(LecturaPagina pagina, LecturaPagina anterior, TramoEnCurso tramo)
    {
        if (pagina.ConTexto != anterior.ConTexto)
            return true;

        if (!pagina.ConTexto)
            return false;

        if (pagina.NumeroInternoDePagina is { } interna)
            return interna == 1;

        if (pagina.Tipo is { } tipo)
        {
            if (tramo.Tipo is { } tipoDelTramo && tipoDelTramo.Id != tipo.Id)
                return true;

            if (tramo.Tipo is not null && pagina.TitularUnico() is { } titular && tramo.TitularUnico() is { } delTramo
                && titular != delTramo)
                return true;
        }

        return pagina.TieneTituloDestacado && pagina.Encabezado != anterior.Encabezado;
    }

    private sealed record TipoReconocible(TipoDocumentoDivisionPdf Tipo, List<HashSet<string>> Variantes)
    {
        public int Peso => Variantes.Max(v => v.Count);

        public static TipoReconocible Crear(TipoDocumentoDivisionPdf tipo)
        {
            var variantes = new[] { tipo.Nombre }.Concat(tipo.Aliases)
                .Select(t => Palabras(t).Where(p => !PalabrasVacias.Contains(p)).ToHashSet())
                .Where(v => v.Count > 0)
                .ToList();

            return new TipoReconocible(tipo, variantes);
        }

        public bool AparecenEn(HashSet<string> palabras) => Variantes.Any(v => v.IsSubsetOf(palabras));
    }

    /// <summary>Una coincidencia de titular en una página.</summary>
    private sealed record Coincidencia(ClaseTitular Clase, Guid Id, string Nombre, MotivoDeteccion Motivo, int Pagina);

    private sealed class LecturaPagina
    {
        public required int Numero { get; init; }
        public required bool ConTexto { get; init; }
        public string Encabezado { get; private init; } = string.Empty;
        public bool TieneTituloDestacado { get; private init; }
        public int? NumeroInternoDePagina { get; private init; }
        public TipoDocumentoDivisionPdf? Tipo { get; private init; }
        public IReadOnlyList<Coincidencia> Coincidencias { get; private init; } = [];

        public static LecturaPagina Crear(
            int numero, PaginaPdf? pagina, List<TipoReconocible> tipos, CatalogoDivisionPdf catalogo)
        {
            if (pagina is null || pagina.Palabras.Count < MinimoPalabrasConTexto)
                return new LecturaPagina { Numero = numero, ConTexto = false };

            var lineas = AgruparEnLineas(pagina.Palabras);
            var alturaMediana = Mediana(pagina.Palabras.Select(p => p.Alto));
            var textoLineas = lineas.Select(l => string.Join(' ', l.Select(p => p.Texto))).ToList();
            var textoNormalizado = Normalizar(string.Join(' ', textoLineas));
            var palabrasDeZona = textoLineas.Take(LineasDeZonaDeTitulo).SelectMany(Palabras).ToHashSet();

            return new LecturaPagina
            {
                Numero = numero,
                ConTexto = true,
                Encabezado = Digitos().Replace(Normalizar(textoLineas[0]), string.Empty),
                TieneTituloDestacado = lineas.Take(LineasDeTitulo).Any(l => l.Max(p => p.Alto) >= alturaMediana * FactorTituloDestacado),
                NumeroInternoDePagina = LeerNumeroInterno(textoNormalizado),
                Tipo = ReconocerTipo(tipos, palabrasDeZona),
                Coincidencias = BuscarTitulares(numero, textoNormalizado, catalogo)
            };
        }

        public Guid? TitularUnico()
        {
            var ids = Coincidencias
                .Where(c => c.Clase is ClaseTitular.Trabajador or ClaseTitular.Vehiculo or ClaseTitular.Subcontrata)
                .Select(c => c.Id).Distinct().ToList();

            return ids.Count == 1 ? ids[0] : null;
        }

        private static int? LeerNumeroInterno(string texto)
        {
            var coincidencia = PaginaDeTotal().Match(texto);
            return coincidencia.Success && int.TryParse(coincidencia.Groups[1].Value, CultureInfo.InvariantCulture, out var numero)
                ? numero
                : null;
        }

        private static TipoDocumentoDivisionPdf? ReconocerTipo(List<TipoReconocible> tipos, HashSet<string> palabras)
        {
            var candidatos = tipos.Where(t => t.AparecenEn(palabras)).ToList();
            if (candidatos.Count == 0)
                return null;

            var mayorPeso = candidatos.Max(t => t.Peso);
            var mejores = candidatos.Where(t => t.Peso == mayorPeso).ToList();

            // Dos tipos que empatan no se desempatan por azar: mejor sin tipo que con uno inventado.
            return mejores.Count == 1 ? mejores[0].Tipo : null;
        }

        private static List<Coincidencia> BuscarTitulares(int numero, string texto, CatalogoDivisionPdf catalogo)
        {
            var compacto = texto.Replace(" ", string.Empty, StringComparison.Ordinal);
            var conEspacios = $" {texto} ";
            var coincidencias = new List<Coincidencia>();

            void PorCif(EmpresaDivisionPdf empresa, ClaseTitular clase, MotivoDeteccion motivo)
            {
                if (Compactar(empresa.Cif) is { Length: >= 8 } cif && compacto.Contains(cif, StringComparison.Ordinal))
                    coincidencias.Add(new Coincidencia(clase, empresa.Id, empresa.RazonSocial, motivo, numero));
            }

            PorCif(catalogo.EmpresaGestionada, ClaseTitular.EmpresaGestionada, MotivoDeteccion.CifEmpresaGestionada);
            foreach (var subcontrata in catalogo.Subcontratas)
                PorCif(subcontrata, ClaseTitular.Subcontrata, MotivoDeteccion.CifSubcontrata);

            foreach (var trabajador in catalogo.Trabajadores)
            {
                if (Compactar(trabajador.Dni) is { Length: >= 8 } dni && compacto.Contains(dni, StringComparison.Ordinal))
                {
                    coincidencias.Add(new Coincidencia(ClaseTitular.Trabajador, trabajador.Id, trabajador.NombreCompleto, MotivoDeteccion.Dni, numero));
                    continue;
                }

                var nombre = Normalizar(trabajador.Nombre);
                var apellidos = Normalizar(trabajador.Apellidos);
                if (nombre.Length > 0 && apellidos.Length > 0
                    && (conEspacios.Contains($" {nombre} {apellidos} ", StringComparison.Ordinal)
                        || conEspacios.Contains($" {apellidos} {nombre} ", StringComparison.Ordinal)))
                    coincidencias.Add(new Coincidencia(ClaseTitular.Trabajador, trabajador.Id, trabajador.NombreCompleto, MotivoDeteccion.Nombre, numero));
            }

            foreach (var vehiculo in catalogo.Vehiculos)
            {
                if (Compactar(vehiculo.NumeroPlaca) is { Length: >= 6 } placa && compacto.Contains(placa, StringComparison.Ordinal))
                    coincidencias.Add(new Coincidencia(ClaseTitular.Vehiculo, vehiculo.Id, vehiculo.Nombre, MotivoDeteccion.Matricula, numero));
            }

            return coincidencias;
        }
    }

    private sealed class TramoEnCurso(int paginaInicial)
    {
        private readonly List<LecturaPagina> _paginas = [];

        public TipoDocumentoDivisionPdf? Tipo { get; private set; }

        public void Anadir(LecturaPagina pagina)
        {
            _paginas.Add(pagina);
            Tipo ??= pagina.Tipo;
        }

        public Guid? TitularUnico()
        {
            var ids = _paginas.Select(p => p.TitularUnico()).OfType<Guid>().Distinct().ToList();
            return ids.Count == 1 ? ids[0] : null;
        }

        public TramoDetectado Cerrar()
        {
            var paginaFinal = paginaInicial + _paginas.Count - 1;
            if (!_paginas.Any(p => p.ConTexto))
                return new TramoDetectado(paginaInicial, paginaFinal, null, null, null, null, null, MotivoDeteccion.SinTexto, null);

            var coincidencias = _paginas.SelectMany(p => p.Coincidencias).ToList();
            var elegidas = ElegirClase(coincidencias);
            var distintas = elegidas.GroupBy(c => c.Id).Select(g => g.First()).ToList();

            return distintas.Count switch
            {
                1 => Tramo(paginaFinal, distintas[0].Clase, distintas[0].Id, distintas[0].Nombre, distintas[0].Motivo, distintas[0].Pagina),
                0 => Tramo(paginaFinal, null, null, null, Tipo is null ? MotivoDeteccion.SinCoincidencias : MotivoDeteccion.TipoSinTitular, null),
                _ => Tramo(paginaFinal, null, null, null, MotivoDeteccion.VariosTitulares, distintas[0].Pagina)
            };
        }

        /// <summary>
        /// El tipo reconocido dice de qué clase es el titular; sin tipo, lo más concreto gana, porque el CIF de una
        /// Empresa aparece también en los documentos de sus Trabajadores y Vehículos.
        /// </summary>
        private List<Coincidencia> ElegirClase(List<Coincidencia> coincidencias)
        {
            List<Coincidencia> De(params ClaseTitular[] clases) => coincidencias.Where(c => clases.Contains(c.Clase)).ToList();

            if (Tipo?.Ambito == AmbitoAplicacion.Trabajador) return PreferirDni(De(ClaseTitular.Trabajador));
            if (Tipo?.Ambito == AmbitoAplicacion.Vehiculo) return De(ClaseTitular.Vehiculo);
            if (Tipo?.Ambito == AmbitoAplicacion.Empresa) return De(ClaseTitular.EmpresaGestionada, ClaseTitular.Subcontrata);
            if (Tipo is not null) return [];

            var vehiculos = De(ClaseTitular.Vehiculo);
            if (vehiculos.Count > 0) return vehiculos;

            var trabajadores = PreferirDni(De(ClaseTitular.Trabajador));
            return trabajadores.Count > 0 ? trabajadores : De(ClaseTitular.EmpresaGestionada, ClaseTitular.Subcontrata);
        }

        private static List<Coincidencia> PreferirDni(List<Coincidencia> trabajadores)
        {
            var porDni = trabajadores.Where(c => c.Motivo == MotivoDeteccion.Dni).ToList();
            return porDni.Count > 0 ? porDni : trabajadores;
        }

        private TramoDetectado Tramo(
            int paginaFinal, ClaseTitular? clase, Guid? id, string? nombre, MotivoDeteccion motivo, int? pagina) =>
            new(paginaInicial, paginaFinal, Tipo?.Id, Tipo?.Nombre, clase, id, nombre, motivo, pagina);
    }

    private static List<List<PalabraPdf>> AgruparEnLineas(IReadOnlyList<PalabraPdf> palabras)
    {
        var lineas = new List<List<PalabraPdf>>();

        foreach (var palabra in palabras.OrderByDescending(p => p.CentroY).ThenBy(p => p.Izquierda))
        {
            var ultima = lineas.LastOrDefault();
            if (ultima is not null && Math.Abs(ultima[0].CentroY - palabra.CentroY) <= Math.Max(ultima[0].Alto, palabra.Alto) * 0.5)
                ultima.Add(palabra);
            else
                lineas.Add([palabra]);
        }

        foreach (var linea in lineas)
            linea.Sort((a, b) => a.Izquierda.CompareTo(b.Izquierda));

        return lineas;
    }

    private static double Mediana(IEnumerable<double> valores)
    {
        var ordenados = valores.OrderBy(v => v).ToList();
        return ordenados.Count == 0 ? 0 : ordenados[ordenados.Count / 2];
    }

    private static string[] Palabras(string texto) =>
        Normalizar(texto).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static string? Compactar(string? valor) =>
        valor is null ? null : Normalizar(valor).Replace(" ", string.Empty, StringComparison.Ordinal);

    /// <summary>Minúsculas, sin acentos, y cualquier signo convertido en un solo espacio.</summary>
    private static string Normalizar(string texto)
    {
        var descompuesto = texto.Normalize(NormalizationForm.FormD);
        var salida = new StringBuilder(descompuesto.Length);
        var enEspacio = true;

        foreach (var caracter in descompuesto)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(caracter) == UnicodeCategory.NonSpacingMark)
                continue;

            if (char.IsLetterOrDigit(caracter))
            {
                salida.Append(char.ToLowerInvariant(caracter));
                enEspacio = false;
            }
            else if (!enEspacio)
            {
                salida.Append(' ');
                enEspacio = true;
            }
        }

        return salida.ToString().TrimEnd();
    }

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digitos();

    /// <summary>«Página 2 de 5», ya normalizado: «pagina 2 de 5», «pag 2 de 5» o, tras quitar la barra, «pag 2 5».</summary>
    [GeneratedRegex(@"\bpag(?:ina)?\s+(\d{1,3})\s+(?:de\s+)?\d{1,3}\b", RegexOptions.None, 200)]
    private static partial Regex PaginaDeTotal();
}
