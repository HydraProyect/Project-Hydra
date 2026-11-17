using System.Globalization;
using CaeManager.Domain.Common;

namespace CaeManager.Web.Features.Inbound.Validacion;

/// <summary>Criterios de filtro de la cola. Un valor vacío (o <c>null</c>) no filtra.</summary>
public sealed record FiltrosCola(
    string Texto = "",
    string Requerimiento = "",
    EntidadValidable? Entidad = null,
    string Empresa = "",
    string Centro = "",
    ResultadoLectura? Lectura = null,
    TramoAntiguedad? Tramo = null)
{
    public bool Hay => !string.IsNullOrWhiteSpace(Texto) || Requerimiento != "" || Entidad is not null || Empresa != ""
        || Centro != "" || Lectura is not null || Tramo is not null;
}

/// <summary>
/// Reglas de presentación de la cola y de la validación que las dos pantallas comparten: el orden, la antigüedad, el texto
/// de la lectura y qué filas entran en un lote. Sin estado ni acceso a datos, para probarlas sin renderizar.
/// </summary>
public static class LogicaValidacionInbound
{
    private static readonly CultureInfo Es = new("es-ES");

    /// <summary>
    /// Orden de la cola (decisión de Chris, 2026-11-13): por requerimiento, para revisar seguidos todos los del mismo tipo;
    /// después por Empresa contratista y, dentro, lo que se subió antes.
    /// </summary>
    public static IReadOnlyList<DocumentoPorValidar> Ordenar(IEnumerable<DocumentoPorValidar> documentos) =>
        [.. documentos
            .OrderBy(d => d.Requerimiento, StringComparer.Create(Es, ignoreCase: true))
            .ThenBy(d => d.EmpresaContratista, StringComparer.Create(Es, ignoreCase: true))
            .ThenBy(d => d.SubidoUtc)
            .ThenBy(d => d.Id)];

    /// <summary>La hora de subida a TALVEG como la ve el Gestor CAE: hora peninsular, «15/11/2026 09:30».</summary>
    public static string TextoSubido(DateTime subidoUtc) =>
        DateTime.SpecifyKind(subidoUtc, DateTimeKind.Utc).EnHoraPeninsular().ToString("dd/MM/yyyy HH:mm", Es);

    public static string TextoFecha(DateOnly? fecha) => fecha?.ToString("dd/MM/yyyy", Es) ?? "—";

    public static string Subtitulo(AgrupacionCola agrupacion) => agrupacion switch
    {
        AgrupacionCola.Requerimiento => "Por requerimiento, después por Empresa contratista y por orden de subida",
        AgrupacionCola.EmpresaContratista => "Por Empresa contratista, después por requerimiento y por orden de subida",
        AgrupacionCola.Centro => "Por Centro, después por requerimiento, Empresa contratista y orden de subida",
        AgrupacionCola.Entidad => "Por Entidad, después por requerimiento, Empresa contratista y orden de subida",
        _ => "Sin agrupar: por requerimiento, Empresa contratista y orden de subida"
    };

    public static string Texto(AgrupacionCola agrupacion) => agrupacion switch
    {
        AgrupacionCola.Requerimiento => "Requerimiento",
        AgrupacionCola.EmpresaContratista => "Empresa contratista",
        AgrupacionCola.Centro => "Centro",
        AgrupacionCola.Entidad => "Entidad",
        _ => "Sin agrupar"
    };

    /// <summary>
    /// Parte la cola en grupos. El grupo manda en el orden; dentro de él se mantiene el orden de la cola (requerimiento,
    /// Empresa contratista, subida). Sin agrupar es un único grupo sin título.
    /// </summary>
    public static IReadOnlyList<(string Titulo, IReadOnlyList<DocumentoPorValidar> Documentos)> Agrupar(
        IEnumerable<DocumentoPorValidar> documentos, AgrupacionCola agrupacion)
    {
        var ordenados = Ordenar(documentos);
        if (agrupacion == AgrupacionCola.SinAgrupar)
            return ordenados.Count == 0 ? [] : [(string.Empty, ordenados)];

        string Clave(DocumentoPorValidar d) => agrupacion switch
        {
            AgrupacionCola.Requerimiento => d.Requerimiento,
            AgrupacionCola.EmpresaContratista => d.EmpresaContratista,
            AgrupacionCola.Centro => TextoCentros(d.Centros),
            _ => TextoPlural(d.Entidad)
        };

        return [.. ordenados
            .GroupBy(Clave)
            .OrderBy(g => g.Key, StringComparer.Create(Es, ignoreCase: true))
            .Select(g => (g.Key, (IReadOnlyList<DocumentoPorValidar>)[.. g]))];
    }

    public static TramoAntiguedad Tramo(DocumentoPorValidar documento, DateTimeOffset ahora)
    {
        var horas = (ahora - new DateTimeOffset(DateTime.SpecifyKind(documento.SubidoUtc, DateTimeKind.Utc))).TotalHours;
        return horas < 24 ? TramoAntiguedad.MenosDe24h : horas <= 72 ? TramoAntiguedad.UnoATresDias : TramoAntiguedad.MasDeTresDias;
    }

    public static string Texto(TramoAntiguedad tramo) => tramo switch
    {
        TramoAntiguedad.MenosDe24h => "Menos de 24 h",
        TramoAntiguedad.UnoATresDias => "1 a 3 días",
        _ => "Más de 3 días"
    };

    public static string Texto(EntidadValidable entidad) => entidad switch
    {
        EntidadValidable.Empresa => "Empresa",
        EntidadValidable.Trabajador => "Trabajador",
        _ => "Vehículo"
    };

    public static string TextoPlural(EntidadValidable entidad) => entidad switch
    {
        EntidadValidable.Empresa => "Empresa",
        EntidadValidable.Trabajador => "Trabajadores",
        _ => "Vehículos"
    };

    public static string TextoLecturaGeneral(ResultadoLectura resultado) => resultado switch
    {
        ResultadoLectura.Correcto => "Todo correcto",
        ResultadoLectura.ConError => "Con errores",
        _ => "Sin detectar"
    };

    public static string TextoIa(ResultadoLectura resultado) => resultado switch
    {
        ResultadoLectura.Correcto => "Correcto",
        ResultadoLectura.ConError => "Con error",
        _ => "No detectado"
    };

    /// <summary>La lectura de una fila de la cola: «✓ Todo correcto», «✕ 2 datos con error» o «! 1 sin detectar».</summary>
    public static string TextoLectura(DocumentoPorValidar documento) => documento.Lectura switch
    {
        ResultadoLectura.Correcto => "✓ Todo correcto",
        ResultadoLectura.ConError => "✕ " + (documento.DatosAfectados == 1 ? "1 dato con error" : documento.DatosAfectados + " datos con error"),
        // Sin ningún dato leído, la lectura con IA no corrió: no hay «0 sin detectar», hay «sin lectura».
        _ when documento.DatosAfectados == 0 => "! Sin lectura con IA",
        _ => "! " + documento.DatosAfectados + " sin detectar"
    };

    public static string ClaseResultado(ResultadoLectura resultado) => resultado switch
    {
        ResultadoLectura.Correcto => "ok",
        ResultadoLectura.ConError => "error",
        _ => "aviso"
    };

    /// <summary>«Planta Zaragoza Norte» o, con más de uno, «Los 2 Centros».</summary>
    public static string TextoCentros(IReadOnlyList<string> centros) => centros.Count switch
    {
        0 => "—",
        1 => centros[0],
        _ => "Los " + centros.Count + " Centros"
    };

    public static string TextoCentrosLargo(IReadOnlyList<string> centros) =>
        centros.Count == 0 ? "—" : string.Join(" y ", centros.Count > 2 ? [string.Join(", ", centros.Take(centros.Count - 1)), centros[^1]] : centros);

    /// <summary>
    /// Por qué una fila no entra en un lote, o <c>null</c> si entra (R-2): solo entra lo que sale «Todo correcto» en la lectura
    /// y no es de un tipo excluido. Un tipo excluido se abre siempre, aunque la lectura sea correcta.
    /// </summary>
    public static string? MotivoNoLote(DocumentoPorValidar documento)
    {
        if (!string.IsNullOrWhiteSpace(documento.ExclusionLote))
            return $"No se valida en lote por su tipo de documento ({documento.ExclusionLote}), aunque la lectura sea «Todo correcto». Se abre siempre el documento.";
        if (documento.Lectura != ResultadoLectura.Correcto)
            return "Solo se valida en lote lo que sale «Todo correcto» en la lectura con IA.";
        if (documento.Propio)
            return "No puedes validarlo: lo subiste tú en nombre de la Empresa contratista.";
        return null;
    }

    /// <summary>1 de cada N para revisión, redondeando hacia arriba.</summary>
    public static int MuestraParaRevision(int seleccionados, int unoDeCada) =>
        seleccionados <= 0 ? 0 : (int)Math.Ceiling(seleccionados / (double)Math.Max(1, unoDeCada));

    public static bool Pasa(DocumentoPorValidar documento, FiltrosCola filtros, DateTimeOffset ahora, bool conTramo = true)
    {
        if (!string.IsNullOrWhiteSpace(filtros.Texto)
            && !Contiene($"{documento.Requerimiento} {documento.Titular} {documento.Identificador} {documento.EmpresaContratista}", filtros.Texto.Trim()))
            return false;
        if (filtros.Requerimiento != "" && documento.Requerimiento != filtros.Requerimiento)
            return false;
        if (filtros.Entidad is { } entidad && documento.Entidad != entidad)
            return false;
        if (filtros.Empresa != "" && documento.EmpresaContratista != filtros.Empresa)
            return false;
        if (filtros.Centro != "" && !documento.Centros.Contains(filtros.Centro))
            return false;
        if (filtros.Lectura is { } lectura && documento.Lectura != lectura)
            return false;
        if (conTramo && filtros.Tramo is { } tramo && Tramo(documento, ahora) != tramo)
            return false;
        return true;
    }

    /// <summary>Coincidencia sin mayúsculas ni tildes, como el buscador de las pastillas de la maqueta.</summary>
    public static bool Contiene(string texto, string buscado) =>
        Es.CompareInfo.IndexOf(texto, buscado, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;

    /// <summary>El peor resultado de una lista de datos leídos: con error si alguno lo es; si no, sin detectar si alguno lo es.</summary>
    public static ResultadoLectura Peor(IEnumerable<ResultadoLectura> resultados)
    {
        var lista = resultados.ToList();
        // Sin ninguna lectura (la IA no corrió o falló) no hay nada «correcto»: se trata como sin detectar, y pide confirmación.
        if (lista.Count == 0) return ResultadoLectura.SinDetectar;
        return lista.Contains(ResultadoLectura.ConError) ? ResultadoLectura.ConError
            : lista.Contains(ResultadoLectura.SinDetectar) ? ResultadoLectura.SinDetectar
            : ResultadoLectura.Correcto;
    }

    public static string TextoVigencia(int? meses) => meses switch
    {
        null => string.Empty,
        1 => "1 mes",
        12 => "1 año",
        { } n when n % 12 == 0 => n / 12 + " años",
        { } n => n + " meses"
    };

    /// <summary>Suma meses sin salirse del mes (31 de enero más un mes, 28 o 29 de febrero).</summary>
    public static DateOnly SumarMeses(DateOnly fecha, int meses) => fecha.AddMonths(meses);

    public static string TextoVigenciaEstado(EstadoVigenciaValidacion estado) => estado switch
    {
        EstadoVigenciaValidacion.Proximo => "Próximo",
        EstadoVigenciaValidacion.Urgente => "Urgente",
        EstadoVigenciaValidacion.Vencido => "Vencido",
        _ => "Vigente"
    };
}
