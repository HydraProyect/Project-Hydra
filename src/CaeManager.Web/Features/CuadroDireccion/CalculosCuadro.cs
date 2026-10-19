using System.Text;
using System.Text.Json;

namespace CaeManager.Web.Features.CuadroDireccion;

/// <summary>La franja de cuatro horas seguidas que concentra más solicitudes y los días en que más pesa.</summary>
public sealed record FranjaRefuerzo(int HoraInicio, int HoraFin, double PesoPorcentaje, IReadOnlyList<int> DiasPico);

/// <summary>Un día laborable del calendario de gestiones; <paramref name="EnRango"/> es falso fuera del periodo pedido.</summary>
public sealed record CeldaDia(DateOnly Dia, int Cantidad, bool EnRango);

/// <summary>Una Empresa en la concentración de la facturación: su peso y el acumulado hasta ella.</summary>
public sealed record ItemPareto(string Nombre, decimal Importe, double Porcentaje, double Acumulado);

/// <summary>
/// Cálculos puros del Cuadro de dirección que la pantalla usa sobre las cifras ya medidas. Todo es del Operador
/// CAE o de una Empresa (Tenant beneficiario): ninguna cuenta mide a una persona.
/// </summary>
public static class CalculosCuadro
{
    /// <summary>
    /// «Si hubiera que reforzar a media jornada»: la ventana de <paramref name="ancho"/> horas seguidas con más peso
    /// sobre las solicitudes de lunes a viernes, y los dos días en que más pesa. Null sin solicitudes.
    /// </summary>
    public static FranjaRefuerzo? FranjaDeRefuerzo(IReadOnlyList<CeldaSolicitudes> celdas, int ancho = 4)
    {
        if (celdas.Count == 0) return null;

        var minimo = celdas.Min(c => c.Hora);
        var maximo = celdas.Max(c => c.Hora);
        var mejorInicio = minimo;
        var mejorPeso = -1.0;
        for (var inicio = minimo; inicio <= Math.Max(minimo, maximo - ancho + 1); inicio++)
        {
            var peso = celdas.Where(c => c.Hora >= inicio && c.Hora < inicio + ancho).Sum(c => c.PesoPorcentaje);
            if (peso > mejorPeso)
            {
                mejorPeso = peso;
                mejorInicio = inicio;
            }
        }

        var dias = celdas
            .Where(c => c.Hora >= mejorInicio && c.Hora < mejorInicio + ancho)
            .GroupBy(c => c.Dia)
            .Select(g => (Dia: g.Key, Peso: g.Sum(c => c.PesoPorcentaje)))
            .OrderByDescending(d => d.Peso)
            .Take(2)
            .Select(d => d.Dia)
            .OrderBy(d => d)
            .ToList();

        return new FranjaRefuerzo(mejorInicio, mejorInicio + ancho, mejorPeso, dias);
    }

    /// <summary>
    /// Rotación del mes: altas y bajas de Trabajadores frente a la plantilla media del mes (la de al inicio y la del
    /// corte). Null si no hubo plantilla.
    /// </summary>
    public static double? Rotacion(int altas, int bajas, int activosAlCorte)
    {
        var alInicio = activosAlCorte - altas + bajas;
        var media = (alInicio + activosAlCorte) / 2.0;
        return media <= 0 ? null : (altas + bajas) * 100.0 / media;
    }

    /// <summary>Los índices de los <paramref name="cuantos"/> valores mayores (solo los que tienen algo), por orden de posición.</summary>
    public static IReadOnlySet<int> IndicesPico(IReadOnlyList<double?> valores, int cuantos) =>
        valores
            .Select((v, i) => (V: v, I: i))
            .Where(x => x.V is > 0)
            .OrderByDescending(x => x.V)
            .Take(cuantos)
            .Select(x => x.I)
            .ToHashSet();

    /// <summary>Importe estimado por gestión; null sin gestiones (no se divide por cero ni se inventa).</summary>
    public static double? IngresoPorGestion(decimal importe, int? gestiones) =>
        gestiones is > 0 ? (double)(importe / gestiones.Value) : null;

    /// <summary>Días de lunes a viernes del mes natural (sin descontar festivos).</summary>
    public static int DiasLaborables(int anyo, int mes)
    {
        var dias = DateTime.DaysInMonth(anyo, mes);
        return Enumerable.Range(1, dias).Count(d => new DateOnly(anyo, mes, d).DayOfWeek is >= DayOfWeek.Monday and <= DayOfWeek.Friday);
    }

    /// <summary>
    /// «Meses de más y menos carga»: gestiones por día laborable según el mes del año, promediando los años con el
    /// mes completo. Posición 0 = enero; null donde no hay ningún mes completo.
    /// </summary>
    public static IReadOnlyList<double?> GestionesPorDiaLaborableSegunMes(IEnumerable<(int Anyo, int Mes, int? Gestiones)> mesesCompletos)
    {
        var datos = mesesCompletos.Where(m => m.Gestiones is not null).ToList();
        return Enumerable.Range(1, 12)
            .Select(mes =>
            {
                var delMes = datos.Where(d => d.Mes == mes).ToList();
                return delMes.Count == 0
                    ? (double?)null
                    : delMes.Average(d => d.Gestiones!.Value / (double)DiasLaborables(d.Anyo, d.Mes));
            })
            .ToList();
    }

    /// <summary>Concentración: cada Empresa con su peso sobre el total y el acumulado, de mayor a menor. Sin importe, vacío.</summary>
    public static IReadOnlyList<ItemPareto> Pareto(IEnumerable<(string Nombre, decimal Importe)> importes)
    {
        var ordenados = importes.Where(i => i.Importe > 0).OrderByDescending(i => i.Importe).ThenBy(i => i.Nombre, StringComparer.CurrentCulture).ToList();
        var total = ordenados.Sum(i => i.Importe);
        if (total <= 0) return [];

        var acumulado = 0m;
        var resultado = new List<ItemPareto>();
        foreach (var (nombre, importe) in ordenados)
        {
            acumulado += importe;
            resultado.Add(new ItemPareto(nombre, importe, (double)(importe * 100 / total), (double)(acumulado * 100 / total)));
        }

        return resultado;
    }

    /// <summary>
    /// «Gestiones por día»: las semanas de lunes a viernes entre dos fechas (ambas incluidas), cada día con sus
    /// gestiones (0 si ninguna). Las semanas empiezan en el lunes de la fecha inicial.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<CeldaDia>> Semanas(DateOnly desde, DateOnly hasta, IReadOnlyDictionary<DateOnly, int> porDia)
    {
        var lunes = desde.AddDays(-(((int)desde.DayOfWeek + 6) % 7));
        var semanas = new List<IReadOnlyList<CeldaDia>>();
        for (var inicio = lunes; inicio <= hasta; inicio = inicio.AddDays(7))
        {
            semanas.Add(Enumerable.Range(0, 5)
                .Select(i => inicio.AddDays(i))
                .Select(d => new CeldaDia(d, d >= desde && d <= hasta ? porDia.GetValueOrDefault(d) : 0, d >= desde && d <= hasta))
                .ToList());
        }

        return semanas;
    }

    /// <summary>
    /// Índice de cartera por Gestor CAE (maqueta): media de las proporciones de trabajadores, centros y gestiones de cada
    /// cartera frente a la media de todas, por cien (100 = media). Mide el volumen de la cartera, no el rendimiento de nadie.
    /// Una magnitud sin dato en alguna cartera (gestiones de un cierre antiguo) o con media cero no entra en la media de
    /// proporciones; si no queda ninguna, el índice es null.
    /// </summary>
    public static IReadOnlyList<int?> IndiceDeCartera(IReadOnlyList<(int Trabajadores, int Centros, int? Gestiones)> carteras)
    {
        if (carteras.Count == 0) return [];

        var magnitudes = new List<(Func<(int Trabajadores, int Centros, int? Gestiones), double?> Valor, double Media)>();
        void Anadir(Func<(int Trabajadores, int Centros, int? Gestiones), double?> valor)
        {
            if (carteras.Any(c => valor(c) is null)) return;
            var media = carteras.Average(c => valor(c)!.Value);
            if (media > 0) magnitudes.Add((valor, media));
        }

        Anadir(c => c.Trabajadores);
        Anadir(c => c.Centros);
        Anadir(c => c.Gestiones);

        return carteras
            .Select(c => magnitudes.Count == 0
                ? (int?)null
                : (int)Math.Round(magnitudes.Average(m => m.Valor(c)!.Value / m.Media) * 100, MidpointRounding.AwayFromZero))
            .ToList();
    }

    /// <summary>Variación porcentual frente a un valor previo; null si el previo es cero.</summary>
    public static double? Variacion(double actual, double previo) => previo == 0 ? null : (actual - previo) * 100 / previo;

    /// <summary>CSV de una tabla visible: separador «;» (el que abre Excel en castellano), comillas dobles solo si hacen falta.</summary>
    public static string Csv(IEnumerable<IReadOnlyList<string>> filas)
    {
        // Un campo que empieza por = + - @ (o por tabulador o retorno) lo interpreta la hoja de cálculo como fórmula:
        // se antepone un apóstrofo para que se abra como texto.
        static string Campo(string valor)
        {
            if (valor.Length > 0 && valor[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
                valor = "'" + valor;
            return valor.IndexOfAny([';', '"', '\n', '\r']) >= 0 ? "\"" + valor.Replace("\"", "\"\"") + "\"" : valor;
        }

        var texto = new StringBuilder();
        foreach (var fila in filas)
            texto.Append(string.Join(';', fila.Select(Campo))).Append("\r\n");
        return texto.ToString();
    }
}

/// <summary>
/// Preferencias de la pantalla que se recuerdan en este navegador: tarjetas ocultas, orden de las tarjetas de cada
/// pestaña, etiquetas de datos y avisos de cierre ya vistos. Es presentación, no estado de negocio: leerla mal
/// (JSON roto, tarjetas que ya no existen) deja los valores por defecto.
/// </summary>
public sealed class PreferenciasCuadro
{
    public List<string> Ocultas { get; set; } = [];
    public Dictionary<string, List<string>> Orden { get; set; } = [];
    public bool Etiquetas { get; set; }
    public List<string> AvisosVistos { get; set; } = [];

    public static PreferenciasCuadro Leer(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new PreferenciasCuadro();
        try
        {
            var leidas = JsonSerializer.Deserialize<PreferenciasCuadro>(json) ?? new PreferenciasCuadro();

            // Un JSON válido puede traer propiedades o elementos a null ({"Ocultas":null}): se normaliza en vez de fallar luego.
            leidas.Ocultas = leidas.Ocultas?.Where(id => id is not null).ToList() ?? [];
            leidas.AvisosVistos = leidas.AvisosVistos?.Where(id => id is not null).ToList() ?? [];
            leidas.Orden = (leidas.Orden ?? [])
                .Where(par => par.Key is not null && par.Value is not null)
                .ToDictionary(par => par.Key, par => par.Value.Where(id => id is not null).ToList());
            return leidas;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
        {
            return new PreferenciasCuadro();
        }
    }

    public string Serializar() => JsonSerializer.Serialize(this);

    public bool EsVisible(string id) => !Ocultas.Contains(id);

    public void Alternar(string id)
    {
        if (!Ocultas.Remove(id)) Ocultas.Add(id);
    }

    /// <summary>El orden guardado de una pestaña aplicado a sus tarjetas: las que ya no existen se descartan y las nuevas van al final.</summary>
    public IReadOnlyList<string> Ordenar(string pestana, IReadOnlyList<string> porDefecto)
    {
        if (!Orden.TryGetValue(pestana, out var guardado)) return porDefecto;
        return guardado.Where(porDefecto.Contains).Distinct().Concat(porDefecto.Where(id => !guardado.Contains(id))).ToList();
    }

    /// <summary>Mueve una tarjeta <paramref name="delta"/> posiciones (negativo = hacia arriba) dentro del orden de su pestaña.</summary>
    public void Mover(string pestana, IReadOnlyList<string> porDefecto, string id, int delta)
    {
        var actual = Ordenar(pestana, porDefecto).ToList();
        var origen = actual.IndexOf(id);
        if (origen < 0) return;
        var destino = Math.Clamp(origen + delta, 0, actual.Count - 1);
        if (destino == origen) return;
        actual.RemoveAt(origen);
        actual.Insert(destino, id);
        Orden[pestana] = actual;
    }

    /// <summary>Lleva una tarjeta a la posición de otra (arrastrar y soltar).</summary>
    public void Colocar(string pestana, IReadOnlyList<string> porDefecto, string id, string antesDe)
    {
        var actual = Ordenar(pestana, porDefecto).ToList();
        if (id == antesDe || !actual.Remove(id)) return;
        var destino = actual.IndexOf(antesDe);
        actual.Insert(destino < 0 ? actual.Count : destino, id);
        Orden[pestana] = actual;
    }

    public void Restablecer(string pestana, IEnumerable<string> ids)
    {
        Orden.Remove(pestana);
        Ocultas.RemoveAll(ids.Contains);
    }
}
